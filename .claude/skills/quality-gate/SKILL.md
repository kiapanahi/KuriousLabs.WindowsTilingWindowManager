---
name: quality-gate
description: Pre-completion gate: the exact build/test/format/publish commands that must pass before declaring any task done in this repo. Run at the end of every coding task.
---

# quality-gate

Purpose: one canonical, ordered command sequence so no session invents its own definition of
"done." The sequence below mirrors `docs/engineering/quality-gates.md` §8 and the CI workflow
exactly — if you find a discrepancy between this file and that doc or the CI YAML, treat the
doc/CI as the source of truth and flag the drift instead of silently picking one.

## When to use

- Before reporting any coding task complete, no matter how small the diff looked.
- Before creating any commit.
- After resolving code-review feedback, even if only one file changed.
- Never skip this because "it's just a comment/doc change" — run it anyway; it is cheap insurance
  and catches config drift you didn't cause.

## Before you start

- Confirm `global.json` still pins the MTP-native test runner
  (`"test": {"runner": "Microsoft.Testing.Platform"}`). This check is a no-op until the project
  scaffolding (`global.json`, `.csproj`/`.sln`) exists — early in the repo's life it may still be
  docs-only, so don't be surprised if the file is absent. If a change touched `global.json` or added
  a test project, verify it did not silently reintroduce a VSTest-based project
  (`xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`) alongside the MTP ones — mixing the two
  in one solution is unsupported by Microsoft's own docs and produces confusing, silently-ignored
  CLI options. If you find a mixed project, stop and raise it; do not "fix" it by guessing which
  mode wins.
- Know which projects are pure (`Bastion.Core`, `Bastion.Layout`) vs. Win32/AOT-adjacent
  (`Bastion.Win32`, `Bastion.Daemon`, `Bastion.Cli`) — several gate steps below apply only to the
  latter.

## Gate sequence (all must pass; stop and fix at first failure — never proceed past a red step)

1. **`dotnet build`** — must be zero-warning end to end (`TreatWarningsAsErrors` is repo-wide).
   Do not just check the exit code; read the warning list if the exit code is 0 but warnings were
   printed (a misconfigured project can still emit warnings without failing). Watch specifically
   for:
   - `IL2xxx`/`IL3xxx` — AOT/trim analyzer diagnostics. `IsAotCompatible` is set on the library ring
     (`Bastion.Core`, `Bastion.Layout`, `Bastion.Win32`) and produces IL2xxx/IL3xxx during ordinary
     `dotnet build`. `Bastion.Daemon`/`Bastion.Cli` instead carry `PublishAot=true`, whose full
     trim/AOT analysis only runs at `dotnet publish` (step 4) — don't expect step 1 alone to catch
     AOT issues specific to those two executables. Never suppress with a bare pragma; see
     `docs/engineering/quality-gates.md` for the `[UnconditionalSuppressMessage]` policy.
   - `RS0030` — banned API hit (`BannedSymbols.txt`). If it fires, do not suppress it: either the
     code should not exist (find the documented replacement) or `BannedSymbols.txt` is stale for a
     legitimately-allowed pattern — fix the root cause, don't silence the diagnostic.
   - `IDE1006` — naming-convention violation; the repo enforces this at build, not just in the
     editor.
   - `VSTHRD*` (Microsoft.VisualStudio.Threading.Analyzers) — async/threading-lifetime violations;
     these are load-bearing in `Bastion.Win32`/`Bastion.Daemon` where captured `SynchronizationContext`,
     blocking-on-async, and lock-across-await bugs are exactly the failure modes the reconciler
     design forbids.
   - If the change touched `Directory.Packages.props`/`Directory.Build.props`/`.editorconfig`,
     re-run a full clean build (`dotnet build --no-incremental` or a prior `clean`) — analyzer/CPM
     config changes can be invisible to an incremental build.

2. **`dotnet test`** with the main-gate query filter:
   ```
   dotnet test --filter-query "/[Category!=Quarantined]"
   ```
   - This is xUnit v3's native MTP query-filter language. **Never** use the legacy VSTest syntax
     `--filter TestCategory=...` or `--filter Category=Quarantined` — under xUnit v3/MTP this is
     either silently ignored or throws (`ArgumentException: settings.SerializedTestCases`, a known
     xUnit v3 bug). The flag name is `--filter-query`, not `--filter`.
   - Tier 3 (real-window integration) and Tier 5 (behavior canaries) are tagged
     `[Trait("Category", "Quarantined")]`/`[Trait("Category", "Canary")]` per `docs/engineering/testing.md`
     and are **not** part of this gate — they run in their own CI job with `continue-on-error: true`.
     Do not fold them in or treat their failure as blocking. Do note in your task summary if the
     diff plausibly affects those tiers (WinEvent ingest, hotkey registration, cloak/desktop-switch
     heuristics, elevated-window detection) so a human knows to watch that job.
   - New test project/cases reporting **zero discovered tests** is a **failure**, not a pass — MTP
     exits code 8 for zero-test assemblies, unlike VSTest's tolerant success. Investigate (filter
     typo, misspelled trait, wrong test-SDK reference); never shrug it off.
   - Tier 1/2 projects (`Bastion.Core`, `Bastion.Layout`) must also pass on the Linux CI leg — if
     you only ran Windows locally, say so rather than implying cross-platform coverage you didn't check.

3. **`dotnet format --verify-no-changes --severity error`**
   - If it fails, run `dotnet format` (no `--verify-no-changes`) to apply fixes, re-run
     `--verify-no-changes` to confirm convergence, and fold the formatting diff into the same
     change you're already making — never leave formatting fixes for a follow-up commit.
   - Do not hand-edit around a formatter disagreement; if the formatter and `.editorconfig` seem
     to disagree, that's a config bug to raise, not something to route around with inline
     suppressions.

4. **Publish check — only when the change touches interop-adjacent surfaces.** Run
   `dotnet publish -r win-x64` on the affected executable(s) (`Bastion.Daemon`, `Bastion.Cli`) if
   the diff touches any of:
   - `Bastion.Win32`, `Bastion.Daemon`, or `Bastion.Cli` source
   - any `.csproj`/`Directory.Build.props`/`Directory.Build.targets`/`*.targets` file
   - `NativeMethods.txt`/`NativeMethods.json` or any CsWin32 configuration
   - anything else interop-adjacent (P/Invoke signatures, `[UnmanagedCallersOnly]` callbacks,
     `[GeneratedComInterface]` definitions, `ComWrappers` registration)

   **Never substitute `dotnet build -t:Publish` for this** — it does not run the real AOT/trim
   analysis pipeline that `dotnet publish` runs, and a change can build clean but fail to publish
   under AOT. If publish fails with new `IL2xxx`/`IL3xxx` warnings-as-errors, treat it exactly like
   a build failure: stop, fix, re-verify. If the change plausibly does *not* touch these surfaces,
   say `publish: n/a` in your report — don't silently skip and don't over-run it on every trivial
   `Bastion.Core` change either (it's slow; reserve it for the layers listed above).

## Change-type addenda — run these checks in addition to the sequence above when they apply

- **New package reference added**: the version must live in `Directory.Packages.props` as a
  `<PackageVersion>` entry; the `.csproj` gets a bare `<PackageReference Include="..." />` with
  **no** `Version` attribute (a `Version` attribute on the reference fails restore with NU1008
  under central package management). Repo-wide analyzer packages are added as
  `<GlobalPackageReference>`, not per-project `<PackageReference>`.
- **New banned-API-adjacent code** (anything resembling `BinaryFormatter`, raw `DateTime.Now` in
  logic code, `[ComImport]`, blocking-on-async, etc.): confirm `BannedSymbols.txt` still covers the
  pattern before assuming `RS0030` will catch a regression later. If the interop or COM shape of
  the change is non-trivial, run the win32-interop skill (see `docs/engineering/interop.md`) before
  this gate, not after — the gate validates, it doesn't design.
- **New reliance on a Windows API or an observed-but-undocumented OS behavior** (anything in the
  spirit of DESIGN.md §13's accepted risks — cloak-state inference, UIPI-based elevated-window
  detection, `ApplicationFrameHost` structure, etc.): verify the verify-windows-api skill was run
  and that a Tier-5 canary test exists for the new assumption before calling the task done. A
  behavior claim with no canary is not considered load-bearing-safe by this gate.
- **`Bastion.Core`/`Bastion.Layout` changes**: confirm the Linux-leg build/test story still holds —
  no `Bastion.Win32` types, no CsWin32-generated types, no Win32-only BCL surface crept into these
  projects. Per the pure-core skill and DESIGN.md §10, these two projects must stay buildable and
  testable on Linux CI; a change that only builds on Windows here is a design violation, not a
  quality-gate detail to wave through.
- **Coverage/mutation tooling touched**: this repo uses `coverlet.MTP`, never `coverlet.collector`/
  `coverlet.msbuild` (VSTest-only, incompatible with native MTP `dotnet test`). Mutation testing
  (Stryker.NET, `--test-runner mtp`) is scoped to `Bastion.Core`/`Bastion.Layout` only — never point
  it at `Bastion.Win32`/`Bastion.Daemon` (live-OS dependencies, no AOT-publish awareness).

## Hard stops — never proceed past these

- Never report a task complete with a red build, a red test run, or unverified formatting, even if
  the failure looks unrelated to your change — bisect or ask before assuming it's pre-existing.
- Never treat Tier 3/Tier 5 (Quarantined/Canary) results as blocking, but never skip mentioning
  them when the change plausibly touches what they cover.
- Never use `dotnet build -t:Publish` as a stand-in for `dotnet publish` for AOT/trim validation —
  the build-target path misses real trim analysis.
- Never suppress `IL2xxx`/`IL3xxx`/`RS0030` with a pragma to make the gate green — use
  `[UnconditionalSuppressMessage]` with a justification, and only after confirming the flagged path
  is genuinely safe (pragmas/`SuppressMessage` don't survive trimming/AOT analysis anyway).
- Any test project reporting **0 tests discovered** exits code **8** — a hard failure, not a
  shrug. Diagnose discovery (filter typo, trait misspelling, wrong test SDK reference) first.

## Report format

End every task summary with the gate results in this shape, filling in real numbers/paths, not
placeholders:

```
build: ok (0 warnings)
tests: <N> passed, <M> skipped — filter "/[Category!=Quarantined]"
format: clean (or: fixed + re-verified)
publish: ok win-x64 / n/a (no interop-adjacent changes)
```

If any line isn't "ok"/"clean"/"n/a", the task is not done — say so plainly instead of softening
it into prose.
