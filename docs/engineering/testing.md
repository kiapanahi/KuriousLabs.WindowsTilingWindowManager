# Testing Strategy & Toolchain

This document operationalizes [DESIGN.md §11](../../DESIGN.md#11-testing-strategy)'s five test
tiers on the 2026 .NET testing stack. It is the single authority for *how* Bastion's tests are
built, run, and gated in CI. It does not own interop mechanics (see
[interop.md](interop.md)), event-pipeline/concurrency mechanics (see
[concurrency-performance.md](concurrency-performance.md)), daemon hosting/lifecycle (see
[daemon-architecture.md](daemon-architecture.md)), or repo-wide build/analyzer configuration (see
[quality-gates.md](quality-gates.md)) — cross-reference those for anything outside "which test
tier, which framework, which CI job."

---

## 1. Test platform: xUnit v3 on Microsoft.Testing.Platform (MTP)

All five tiers run on **xUnit v3**. xUnit v3 has Microsoft.Testing.Platform (MTP) support built
natively into the test framework, so `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` are
not referenced once every dev machine and CI leg runs MTP.

**Pin the MTP v2 line via `xunit.v3.mtp-v2`, not the plain `xunit.v3` package.** Starting with xUnit
v3 build 3.2.0 you choose an MTP major version at the package level: the plain `xunit.v3` meta-package
unconditionally depends on `xunit.v3.mtp-v1` — its own nuspec description literally states
"Installing this package installs xunit.v3.mtp-v1," with no MSBuild property or TFM-conditional group
to switch it. This was originally documented here as a deliberate "stay on v1, it's the default and
Bastion has no migration debt" choice, but that guidance was wrong in practice for this repo:
`Microsoft.Testing.Extensions.CodeCoverage` hard-depends on `Microsoft.Testing.Platform >= 2.x` in
every 18.x release back through 18.4.1 (confirmed via each version's nuspec) — there is no
CodeCoverage version that stays compatible with the MTP v1 line. Referencing plain `xunit.v3`
alongside any pinned `Microsoft.Testing.Extensions.CodeCoverage` 18.x therefore throws, in sequence,
`TypeLoadException` on `IDataConsumer` (moved namespace between v1/v2), then `TypeLoadException` on
`Microsoft.Testing.Extensions.Telemetry.AppInsightsProvider.LogEventAsync`, then
`MissingMethodException` on `IOutputDevice.DisplayAsync` (gained a `CancellationToken` parameter in
v2) — confirmed by direct reproduction, and consistent with
[Microsoft's MTP v1→v2 migration guide](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-migration-from-v1-to-v2).
Reference `xunit.v3.mtp-v2` explicitly instead — it depends on `xunit.v3.core.mtp-v2` +
`xunit.v3.assert` + `xunit.analyzers` and resolves cleanly against a 2.3.x-pinned
`Microsoft.Testing.Platform`/`.MSBuild`/`Extensions.Telemetry`/`Extensions.TrxReport.Abstractions`:

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="xunit.v3.mtp-v2" Version="[pin exact 3.x.y — do not float]" />
<PackageVersion Include="FsCheck.Xunit.v3" Version="[pin]" />
<PackageVersion Include="Microsoft.Testing.Extensions.CodeCoverage" Version="[pin]" />
<PackageVersion Include="Microsoft.Testing.Platform" Version="[pin — same 2.3.x build as below]" />
<PackageVersion Include="Microsoft.Testing.Platform.MSBuild" Version="[pin — same build]" />
<PackageVersion Include="Microsoft.Testing.Extensions.Telemetry" Version="[pin — same build]" />
<PackageVersion Include="Microsoft.Testing.Extensions.TrxReport.Abstractions" Version="[pin — same build]" />
```

```xml
<!-- each test project's .csproj -->
<PackageReference Include="xunit.v3.mtp-v2" />
```

`FsCheck.Xunit.v3` depends only on the MTP-variant-agnostic `xunit.v3.extensibility.core`, so it does
not pull `mtp-v1` back in transitively — confirmed via its nuspec. Verify with
`dotnet nuget why <project> Microsoft.Testing.Platform` before trusting a build: a clean graph shows
every consumer resolving to the same single version with no duplicate `xunit.v3.core.mtp-v1` /
`xunit.v3.core.mtp-v2` entries side by side.

**Enable native MTP `dotnet test` via `global.json`**, not the VSTest-bridge mode, and opt every
test project into MTP via `Directory.Build.props`:

```json
{
  "sdk": { "version": "10.0.100", "rollForward": "patch" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
</PropertyGroup>
```

Per Microsoft's official VSTest→MTP migration guidance, both properties are required for xUnit.net
v3 test projects to opt into MTP; `xunit.v3.core` (since release 1.1.0) emits a build error if
`OutputType=Exe` is missing. This mode requires the .NET 10 SDK and MTP 1.7+, drops the legacy `--`
separator before test-runner arguments, and unlocks the native globbing/query-filter language used
in §2.

**Forbidden: mixing VSTest-based and MTP-based test projects in one solution or CI run.**
Microsoft's own docs call this combination unsupported — VSTest-specific CLI options are silently
ignored or misapplied against MTP projects, and the two hosts have different discovery/execution
protocols. Since Bastion.Core/Bastion.Layout run on Linux CI (Tier 1) and Bastion.Win32/Daemon run
on Windows CI (Tiers 2–5), verify **every** test project in the solution targets MTP before wiring
the workflow matrix — a single VSTest holdout test project breaks the invariant for the whole
solution-level `dotnet test` invocation.

**MTP zero-tests behavior: exit code 8 means failure.** Unlike VSTest's tolerant "no tests found is
fine" default, MTP treats zero discovered tests in an assembly as a hard failure (exit code 8).
This is a real risk for Tier 3 (`windows-latest`-only, quarantined) if a filter or a platform-gated
`#if WINDOWS` leaves zero runnable tests on a given leg — e.g. a Linux CI matrix leg that happens to
also enumerate a Windows-only test assembly. Mitigate with either a per-leg placeholder
`[Fact]` that always passes (documenting *why* it exists) or an explicit CI step that skips the
`dotnet test` invocation entirely for legs with no applicable project, rather than letting a
zero-tests exit silently fail a job that "should" have been a no-op.

---

## 2. Tier filtering: xUnit v3 query-filter language

Use xUnit v3's structured query-filter language to split CI jobs by tier/trait — **never** the
legacy VSTest `--filter TestCategory=X` syntax:

```
dotnet test --filter-query "/[Category!=Quarantined]"   # main gate: Tier 1/2 + non-quarantined
dotnet test --filter-query "/[Category=Quarantined]"     # Tier 3: allowed-to-fail job
dotnet test --filter-query "/[Category=Canary]"          # Tier 5: behavior canaries
```

The query language supports `/<assembly>/<namespace>/<class>/<method>` paths combined with
`&`/`|`/`!=` and wildcards — it is the v3-native replacement for the old trait-filter shorthand.

**Forbidden: `dotnet test --filter TestCategory=X` (or any VSTest `--filter` trait syntax) against
xUnit v3.** Microsoft's official VSTest→MTP migration docs state this syntax is unsupported against
xUnit v3/MTP projects — the two hosts have different discovery/execution protocols and VSTest CLI
options are silently ignored or misapplied. (xunit/xunit#3030, once cited here as a live crash, was
filed against a pre-release `xunit.v3` 0.3.0-pre.18 package on the .NET 9 RC1 SDK and was opened and
closed the same day in September 2024 — it is historical, pre-GA, and already closed; the current
justification is the documented xUnit v3/MTP filter-syntax incompatibility, not an open bug.) This
is not a style preference. Use the query-filter language exclusively.

**Traits and CI job wiring:**

```csharp
[Trait("Category", "Quarantined")]
public class RealWindowIntegrationTests { /* Tier 3 */ }

[Trait("Category", "Canary")]
public class UipiBehaviorCanaryTests { /* Tier 5 */ }
```

Give the Tier 3 job `continue-on-error: true` in the CI workflow so real-window flakiness never
blocks a Tier 1/2 merge gate — this is what DESIGN.md §11 means by "quarantined so it can't erode
trust." Tier 5 canaries are a separate, non-blocking early-warning job (§7) — they open tracked
issues on failure rather than gating merges, since their entire purpose is detecting *upstream*
Windows servicing changes, not regressions in this repo.

---

## 3. Tier 1 — property-based testing of Layout/Core

**Library choice: FsCheck by default.** Use `FsCheck.Xunit.v3`'s `[Property]` attribute for Layout
Engine and Core invariants — it is the most mature .NET property-based testing library and
integrates with the existing xUnit v3 harness with no custom runner glue. (The plain `FsCheck.Xunit`
package is built against xUnit v2's `FactAttribute`/extensibility API and does not work with xUnit
v3; `FsCheck.Xunit.v3`, available since FsCheck 3.3.0, is the xUnit-v3-compatible package.)

**Documented alternative: CsCheck.** If shrink quality on composed generators (a split-tree of
nested horizontal/vertical split nodes, each with float ratios and leaf stacks) proves weak in
practice with FsCheck, or when stateful/model-based property tests arrive for the Reconciler's
convergence under concurrent intents, switch that suite to CsCheck — its own comparison
documentation claims stronger automatic shrinking of composed generator types and native
stateful/concurrent-testing support. **Decide per-generator empirically** (compare shrunk
counterexample quality on a real failing case), not by default preference — record the decision
and its evidence in the test project's README when made.

**Generate over the input space, never over rendered output.** Generators produce
`(tree, workArea, constraints, gaps)` — the *inputs* to `Layout` — never post-hoc pixel-output
fixtures. Given `Layout(tree, workArea, constraints, gaps) → [(WindowId, RECT visibleBounds)]` is a
pure, Win32-free function (DESIGN.md §3.5/§6), this runs unmodified on Linux CI with no adapter or
fake required — it is Tier 1's entire value proposition.

Core invariants, each a first-class `[Property]`:

```csharp
[Property]
public Property NoOverlap(SplitTree tree, Rect workArea, Constraints c, Gaps g)
{
    var rects = Layout.Solve(tree, workArea, c, g).Select(r => r.Bounds).ToList();
    return (from i in Enumerable.Range(0, rects.Count)
            from j in Enumerable.Range(i + 1, rects.Count - i - 1)
            select !rects[i].IntersectsWith(rects[j]))
           .Aggregate((a, b) => a & b).ToProperty();
}
```

- **No-overlap**: pairwise leaf-rect intersection is empty.
- **Full coverage**: the union of leaf rects equals `workArea` minus configured gaps — no dead
  space, no double-covered space.
- **Min-size respect**: no leaf rect is smaller than its constraint-cache minimum.
- **Determinism**: identical inputs produce byte-identical rects across repeated runs (no reliance
  on set/dictionary enumeration order, no `GetHashCode`-derived ordering).

**Subtree-locality as a metamorphic property** — the highest-value test in this tier, per
DESIGN.md §3.5's stability requirement:

```csharp
[Property]
public Property InsertPerturbsOnlyAffectedSubtree(SplitTree tree, Rect workArea, Leaf newLeaf)
{
    var before = Layout.Solve(tree, workArea).ToDictionary(r => r.WindowId, r => r.Bounds);
    var after = Layout.Solve(tree.Insert(newLeaf), workArea)
                       .Where(r => r.WindowId != newLeaf.WindowId)
                       .ToDictionary(r => r.WindowId, r => r.Bounds);
    return before.Keys.All(id => before[id] == after[id]).ToProperty();
}
```

Generate a tree, run `Layout`, insert or remove one leaf, run `Layout` again, and assert every
*other* leaf's rect is byte-identical across both runs. This directly encodes DESIGN.md §3.5's
"single insert/remove perturbs only the affected subtree" invariant and is a relational
(metamorphic) property both FsCheck and CsCheck support by combining two generator-driven runs of
related inputs.

---

## 4. Deterministic time in tests

Inject `TimeProvider` everywhere the Reconciler/Coalescer read wall-clock time — the 75 ms
coalescing window, 150 ms admission grace, 500 ms display-settle debounce, and 5 s heartbeat
(DESIGN.md §3.2/§3.4) — and never call `DateTime.Now`/`Task.Delay` directly in code under test.
Concurrency/threading ownership of these values (channel wiring, dedicated-thread vs. task-pool
execution) is owned by [concurrency-performance.md](concurrency-performance.md); this section
covers only how tests drive them deterministically.

Use `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider`
(`Microsoft.Extensions.Time.Testing` namespace):

```csharp
var time = new FakeTimeProvider(); // starts 2000-01-01 UTC by default
var coalescer = new Coalescer(time, coalesceWindow: TimeSpan.FromMilliseconds(75));

coalescer.OnEvent(intent);
time.Advance(TimeSpan.FromMilliseconds(75));

Assert.True(coalescer.TryDequeue(out _));
```

`FakeTimeProvider` is `TimeProvider`-substitutable in any constructor accepting the abstract base
type — no test-framework coupling beyond the constructor parameter.

**Documented gotcha: clear the synchronization context before advancing.** When a test awaits work
gated on the fake provider's timers, call `SynchronizationContext.SetSynchronizationContext(null)`
before `Advance(...)`, or the awaited continuation may not observe the timer callback
synchronously — the package's own documentation calls this out explicitly. This is the difference
between a coalescing test that reliably completes after one `Advance` call and one that
intermittently hangs or times out.

---

## 5. Tier 2 — fake adapter & replay

**The fake implements the same adapter-facing interface production code compiles against** — the
seam sits at the adapter boundary (an `IWindowSystem`-shaped interface), *above* CsWin32/COM, not
inside a COM shim. Because Bastion's core already isolates state-bearing code behind an opaque
`WindowId` with no HWND (DESIGN.md §3, §10), the fake adapter touches zero interop types. This also
sidesteps a real, reported CsWin32 limitation where generated COM interfaces are not always
`partial`/re-wrappable for `[GeneratedComInterface]` — a friction Tier 2 never needs to hit because
the fake sits above that layer entirely. (Interop-specific CsWin32/COM guidance lives in
[interop.md](interop.md); Tier 2 deliberately never needs it.)

**Replay is a deterministic driver, not a mock-heavy unit test.** It feeds recorded
`(WindowId-analog, event, dwmsEventTime)` tuples — the same shape DESIGN.md §3.4's
`bastion debug record` trace format captures — through the **real** ingest → coalesce → reconcile
pipeline, and asserts against the fake's captured outbound calls (`SetWindowPos`-equivalents), never
against real HWNDs:

```csharp
var fake = new FakeWindowSystem();
var pipeline = Pipeline.Create(fake, time); // same composition as production, minus the adapter impl

await ReplayDriver.RunAsync(pipeline, TraceFile.Load("field-report-2026-07-14.trace"));

fake.CapturedPlacements.Should().ContainSingle(p => p.WindowId == expectedId && p.Rect == expectedRect);
```

**Every field trace becomes a permanent regression test.** Per DESIGN.md §3.4/§11, `bastion debug
record` produces a redacted trace file from a live user's report; `bastion debug replay` replays it
deterministically through the fake. Check every such trace into the test project once triaged —
this tier is explicitly called out in DESIGN.md §11 as carrying "the main regression burden," and
that burden is discharged one committed trace file at a time, not by hand-written synthetic replay
scenarios alone (synthetic storm/reentrancy scenarios remain valuable for coverage of paths no field
report has hit yet, but they are supplementary to the trace corpus).

---

## 6. Snapshot testing with Verify

Use [Verify](https://github.com/VerifyTests/Verify) (`VerifyTests.Verify` + `Verify.XunitV3` — not
the deprecated, xUnit-v2-only `Verify.Xunit`) for
whole-solution snapshot assertions of Layout Engine rect outputs and Reconciler placement plans —
diffing an entire `[(WindowId, RECT)]` solution against a committed `*.verified.txt` file catches
unintended shifts across every leaf at once, rather than requiring a per-rect assert for each leaf
on every test.

```csharp
[Fact]
public Task DwindleLayout_ThreeWindows()
{
    var solution = Layout.Solve(tree, workArea);
    return Verify(solution)
        .AddScrubber(sb => sb.Replace(actualWindowId.ToString(), "<WindowId>"));
}
```

**Register explicit scrubbers/converters for `RECT` and `WindowId` DTOs.** Verify's built-in
scrubbing only auto-normalizes GUIDs, `DateTime`, and file paths by default — it does **not**
know how to normalize a `RECT` struct or a Bastion `WindowId` value out of the box. If `WindowId` is
backed by a non-deterministic value (a GUID, a monotonically-incrementing counter seeded
differently per test run, etc.), register a converter or scrubber for it explicitly; otherwise every
run produces a spurious diff against the committed snapshot even when the layout itself is
unchanged. Prefer a `VerifierSettings`-level global converter registered once in a test-project
module initializer over per-test scrubber calls, to avoid the registration being forgotten on new
snapshot tests.

**Licensing note — not currently actionable, revisit if it becomes relevant.** From August 2026,
commercial/government users of Verify's official binaries are asked to pay a small subscription;
source remains open, and CI runs, forks, non-revenue organizations, and individuals are explicitly
unaffected. Bastion is OSS with no commercial arrangement today, so this does not change anything —
flag it for re-review only if a commercial fork or paid-support arrangement around the project ever
forms.

---

## 7. CI runner realities (Tier 3 / Tier 5)

**`windows-latest` hosted runners do run an interactive desktop session** — they are not Session 0
services. This makes Tier 3's `CreateWindowExW`-spawned test windows (via `Bastion.TestWindows`,
never Notepad — DESIGN.md §11) and `DWMWA_EXTENDED_FRAME_BOUNDS` readback assertions genuinely
CI-viable: the windows render on a real, capturable desktop, the same reason a documented
`actions/runner` bug report shows the runner-provisioner console window visible in uploaded
screenshots from hosted-runner UI test failures.

**But `windows-latest` is Windows Server 2025 (build 26100), not Windows 11 25H2 (build 26200).**
DESIGN.md's target platform, and the entire premise of Tier 5's behavior canaries, is "Windows 11
25H2, build 26200" specifically (DESIGN.md §1, §3.6). Running Tier 5 on `windows-latest` therefore
validates behavior on a *different, Server-flavored OS build* — a weaker proxy, not a substitute for
the stated goal. Treat green Tier 5 runs on `windows-latest` as "no regression against Server 2025
26100," and track the gap to genuine 26200 confidence explicitly (open issue / roadmap item), rather
than reporting Tier 5 as validating the design's stated target.

**True build-26200 coverage requires either a self-hosted Windows 11 25H2 runner or the already-
planned Tier 4 Windows Sandbox leg** (`.wsb`, local/self-hosted only per DESIGN.md §11). Until a
self-hosted 25H2-pinned runner exists, Tier 5's hosted-CI run is a continuous early-warning signal
against the *nearest available* Windows build, and the Tier 4 Sandbox smoke test (run locally or on
a self-hosted 25H2 host) is the closest thing to genuine 26200 validation the project currently has.

**Forbidden: installing a self-hosted Windows runner intended to drive Tier 3/5 visible windows as a
Windows service.** A runner installed as a service runs in Session 0, which has no visible desktop —
`CreateWindowExW`-spawned windows are created successfully (they exist, they have valid HWNDs) but
are never rendered on any interactive desktop, and interactive-desktop-dependent assertions
(foreground activation, real Snap Layouts adoption, DWM frame-bounds readback of a window a human
could actually see) either silently no-op or read stale/default geometry. If a self-hosted 25H2
runner is ever added specifically to close the Tier 5 build-26200 gap, it must run interactively
under a logged-in user session (e.g. via `runner.exe` invoked from an auto-login startup task, not
`svc install`), matching exactly the constraint `windows-latest` already satisfies as a hosted
runner.

---

## 8. Coverage

Use **`coverlet.MTP`** (invoked with the `--coverlet` flag under native MTP `dotnet test`) for
Bastion.Core/Bastion.Layout/Bastion.Win32 test coverage:

```
dotnet test --coverlet --coverlet-output-format cobertura
```

**Forbidden: `coverlet.collector`/`coverlet.msbuild` once test projects run under native MTP.** Both
packages are built on VSTest data-collector infrastructure and are documented as incompatible with
MTP's execution model — they either fail to attach or silently produce no coverage output. Per
Microsoft's migration docs, `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`
(not `false`) is the value that enables the VSTest-based bridge for `dotnet test` on the .NET 9 SDK
and earlier; `false`/unset is the non-bridge, native-MTP state. Microsoft's docs further state that
once a solution opts into native MTP via `global.json` (§1), mixing in a VSTest-based project is
documented as an error — so a per-project VSTest-bridge fallback alongside true-MTP projects is not
a documented supported pattern, not merely a discouraged default posture.

**Do not conflate "the shipped binary is Native AOT" with "the test run needs AOT coverage
instrumentation."** Bastion.Daemon and Bastion.Win32 are published `PublishAot`, but the *test
assemblies* that exercise them are ordinary JIT'd binaries running under the normal .NET 10 runtime
— `coverlet.MTP` applies to them exactly as it does to Bastion.Core/Layout, with no AOT-specific
concern. The `Microsoft.CodeCoverage.MSBuild` + `/p:AotMsCodeCoverageInstrumentation=true` +
`--coverage` path is a materially different, separate mechanism that only matters if a test
specifically instruments and runs the **AOT-published `bastiond.exe` entry point itself** (e.g. a
Tier 4 sandbox smoke test measuring coverage of the published executable) — that is a narrow,
opt-in addition, not the default coverage mechanism for any of the five tiers.

---

## 9. Mutation testing

Use **Stryker.NET v4.13+ (MTP runner, still preview)** (requires the .NET 10 runtime) with its MTP-native runner, invoked
explicitly:

```
dotnet stryker --test-runner mtp
```

This flag is required for xUnit v3 projects — the legacy VSTest runner path is broken for xUnit v3
(tracked as stryker-mutator/stryker-net#3117; the project reported not handling v3 test discovery
correctly at all under the old runner). Expect longer full-suite runs than a VSTest-runner project
would give: per-mutant coverage filtering (skipping mutants no test touches, normally Stryker's main
speed optimization) is only partially implemented for the MTP runner as of mid-2026 — budget CI time
accordingly and re-evaluate as the MTP runner matures out of preview.

**Scope mutation testing to Bastion.Core and Bastion.Layout only.** Two independent reasons:

1. Mutation testing has no AOT-publish-time awareness — it mutates IL/source and runs it through
   the ordinary JIT'd test host regardless of whether the production assembly ever gets
   `PublishAot`'d, so it tells you nothing about AOT-specific behavior even if pointed at
   Bastion.Win32/Daemon.
2. Bastion.Win32/Daemon depend on live OS state (real HWNDs, WinEvents, COM activation) that a
   mutation run cannot meaningfully fake at the unit level without duplicating all of Tier 2's
   fake-adapter machinery — and Tier 2's replay corpus is already the right tool for regression
   coverage of that ring (§5), not mutation testing.

Bastion.Core and Bastion.Layout are the opposite: pure, side-effect-free, invariant-heavy code where
Tier 1's property tests (§3) are exactly the kind of test suite mutation testing is designed to
find gaps in — a mutant that survives every property test is a real signal that an invariant is
under-specified or a generator's input space misses a case.

---

## Forbidden (quick reference)

- Mixing VSTest-based and Microsoft.Testing.Platform-based test projects in one solution or CI
  matrix (§1) — unsupported; VSTest CLI options are silently ignored or misapplied against MTP.
- Filtering Tier 3/Tier 5 tests with legacy `--filter TestCategory=X` VSTest syntax under xUnit v3
  (§2) — unsupported per Microsoft's VSTest→MTP migration docs; use the v3 query-filter language.
- `coverlet.collector`/`coverlet.msbuild` under native MTP `dotnet test` (§8) — VSTest-collector
  infrastructure, documented-incompatible; use `coverlet.MTP`.
- Reporting Tier 5 canary results from `windows-latest` as validating DESIGN.md's stated target
  (Windows 11 25H2, build 26200) (§7) — `windows-latest` is Server 2025, build 26100; a different
  OS/build. Track the gap to a self-hosted 25H2 runner or Tier 4 Sandbox explicitly.
- Installing a self-hosted Windows runner as a Windows service when it must drive Tier 3/5's
  visible test windows (§7) — Session 0 has no interactive desktop; windows are created but never
  rendered. Run interactively under a logged-in session instead.
- Pointing Stryker.NET (or any mutation run) at Bastion.Win32/Bastion.Daemon as a primary target
  (§9) — no AOT-publish-time awareness, and live-OS dependencies make unit-level mutation low-value
  there; reserve mutation testing for Bastion.Core/Bastion.Layout.
- Assuming Verify's default scrubbers normalize a `RECT` or `WindowId` DTO (§6) — only GUIDs,
  `DateTime`, and paths are auto-scrubbed; register an explicit scrubber/converter or accept
  spurious diffs from non-deterministic IDs.
- Treating "the shipped binary is Native AOT" as a reason to add AOT coverage instrumentation to
  ordinary unit-test coverage runs (§8) — test assemblies are JIT'd regardless of the production
  assembly's publish mode; `AotMsCodeCoverageInstrumentation` is a narrow, separate concern for
  instrumenting the published `bastiond.exe` entry point itself, not the default path.

---

## Open items to revisit

- **FsCheck vs. CsCheck for the split-tree generator**: default to FsCheck; switch a given test
  suite to CsCheck only after empirically observing weak shrink quality on a real counterexample,
  or when stateful/model-based Reconciler property tests are added. Record the decision and
  supporting evidence where the choice is made (test project README), not in this document.
- **MTP v1 → v2 migration**: already complete as of this writing — §1 now pins `xunit.v3.mtp-v2`
  directly, forced by `Microsoft.Testing.Extensions.CodeCoverage`'s hard MTP-v2 dependency. When
  xUnit 4.0.0 ships and makes the plain `xunit.v3` package default to the v2 line, revisit whether
  the explicit `xunit.v3.mtp-v2` reference can simplify back to plain `xunit.v3`.
- **Self-hosted 25H2 runner for Tier 5**: not yet provisioned. When added, it must run interactively
  (§7) and its provisioning/maintenance is a CI-infrastructure decision outside this document's
  scope — record the runbook wherever the project's CI infrastructure is documented once it exists.
- **Stryker.NET MTP runner coverage-per-mutant filtering**: incomplete as of mid-2026; re-time CI
  budget for Tier 1's mutation job as the runner matures out of preview.
