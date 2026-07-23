# CLAUDE.md — Bastion

This file is an index and a set of hard rules. It is not the design document —
**read `DESIGN.md` first** for architecture, rationale, and the full edge-case
matrix. Nothing here should duplicate it; if this file and `DESIGN.md` ever
disagree, `DESIGN.md` wins and this file is stale (fix it).

## 1. What Bastion is

Bastion is a self-healing tiling window manager for Windows 11 25H2, split
across three processes: `bastiond` (daemon, does all the work), `bastionc`
(CLI, thin IPC client), `bastion-bar` (WinUI 3 status bar/appbar). Core
philosophy (DESIGN.md §1): the desktop is a hostile, eventually-consistent
distributed system — WinEvents are hints, authoritative reads are truth, and a
single-threaded Reconciler actor continuously converges desired state against
observed state rather than trusting an event stream. When in doubt about *why*
something is built the way it is, the answer is in DESIGN.md, not here.

## 2. THE constraint (non-negotiable)

Bastion ships **only documented, supported, public Windows APIs.** No private
`IApplicationView`/`IApplicationViewCollection` internals, no DLL injection, no
`WH_CBT`, no reverse-engineered GUIDs, no undocumented registry reads or
writes. This applies to every suggestion accepted into this repo, including
ones I propose myself — a clever undocumented trick is a rejected trick, full
stop, regardless of how it performs.

Observed-but-undocumented *behavior* of a documented API (e.g. shell-cloaking
of inactive-desktop windows, UIPI's `ERROR_ACCESS_DENIED` on `SetWindowPos`)
may be relied on only when it is: (1) explicitly labeled as observed, not
contractual; (2) kept **non-load-bearing** — a documented source of truth
always backstops it; and (3) pinned by a Tier-5 behavior canary (DESIGN.md
§11). See DESIGN.md §2, §3, §13 for the full catalogue of what this rules out.

**Before writing any code that calls a Windows API for the first time, or
relies on newly-discovered API behavior, invoke the `verify-windows-api`
skill.** No exceptions, no "I'm pretty sure this is documented."

## 3. Architecture map

```
WinEvents/hooks -> Event Ingest (pump thread) -> Coalescer -> Reconciler (actor)
                                                        |
                                    5s heartbeat + distrust re-sync
                                                        v
                                   Layout Engine (pure) -> Placement Executor -> Win32
```
IPC (named pipes) connects the daemon to `bastionc` and `bastion-bar`. Full
diagram and per-component detail: DESIGN.md §3.

| Project | Role | Detail |
|---|---|---|
| `Bastion.Core` | Pure state/reconciler/event-log. **No Win32 types.** Linux-CI-tested. | DESIGN.md §3.4, §5 |
| `Bastion.Layout` | Pure `ILayoutEngine` implementations (dwindle, tree, master-stack, monocle). | DESIGN.md §3.5, §6 |
| `Bastion.Win32` | Adapter ring — the **only** project touching `HWND`, CsWin32, or COM. | DESIGN.md §3.1–§3.3, §3.6–§3.10 |
| `Bastion.Daemon` | `bastiond` composition root, hosted services, IPC server. | DESIGN.md §3.9, §10 |
| `Bastion.Cli` | `bastionc`, thin IPC client. | DESIGN.md §3.9 |
| `Bastion.Bar` | WinUI 3 appbar (own windows only). **Deferred to v0.3, not yet scaffolded.** | DESIGN.md §3.8, §10 |
| `Bastion.TestWindows` | Parameterized `CreateWindowExW` spawner for Tier 3 integration tests. | DESIGN.md §11 |

The core boundary is an **opaque `WindowId`**: no `HWND` ever enters
`Bastion.Core` or `Bastion.Layout`. Recycling, PIDs, and timestamps are
`Bastion.Win32` concerns only (DESIGN.md §3, §10).

## 4. Build / test / quality-gate commands

The solution file is `Bastion.slnx` (SLNX format, not legacy `.sln` — see
`dotnet sln`/`dotnet new sln` docs; .NET 10's default). CI (`.github/workflows/ci.yml`)
runs this exact sequence, mirrored by the `quality-gate` skill.

- `dotnet build Bastion.slnx --configuration Release -warnaserror` — full
  build; analyzers and `IsAotCompatible` trim/AOT analyzers (IL2xxx/IL3xxx)
  run here on every library project.
- `dotnet test Bastion.slnx --configuration Release` — native
  Microsoft.Testing.Platform runner (pinned via `global.json`). Filter by
  tier with xUnit v3 query syntax, e.g.
  `dotnet test --filter-query "/[Category!=Quarantined]"` to skip the
  flaky Tier 3 windows-latest job. `Bastion.Core`/`Bastion.Layout` also
  build and test standalone on Linux CI (everything else needs Windows).
- `dotnet format Bastion.slnx --verify-no-changes --severity error` — style
  gate, must be clean before commit.
- `dotnet publish src/Bastion.Daemon -r win-x64` /
  `dotnet publish src/Bastion.Cli -r win-x64` — the **only** valid AOT/trim
  validation, run for every `PublishAot=true` project. `dotnet build
  -t:Publish` does not exercise real trimming/AOT analysis and must never be
  used to claim AOT-safety.

Test tiers (DESIGN.md §11, detail in `docs/engineering/testing.md`):
1. Pure-library property tests (`Core`, `Layout`) — Linux CI.
2. Replay: recorded WinEvent traces through the real Coalescer/Reconciler.
3. Integration on `windows-latest` against `Bastion.TestWindows` — quarantined, expected flaky.
4. Windows Sandbox install-and-smoke — local/self-hosted only.
5. Behavior canaries — pin field-verified-but-undocumented behavior per Windows build.

**Before declaring any task done, run the `quality-gate` skill.**

## 5. Non-negotiable coding rules

- **Interop surface is `LibraryImport`/`[GeneratedComInterface]` only.**
  `DllImport` and `[ComImport]` are BANNED and enforced via
  `BannedSymbols.txt`/RS0030 — `[ComImport]` is unsupported under NativeAOT.
  CsWin32 config for `Bastion.Win32` needs all four together:
  `CsWin32RunAsBuildTask=true` + `DisableRuntimeMarshalling=true` (csproj), and
  `allowMarshaling=false` + `comInterop.useComSourceGenerators=true`
  (`NativeMethods.json`). `allowMarshaling=false` alone, without the other
  three, is a different, insufficient path that only emits COM structs, not
  `[GeneratedComInterface]` projections. Exact settings and rationale ->
  `docs/engineering/interop.md`.
- **Hook callbacks** (`WinEventProc`, `LowLevelKeyboardProc`) are `static
  [UnmanagedCallersOnly]` methods using `delegate* unmanaged` function
  pointers — never `Marshal.GetDelegateForFunctionPointer`/
  `GetFunctionPointerForDelegate` (the `Type`/`Delegate`-based overloads are
  `[RequiresDynamicCode]`; the generic overloads are unsupported for interop
  scenarios regardless) — avoid both APIs entirely, superseded by
  `delegate* unmanaged` function pointers.
  Every callback body is a catch-all try/catch — an escaping exception across
  the native boundary is undefined behavior under AOT. Context is passed via
  `GCHandle`, never a captured closure (`UnmanagedCallersOnly` forbids
  instance/closure methods). -> `docs/engineering/interop.md`
- **Never `SafeHandle`-wrap `HWND`/`HHOOK`/`HWINEVENTHOOK`.** These are not
  `CloseHandle`-family kernel objects; there is no correct universal
  `ReleaseHandle`. Keep raw `HWND` values at the `Bastion.Win32` boundary,
  matching the opaque-`WindowId` design. -> `docs/engineering/interop.md`
- **All shell COM calls run on one dedicated STA thread** — the
  source-generated COM layer does not marshal apartments for you. ->
  `docs/engineering/interop.md`
- **`Bastion.Core`/`Bastion.Layout` purity is absolute**: must build and test
  on Linux CI; no Win32 types, no `DateTime.Now`/`Task.Delay` (inject
  `TimeProvider`), no I/O. -> `pure-core` skill,
  `docs/engineering/concurrency-performance.md`
- **Concurrency**: bounded channels with explicit `BoundedChannelOptions` and
  an overflow policy; pump threads are raw dedicated `Thread`s, never
  `BackgroundService`/`Task.Run`; no closures on hot paths (hook callbacks,
  coalescer, executor). -> `docs/engineering/concurrency-performance.md`
- **Daemon**: `Host.CreateApplicationBuilder`; `[LoggerMessage]` source-gen
  logging only (no string interpolation in log calls, CA2254); state
  snapshots are `ImmutableArray<T>` and never default-constructed; guard
  recursion depth everywhere it can occur (layout tree operations). ->
  `docs/engineering/daemon-architecture.md`
- **Tooling**: central package management (no `Version` on
  `PackageReference`), pinned `LangVersion` 14.0 (never `latest`),
  `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`. ->
  `docs/engineering/quality-gates.md`
- **Every undocumented-behavior dependency gets a Tier-5 canary** before it
  ships, no exceptions. -> `verify-windows-api` skill

## 6. Docs index

| Doc | Owns |
|---|---|
| `docs/engineering/interop.md` | CsWin32 config, `[UnmanagedCallersOnly]` hooks, function-pointer callbacks, handle modeling, `[GeneratedComInterface]` — everything AOT-safe-interop for `Bastion.Win32`. |
| `docs/engineering/concurrency-performance.md` | Bounded-channel config, dedicated-thread vs task-pool, STA thread setup, GC/allocation policy, testable time, hot-path diagnostics. |
| `docs/engineering/daemon-architecture.md` | Generic Host wiring, hosted-service lifetime (incl. .NET 10 `BackgroundService` change), AOT-safe logging/config binding, immutable state snapshots, must-not-die policy, single-instance enforcement. |
| `docs/engineering/testing.md` | The five test tiers on xUnit v3/MTP, property-based layout testing, Tier-2 fake-adapter/replay seam, deterministic time, Verify snapshots, CI runner realities, coverage/mutation gates. |
| `docs/engineering/quality-gates.md` | CPM, `Directory.Build.props`/`.targets`, warning/analyzer escalation, `BannedApiAnalyzers` no-hacks enforcement, style, third-party analyzer layering, LangVersion policy, per-project AOT property placement, exact CI commands. |
| `docs/engineering/json-ipc-config.md` | `JsonSerializerContext` layout for config/IPC DTOs, polymorphic IPC commands, JSONC parsing/layering/hot-reload, published `JsonSchemaExporter` schema, named-pipe framing/security/multi-instance/cancellation. |

## 7. Skills index

| Skill | When |
|---|---|
| `win32-interop` | Writing or reviewing anything in `Bastion.Win32`. |
| `verify-windows-api` | **Mandatory** before any new Windows API call or reliance on new API behavior; wires up Tier-5 canaries. |
| `pure-core` | Touching `Bastion.Core` or `Bastion.Layout`. |
| `quality-gate` | Before completing any task, always. |
| `issue-authoring` | **Mandatory** before creating or substantially rewriting any GitHub issue — the backlog lives entirely in Issues (see README/CONTRIBUTING), so every one must stand alone without conversation context. |

## 8. Known uncertainties (do not assert as fact)

Each doc below has an "Uncertain / verify before relying" section — check it
before writing code in that area rather than trusting memory or this file:
`LibraryImport`'s `SetLastError` interaction under `DisableRuntimeMarshalling`
(`docs/engineering/interop.md`); `InvariantGlobalization` scope vs
`Bastion.Bar` (`docs/engineering/quality-gates.md`);
Serilog's current AOT-compatibility status (`docs/engineering/daemon-architecture.md`);
`[UnmanagedCallersOnly]` escape fail-fast semantics (`docs/engineering/interop.md`);
whether `ITaskbarList` strictly requires the dedicated STA thread or merely
benefits from it (`docs/engineering/interop.md`). When any of these turns out
to matter for the task at hand, run `verify-windows-api` rather than guessing.
