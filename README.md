# Bastion

A self-healing tiling window manager for Windows 11 25H2, built under one
non-negotiable constraint: **only documented, supported, public Windows
APIs.** No private `IApplicationView`/`IApplicationViewCollection`
internals, no DLL injection, no `WH_CBT`, no reverse-engineered GUIDs, no
undocumented registry reads or writes. See [`DESIGN.md`](DESIGN.md) for the
full rationale, architecture, and edge-case matrix — read that first.

## Why

There is no documented Windows API for third-party tiling window
management. Every existing tiling WM for Windows leans on some combination
of undocumented COM interfaces, DLL injection, or global hooks to get a
polished experience. Bastion takes the opposite bet: build the best tiling
WM possible using *only* what Microsoft documents and supports, and be
honest — in the product, not just the source — about what that constraint
costs. `DESIGN.md` §2 and §13 catalogue exactly what this rules out and what
it accepts as a trade-off.

## Status

Early scaffolding. The solution structure, build/quality-gate tooling, and
five-tier test strategy are in place; the event-ingest → reconcile → layout
→ placement pipeline described in `DESIGN.md` §3 is not yet built. All
tracked work lives in [Issues](../../issues), organized into
[milestones](../../milestones) matching the phased roadmap in
[`DESIGN.md` §12](DESIGN.md#12-phased-roadmap) — there is no separate
roadmap document or project board.

## Architecture at a glance

Three processes: `bastiond` (daemon, does all the work), `bastionc` (CLI,
thin IPC client), `bastion-bar` (WinUI 3 status bar — not yet scaffolded,
deferred to v0.3). Full diagram: [`DESIGN.md` §3](DESIGN.md#3-system-architecture).

| Project | Role |
|---|---|
| `Bastion.Core` | Pure state/reconciler/event-log. No Win32 types. Linux-CI-tested. |
| `Bastion.Layout` | Pure `ILayoutEngine` implementations (dwindle, tree, master-stack, monocle). |
| `Bastion.Win32` | Adapter ring — the only project touching `HWND`, CsWin32, or COM. |
| `Bastion.Daemon` | `bastiond` composition root, hosted services, IPC server. |
| `Bastion.Cli` | `bastionc`, thin IPC client. |
| `Bastion.Bar` | WinUI 3 appbar. Deferred to v0.3, not yet scaffolded. |
| `Bastion.TestWindows` | Parameterized `CreateWindowExW` spawner for integration tests. |

The core boundary is an opaque `WindowId`: no `HWND` ever enters
`Bastion.Core` or `Bastion.Layout`.

## Building

Requires the .NET 10 SDK, pinned exactly in [`global.json`](global.json).

```bash
dotnet build Bastion.slnx --configuration Release -warnaserror
dotnet test Bastion.slnx --configuration Release --filter-query "/[Category!=Quarantined]"
dotnet format Bastion.slnx --verify-no-changes --severity error
dotnet publish src/Bastion.Daemon -r win-x64 --configuration Release
dotnet publish src/Bastion.Cli -r win-x64 --configuration Release
```

This is the same sequence CI runs — see
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) and
[`docs/engineering/quality-gates.md`](docs/engineering/quality-gates.md) §8
for exactly what each step catches. `Bastion.Core`/`Bastion.Layout`
additionally build and test on Linux; every other project requires Windows.

## Documentation

- [`DESIGN.md`](DESIGN.md) — architecture, rationale, and the full edge-case matrix. Read this first.
- [`docs/engineering/`](docs/engineering/) — interop, concurrency, daemon hosting, testing, quality gates, and JSON/IPC implementation detail.
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — how work is tracked and what an issue needs before it's ready to pick up.

## License

[MIT](LICENSE)
