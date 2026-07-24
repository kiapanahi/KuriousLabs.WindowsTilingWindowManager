# Repo Quality Gates: MSBuild, Analyzers, Style & AOT Validation

This document is the single source of truth for Bastion's build infrastructure: central package
management, `Directory.Build.props`/`.targets` semantics, warning/analyzer escalation, the
`BannedApiAnalyzers`-enforced no-hacks constraint, style enforcement, third-party analyzer
layering, C# 14 / `LangVersion` policy, per-project AOT property placement, and the exact CI gate
commands. Everything here exists to make DESIGN.md §1's "only documented, supported, public
Windows APIs" constraint (and §10's NativeAOT-first stack) a build-time fact, not a code-review
opinion.

Sibling docs: `docs/engineering/interop.md` (CsWin32/COM/callback specifics referenced in §5, §7),
`docs/engineering/concurrency-performance.md`, `docs/engineering/daemon-architecture.md`,
`docs/engineering/testing.md`.

---

## 1. Central Package Management

Bastion uses NuGet Central Package Management (CPM), **not** a `packages.json`-style file — the
manifest is `Directory.Packages.props` at the repo root:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Windows.CsWin32" Version="0.3.106" />
    <PackageVersion Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.11.0" />
    <!-- one PackageVersion per package id, repo-wide -->
  </ItemGroup>
</Project>
```

Rules:

- Every `.csproj` `<PackageReference>` is **bare** — `<PackageReference Include="Microsoft.Windows.CsWin32" />`, no `Version` attribute. A `Version` attribute on a `PackageReference` when CPM is enabled is a **restore failure (NU1008)**, not a warning — this is intentional friction against version drift between projects.
- `<PackageVersion>` items in `Directory.Packages.props` hold the actual versions. One entry per package id for the whole repo; no per-project overrides unless justified (below).
- `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` pins transitive dependency versions too, so a transitive bump can't silently change behavior between two projects that both restore the same top-level package.
- `VersionOverride` on an individual `<PackageReference>` is the **only** sanctioned escape hatch (e.g. a project needs a newer package version temporarily during a migration) — it must carry a `<!-- VersionOverride: reason, tracking issue -->` comment and is expected to be temporary. Do not use it to route around a CPM restore failure without understanding why the failure fired.

### `GlobalPackageReference` for repo-wide tooling

Analyzer/tooling packages that every project needs but that are never referenced in code go in
`Directory.Packages.props` as `<GlobalPackageReference>`, not as a `<PackageReference>` copy-pasted
into every `.csproj`:

```xml
<ItemGroup>
  <GlobalPackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.11.0" />
  <GlobalPackageReference Include="Roslynator.Analyzers" Version="4.12.9" />
  <GlobalPackageReference Include="Meziantou.Analyzer" Version="2.0.180" />
</ItemGroup>
```

This applies the reference to every project in the tree automatically (SDK-style projects only)
without touching individual `.csproj` files when a new analyzer is added.

---

## 2. `Directory.Build.props` / `.targets` semantics

Two files, two different import timings, two different jobs:

- **`Directory.Build.props`** — imported **early**, before the project's own `<PropertyGroup>`s. Anything set here is an **overridable default**: `TargetFramework`, `LangVersion`, `Nullable`, `ImplicitUsings`, baseline analyzer configuration. A project file can still override these by setting the property again after the implicit `Sdk.props` import.
- **`Directory.Build.targets`** — imported **late**, after the project body. Use only for values that must win regardless of what an individual project set — e.g. a repo-wide `<Deterministic>true</Deterministic>` that no project should be able to opt out of, or wiring `<AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />` for `BannedApiAnalyzers` (§5) so it's impossible to forget per-project.

**Search-upward-stop-at-first-found.** MSBuild walks up from each project directory and imports
the *first* `Directory.Build.props`/`.targets` it finds — it does **not** merge multiple levels
automatically. A nested file (e.g. a hypothetical `Bastion.Bar/Directory.Build.props` for WinUI-3-
specific settings) **replaces** the root one for that subtree unless it explicitly imports the
parent:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" Condition="'' != $([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <!-- Bastion.Bar-specific additions below -->
</Project>
```

Any project subtree that needs its own `Directory.Build.props` (Bastion.Bar's WinUI 3/Windows App
SDK targets are the likely candidate) must use `GetPathOfFileAbove` to chain to the root file, or
it silently loses CPM wiring, `LangVersion`, analyzer configuration, and the `BannedSymbols.txt`
`AdditionalFiles` registration. Treat a nested `Directory.Build.props` as a deliberate, reviewed
decision, not a convenience.

---

## 3. Warning escalation — know what each knob covers

Three independent diagnostic families exist in this repo, and **no single knob covers all three**.
Conflating them is the most common way a "should have failed CI" bug slips through.

| Family | Knob | Covers |
|---|---|---|
| Compiler warnings (`CSxxxx`) | `TreatWarningsAsErrors=true` + `WarningsAsErrors=$(WarningsAsErrors);nullable` | Only `CS` diagnostics — including the `nullable` warning wave explicitly appended so nullable-flow violations are errors even before they're individually promoted. |
| Roslyn code-quality analyzers (`CAxxxx`) | `dotnet_analyzer_diagnostic.category-<Category>.severity` in `.editorconfig`, or `AnalysisLevel`/`AnalysisMode` (§below) | Analyzer diagnostics from `Microsoft.CodeAnalysis.NetAnalyzers` and third-party analyzers. `TreatWarningsAsErrors` does escalate these too **once emitted as build warnings**, but their *severity* (warn vs. suggestion vs. silent) is governed by the analyzer configuration, not the compiler switch. |
| NuGet audit (`NU190x`) | Explicit promotion, e.g. `<WarningsAsErrors>$(WarningsAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>` | Vulnerable-package advisories. `NuGetAudit` runs by default (`all` mode, including transitive — confirm this is still the default for the SDK version pinned in `global.json`) but only *warns*; CI treats a known-vulnerable dependency as a hard failure only once these four IDs are added to `WarningsAsErrors`. |

```xml
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <WarningsAsErrors>$(WarningsAsErrors);nullable;NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>
</PropertyGroup>
```

### `AnalysisLevel` — pin the compound value, never `latest`

```xml
<PropertyGroup>
  <AnalysisLevel>10-all</AnalysisLevel>
  <AnalysisModeSecurity>All</AnalysisModeSecurity>
  <AnalysisModeReliability>All</AnalysisModeReliability>
</PropertyGroup>
```

- `AnalysisLevel` takes a compound `<version>-<mode>` value. `latest` (the SDK default) means the
  enabled rule set silently changes on every SDK upgrade — pin the numeric TFM-year form (`10-all`)
  so a `dotnet` SDK bump can't change which rules fire without an explicit, reviewed repo change.
- **[verified against `learn.microsoft.com/dotnet/core/project-sdk/msbuild-props` and
  `.../fundamentals/code-analysis/overview`]** Setting `AnalysisMode`/`AnalysisLevel` to `All` (or
  the compound `<n>-all`) does **not** enable every rule — a fixed legacy subset is excluded even
  under `all`: **CA1017, CA1045, CA1005, CA1014, CA1060, CA1021**, plus the code-metrics rules
  **CA1501, CA1502, CA1505, CA1506, CA1509**. These can still be turned on individually with
  `dotnet_diagnostic.CA1017.severity = warning` etc. in `.editorconfig` if a specific one is judged
  worth enforcing (e.g. CA1045 — "avoid ref/out parameters" — is plausibly worth reconsidering for
  the P/Invoke-adjacent adapter ring, since CsWin32 signatures legitimately use `out`/`ref`; if so,
  re-enable it per-project via a `.editorconfig` override scoped to `Bastion.Win32/**`, not
  repo-wide).
- `AnalysisModeSecurity=All` and `AnalysisModeReliability=All` bulk-enable those two categories
  independent of the general level — appropriate for an interop-heavy, hook-callback-heavy
  codebase where reliability rules (disposal, exception handling around unmanaged boundaries) and
  security rules (deserialization, cryptography, injection) matter more than the general default
  bar. `AnalysisMode<Category>` values are the same vocabulary (`None`/`Default`/`Minimum`/
  `Recommended`/`All`) as the top-level `AnalysisMode`.
- `.editorconfig` severity precedence, most to least specific:
  `dotnet_diagnostic.<Id>.severity` (single rule) → `dotnet_analyzer_diagnostic.category-<Cat>.severity`
  (one category) → `dotnet_analyzer_diagnostic.severity` (all analyzer rules). A per-rule entry
  always wins over a category entry, which always wins over the blanket entry.

---

## 4. Style enforcement

`EnforceCodeStyleInBuild=true` is non-negotiable in `Directory.Build.props`. Without it, `IDE0xxx`
style rules run live in the IDE and in `dotnet format`, but **never fail a `dotnet build`** — a
style regression would pass CI silently until someone runs `dotnet format --verify-no-changes`
(§8), which is redundant work when the compiler can just enforce it directly.

```xml
<PropertyGroup>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
</PropertyGroup>
```

Baseline `.editorconfig` entries for this repo:

```ini
# File-scoped namespaces are mandatory, not a suggestion
csharp_style_namespace_declarations = file_scoped:error

# var only where the type is visually apparent from the RHS — deliberate for the adapter
# ring, where reading the exact CsWin32-generated type (HWND vs nint vs a raw struct) at
# the P/Invoke boundary matters more than terseness.
csharp_style_var_for_built_in_types = true:suggestion
csharp_style_var_when_type_is_apparent = true:warning
csharp_style_var_elsewhere = false:warning

# IDE1006 is the ONLY naming severity that has build-time teeth (see below)
dotnet_diagnostic.IDE1006.severity = error
```

**Naming rules gotcha.** The `dotnet_naming_rule.*`/`dotnet_naming_style.*`/`dotnet_naming_symbols.*`
triad in `.editorconfig` *configures* what "correct" naming looks like (PascalCase types, `_camelCase`
private fields, etc.), but that configuration is inert for build purposes unless
`dotnet_diagnostic.IDE1006.severity` is itself set to `warning` or `error`. Setting only the naming
triad and forgetting the `IDE1006` severity line is a silent no-op at build time — the IDE will
still squiggle it, `dotnet build` will not fail. This repo sets it to `error`.

---

## 5. `BannedApiAnalyzers` — the no-hacks constraint as a build error

DESIGN.md §1's "only documented, supported, public Windows APIs" and §10's AOT-safe-interop
commitments are enforced mechanically, not by convention, via
`Microsoft.CodeAnalysis.BannedApiAnalyzers`.

Wiring (`Directory.Build.props` + `Directory.Build.targets`):

```xml
<!-- Directory.Build.props -->
<ItemGroup>
  <GlobalPackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.11.0" />
</ItemGroup>

<!-- Directory.Build.targets (late import — every project gets this regardless of local overrides) -->
<ItemGroup>
  <AdditionalFiles Include="$([MSBuild]::GetPathOfFileAbove('BannedSymbols.txt', '$(MSBuildThisFileDirectory)'))" />
</ItemGroup>
<PropertyGroup>
  <WarningsAsErrors>$(WarningsAsErrors);RS0030</WarningsAsErrors>
</PropertyGroup>
```

`RS0030` (the "symbol is banned" diagnostic id) is added to `WarningsAsErrors` so a banned API use
is a build failure, not a suggestion — this is the mechanism that turns "no hacks" from a design
principle into something a PR literally cannot merge with CI green.

### `BannedSymbols.txt` — repo-root, `DocumentationCommentId` format

Each line is `<DocCommentId>;<message>`. Seed entries, grounded directly in interop.md's
researched anti-patterns:

```text
T:System.Runtime.InteropServices.DllImportAttribute;Use [LibraryImport] (source-generated, AOT/trim-safe). See docs/engineering/interop.md §2.
T:System.Runtime.InteropServices.ComImportAttribute;Use [GeneratedComInterface] — [ComImport]-based COM is unsupported under NativeAOT (IL3052). See docs/engineering/interop.md §4.
T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter;BinaryFormatter is unconditionally disabled/removed and a documented deserialization-of-untrusted-data risk (CWE-502). Never use, even behind a feature flag.
M:System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(System.IntPtr,System.Type);[RequiresDynamicCode] and unsupported under full AOT. Use delegate* unmanaged<...> function pointers + [UnmanagedCallersOnly] instead. See docs/engineering/interop.md §3.
M:System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(System.Delegate);[RequiresDynamicCode]. WinEventProc/LowLevelKeyboardProc callbacks must be [UnmanagedCallersOnly] statics registered via delegate* unmanaged pointers, never marshaled delegates. See docs/engineering/interop.md §3.
M:System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(System.IntPtr);Use ComWrappers/StrategyBasedComWrappers (GeneratedComInterface's supporting infrastructure) instead of the classic runtime COM marshaler.
M:System.Runtime.InteropServices.Marshal.GetIUnknownForObject(System.Object);Use ComWrappers/StrategyBasedComWrappers instead of the classic runtime COM marshaler.
```

Grow this list as `interop.md` documents new enforceable symbol names — the DocCommentId format
(`T:` for types, `M:` for methods, `P:` for properties, `F:` for fields, `N:` to ban an entire
namespace) is the same one `Microsoft.CodeAnalysis.DocumentationCommentId` uses to round-trip
symbols to/from strings, which is why `BannedApiAnalyzers` requires exactly this syntax rather than
a bare method name. A banned entry with a typo'd DocCommentId (wrong arity, wrong namespace
casing) silently fails to match anything — when adding an entry, confirm it actually fires by
temporarily introducing the banned call in a scratch file and observing `RS0030`.

**Rationale cross-check** (verified in interop.md's research): `[LibraryImport]` and
`[GeneratedComInterface]` are the compile-time, source-generator-based, AOT/trim-safe replacements
for the JIT-stub-based built-in interop that `[DllImport]`/`[ComImport]` rely on — this is exactly
why they're banned rather than merely discouraged in code review.

---

## 6. Third-party analyzers

Layer analyzers with `PrivateAssets="all"` on the `PackageVersion`/reference so they never flow
transitively to a consumer of Bastion's (currently nonexistent, but keep the habit) NuGet-packaged
libraries:

```xml
<GlobalPackageReference Include="Roslynator.Analyzers" Version="4.12.9" PrivateAssets="all" />
```

- **Roslynator** — install the **`Roslynator.Analyzers` NuGet package only**, never the Visual
  Studio/VS Code IDE extension as the enforcement mechanism. The dotnet org's own guidance is that
  analyzer functionality is being phased out of the IDE extensions in favor of the NuGet package,
  and only the NuGet package participates in `dotnet build`/CI at all — the extension is
  editor-experience-only and provides zero CI enforcement.
- **Meziantou.Analyzer** — actively, weekly maintained; bulk-configure initial severity with the
  single `<MeziantouAnalysisMode>` MSBuild property (analogous to `AnalysisMode`) rather than
  hand-tuning dozens of individual rule severities on rollout; narrow to per-rule overrides later
  as specific rules prove noisy.
- **`Microsoft.VisualStudio.Threading.Analyzers`** (`VSTHRD*`, notably VSTHRD002 "avoid
  sync-over-async", VSTHRD010 "invoke single-threaded types on the main thread") — scope this
  package to **`Bastion.Win32` and `Bastion.Daemon` only**, via a project-local
  `<PackageReference>` with `VersionOverride` semantics living outside the global set, or an
  explicit per-project `<ItemGroup Condition="'$(MSBuildProjectName)'=='Bastion.Win32' OR
  '$(MSBuildProjectName)'=='Bastion.Daemon'">`. Do **not** make it a `GlobalPackageReference`:
  it has a documented, non-trivial build-time cost on larger compilations, and its analyzers can
  leak STA/UI-thread assumptions into consumers of a library that has none (`Bastion.Core`,
  `Bastion.Layout` are explicitly Win32-free per DESIGN.md §3/§10 and must stay that way — pulling
  in a threading analyzer tuned for STA/UI code onto a pure library is a category error). For pure
  libraries, set `ExcludeAssets="analyzers"` on any transitively-pulled reference so its analyzer
  payload never activates there. Before widening this analyzer's footprint, profile with
  `-p:RunAnalyzers=false` as a baseline to isolate its contribution to build time from everything
  else.

---

## 7. Language & project properties

### `LangVersion` — pinned, never `latest`

```xml
<PropertyGroup>
  <LangVersion>14.0</LangVersion>
  <TargetFramework>net10.0-windows</TargetFramework>
</PropertyGroup>
```

`LangVersion` tracks the TFM's shipped compiler version, and — same reasoning as `AnalysisLevel` —
`latest` makes the enabled language surface non-reproducible across SDK installations/CI runners
that happen to have different SDK feature bands installed. Pin the exact numeric value (`14.0`)
and bump it deliberately in a reviewed change when moving the baseline.

C# 14 features worth using where they concretely pay for themselves in this codebase (not as a
checklist to exhaust — see the top-level engineering guidance for the full catalogue):

- **Extension blocks** (`extension(...)`) — add ergonomic instance-like members to
  CsWin32-generated structs (`HWND`, `HMONITOR`) without touching generated code or wrapping them
  in a hand-rolled type, keeping the generated P/Invoke surface untouched and re-generatable.
- **`field` keyword** — validated setters on config/state records (e.g. reassert-budget config
  values) without a hand-rolled backing field.
- **Null-conditional assignment** (`obj?.Field = value`) — trims null-check boilerplate around the
  optional `Activity?`/`ILogger?` patterns from the observability guidance.
- **Implicit `Span` conversions** — reduces `AsSpan()` noise at hot-path boundaries in the ingest
  pump (`Bastion.Win32`) and Layout Engine.

Any feature that requires a **newer** language/runtime version than this pinned C# 14 / .NET 10
baseline must be called out explicitly in the PR that introduces it and is out of scope for this
document to pre-approve.

### AOT property placement — executables vs. libraries

This is the single most consequential property-placement rule in the repo and the one most likely
to be gotten backwards:

| Project | Property | Effect |
|---|---|---|
| `Bastion.Daemon`, `Bastion.Cli` (executable-hosting) | `<PublishAot>true</PublishAot>` | Enables real Native AOT compilation **only when `dotnet publish` runs**. Has **no effect** on `dotnet build`, and has **no effect at all** if set on a library project — `PublishAot` on a class library is silently inert. |
| `Bastion.Core`, `Bastion.Layout`, `Bastion.Win32` (libraries) | `<IsAotCompatible>true</IsAotCompatible>` | **[verified]** Implies `IsTrimmable=true` plus `EnableTrimAnalyzer=true`, `EnableAotAnalyzer=true`, `EnableSingleFileAnalyzer=true` — surfacing `IL2xxx` (trim) and `IL3xxx` (AOT) analyzer warnings during **ordinary `dotnet build`**, without the consuming app ever needing to publish AOT. This is how a library author gets AOT-compatibility feedback in every PR, not just at release-publish time. |

```xml
<!-- Bastion.Daemon.csproj / Bastion.Cli.csproj -->
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
</PropertyGroup>

<!-- Bastion.Core.csproj / Bastion.Layout.csproj / Bastion.Win32.csproj -->
<PropertyGroup>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

Consequences of getting this backwards: setting `PublishAot` on `Bastion.Core` buys nothing (no
analyzer feedback, no publish behavior — it's an executable-only switch); *omitting*
`IsAotCompatible` on `Bastion.Win32` means the CsWin32/COM interop layer — the one place trim/AOT
incompatibility is most likely to actually occur — gets **zero** IL2xxx/IL3xxx feedback until
someone runs a full `dotnet publish -r win-x64` against `Bastion.Daemon` and traces the failure
back through three project boundaries.

**Validate exclusively via `dotnet publish`, never `dotnet build -t:Publish`** — the two are not
equivalent for trim/AOT analysis purposes; `-t:Publish` does not exercise the same analyzer and
trimming code paths as an actual `dotnet publish` invocation (§8).

### `InvariantGlobalization` — candidate for Daemon/Cli, not yet repo-wide

`<InvariantGlobalization>true</InvariantGlobalization>` is a **[strong, not doc-verified this
pass]** candidate for `Bastion.Daemon`/`Bastion.Cli`: window-title and window-class matching in the
manageability filter (DESIGN.md §3.3) is ordinal, not culture-aware, and a desktop WM's hot path has
no localized formatting need. Before applying it:

1. Confirm the exact scope of what `InvariantGlobalization` changes (ICU removal, string comparison
   defaulting to ordinal, `ToUpper`/`ToLower` behavior) against
   `learn.microsoft.com/dotnet/core/runtime-config/globalization` before relying on it — this was
   asserted architecturally reasonable but not re-confirmed against that specific page in this
   research pass.
2. Verify `Bastion.Bar` (WinUI 3 status bar) has no culture-aware date/number formatting need
   before ever applying `InvariantGlobalization` anywhere near it — it is explicitly **not**
   proposed for `Bastion.Bar` here.

### `InternalsVisibleTo`

Use the classic attribute form, not any MSBuild-item shorthand:

```csharp
// AssemblyInfo.cs or a top-level statement in the project
[assembly: InternalsVisibleTo("Bastion.Core.Tests")]
```

Bare assembly name, unsigned (Bastion ships no strong-named assemblies). Treat any
`<InternalsVisibleTo Include="..." />` MSBuild-item syntax as **unverified** — no confirmed
SDK-native shorthand for this was established in this research pass; use the attribute until one
is confirmed against `learn.microsoft.com`.

---

## 8. The gate commands — canonical CI sequence

These four commands, in this order, are what the quality-gate skill/CI job executes. Each checks a
different failure class; none is a substitute for another.

```bash
# 1. Build: compiler errors, CSxxxx warnings-as-errors, CAxxxx analyzers, AOT/trim analyzers
#    (IsAotCompatible libraries surface IL2xxx/IL3xxx here already, before any publish step)
dotnet build --configuration Release -warnaserror

# 2. Test: Microsoft.Testing.Platform (MTP) entry point; tier filters per testing.md §2
#    (Tier 1/2 run on every PR; Tier 3/5 are quarantined/scheduled per testing.md)
dotnet test --configuration Release

# 3. Style/analyzer verification without mutation — catches IDE0xxx/analyzer drift
#    regardless of whether EnforceCodeStyleInBuild caught it in step 1
dotnet format --verify-no-changes --severity error

# 4. AOT/trim validation — MUST be `dotnet publish`, never `dotnet build -t:Publish`
#    (only publish exercises real trimming/AOT analysis and code paths)
dotnet publish src/Bastion.Daemon -r win-x64 --configuration Release
dotnet publish src/Bastion.Cli -r win-x64 --configuration Release
```

Notes on step 3: `dotnet format` has `style`/`analyzers`/`whitespace` subcommands
(`dotnet format style`, `dotnet format analyzers`) if CI wants to split style-only vs.
analyzer-only verification into separate, independently-reportable jobs — both still read
`.editorconfig` and run regardless of whether `EnforceCodeStyleInBuild` is set, since `dotnet
format` is a separate tool invocation from `dotnet build`, not a consumer of that property.

Notes on step 4: run publish for **every** `PublishAot=true` project and **every** RID Bastion
ships (`win-x64`, `win-arm64` per DESIGN.md's `RuntimeIdentifiers`) — an AOT/trim warning can be
architecture-specific (see interop.md's arch-specific-API note on `AnyCPU` silently dropping
wildcard CsWin32 members), so a green `win-x64` publish does not guarantee a green `win-arm64`
publish.

---

## 9. Product versioning (MinVer)

GitHub issue #48. Bastion derives one semantic version for the whole product from git tags via
[MinVer](https://github.com/adamralph/minver), referenced as a conditional `GlobalPackageReference`
in `Directory.Packages.props` with `PrivateAssets="all"`, scoped to all projects except
`Bastion.Core` and `Bastion.Layout` (which ship nothing and must stay tooling-free per their purity
rules — `pure-core` skill). The condition is `MSBuildProjectName != 'Bastion.Core' and
MSBuildProjectName != 'Bastion.Layout'`. The four shipping projects that pick it up are
`Bastion.Daemon`, `Bastion.Cli`, `Bastion.Win32`, and `Bastion.TestWindows` (and, once scaffolded,
`Bastion.Bar` — issue #19).

- **Why MinVer over Nerdbank.GitVersioning**: `bastiond`/`bastionc`/`bastion-bar` are three
  processes of one product that must always deploy as matching versions — the IPC
  `ProtocolVersion` handshake (`docs/engineering/json-ipc-config.md`) exists specifically to catch
  the drift a mismatched build would cause. A single repo-wide version derived from git tag +
  commit height is the right model here, not NBGV's per-project `version.json` (built for
  independently-versioned packages/libraries, which this repo doesn't publish). MinVer is also a
  build-time-only MSBuild task: it sets `Version`/`AssemblyVersion`/`FileVersion`/
  `InformationalVersion` and adds no reference to the built output, so it never appears in a
  published/trimmed NativeAOT binary.
- **Tag prefix**: `MinVerTagPrefix` is set to `v` in `Directory.Build.props` (repo-wide; harmless
  on projects that don't reference the MinVer package). Tags look like `v0.1.0`,
  `v0.2.0-alpha.1`.
- **Cutting a release**: `git tag -a vX.Y.Z -m "..."` at the commit to release, then `git push
  origin vX.Y.Z`. The next build/publish at (or after) that commit picks it up automatically — no
  MSBuild property to edit by hand.
- **Where the version surfaces**: `bastionc --version` (explicitly reads
  `AssemblyInformationalVersionAttribute` rather than relying on `System.CommandLine`'s own
  undocumented default resolution for its built-in `--version` option — see
  `src/Bastion.Cli/PrintAssemblyVersionAction.cs`) and a `bastiond` startup log line
  (`BastiondService.LogStarted`, `[LoggerMessage]` source-gen per
  `docs/engineering/daemon-architecture.md`).
- **CI history depth**: MinVer needs the tag history to compute height, so any CI job that builds
  a shipping project must check out full history — `actions/checkout@v4` with `fetch-depth: 0` (or
  at minimum enough depth to reach the most recent tag), not the default shallow `fetch-depth: 1`.
  A shallow checkout without the tag falls back to MinVer's untagged default
  (`0.0.0-alpha.0.<height>+<sha>`), which still builds but is not the intended release version.
- Distinct from, and must not be conflated with, the IPC wire-protocol `ProtocolVersion` field
  described in `docs/engineering/json-ipc-config.md` — that versions the command/reply DTO
  contract, this versions the product/release.

---

## Forbidden (build-enforced anti-patterns)

These are build errors (via `BannedApiAnalyzers`/`RS0030`, §5) or must-not-compile-clean patterns,
not style suggestions:

- **`[DllImport]`** anywhere in the repo — banned via `BannedSymbols.txt`. Use `[LibraryImport]`
  (or let CsWin32 generate it). Escaping into raw `[DllImport]` to work around a CsWin32 generation
  gap is exactly the kind of hack §1's constraint exists to prevent — file an issue against
  CsWin32/extend `NativeMethods.txt` instead.
- **`[ComImport]`** anywhere in the repo — banned via `BannedSymbols.txt`; unsupported under
  NativeAOT (`IL3052`). Use `[GeneratedComInterface]` per interop.md.
- **`Marshal.GetDelegateForFunctionPointer` / `Marshal.GetFunctionPointerForDelegate`** — banned;
  both are `[RequiresDynamicCode]` and explicitly documented as superseded by `delegate* unmanaged`
  function pointers + `[UnmanagedCallersOnly]`. Any WinEventProc/LowLevelKeyboardProc callback using
  either is both a build failure here and a runtime-crash risk under AOT (interop.md §3).
- **`Marshal.GetObjectForIUnknown` / `Marshal.GetIUnknownForObject`** — banned; use
  ComWrappers/`StrategyBasedComWrappers`.
- **`BinaryFormatter`** (and `NetDataContractSerializer`) — banned; deserialization of untrusted
  data (CWE-502), and `BinaryFormatter` throws unconditionally on modern .NET regardless.
- **`dotnet build -t:Publish` as an AOT/trim validation substitute** — not a `BannedApiAnalyzers`
  rule (it's an invocation pattern, not a symbol), but treat a PR or CI job that relies on it in
  place of real `dotnet publish` as a quality-gate defect to fix, not a valid alternative — flag it
  in review the same as a banned-API use.
- **`PublishAot=true` set on a library project** (`Bastion.Core`/`Bastion.Layout`/`Bastion.Win32`)
  as if it provided AOT-compatibility feedback — it does nothing there; the correct property is
  `IsAotCompatible=true` (§7). Not a build error, but a reviewer must catch and correct it — it
  gives false confidence that AOT compatibility is being checked when it is not.
- **`AnalysisLevel`/`LangVersion` set to `latest`** anywhere in the tree — both make CI
  non-reproducible across SDK installations; pin numeric values (§3, §7).
- **A nested `Directory.Build.props`/`.targets` that doesn't chain to the root via
  `GetPathOfFileAbove`** — silently drops CPM wiring, pinned `LangVersion`, and the
  `BannedSymbols.txt` `AdditionalFiles` registration for that subtree (§2). Treat an un-chained
  nested build-props file as a build-configuration bug even though nothing fails loudly.
