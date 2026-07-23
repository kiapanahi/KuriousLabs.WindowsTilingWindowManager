# Contributing to Bastion

Read [`DESIGN.md`](DESIGN.md) first — it is the architecture and rationale
document; this file is not. [`docs/engineering/`](docs/engineering/) covers
implementation-level rules (interop, concurrency, daemon hosting, testing,
quality gates, JSON/IPC). `CLAUDE.md` is a project-instructions index
written for AI coding assistants working in this repo, but the rules it
points to apply to every contributor, human or not.

## The one hard rule

Bastion ships only documented, supported, public Windows APIs. No private
`IApplicationView`/`IApplicationViewCollection` internals, no DLL injection,
no `WH_CBT`, no reverse-engineered GUIDs, no undocumented registry reads or
writes. See `DESIGN.md` §1–§2 for the full rationale and what it rules out.
Observed-but-undocumented API *behavior* may be relied on only when it is
labeled as observed (not contractual), kept non-load-bearing, and pinned by
a Tier-5 behavior canary — see `docs/engineering/testing.md` §7 and
`DESIGN.md` §13.

## Picking up work

All work is tracked as [GitHub issues](../../issues), organized into
[milestones](../../milestones) matching `DESIGN.md` §12's phased roadmap
(v0.1 through v1.0). There is no separate roadmap document or project
board — the issue tracker *is* the backlog.

Not sure where to start? The pinned **Getting started** issue lists every
currently-unblocked issue in the active milestone — everything else has an
explicit `blocked by` relationship in its own sidebar pointing at what needs
to land first.

Every issue is required to be actionable by someone who has never seen any
conversation about it — using only the issue text, whatever it links to,
and the files in this repo. If an issue seems to assume context you don't
have, that's a defect in the issue: open a comment saying what's missing,
or send a PR fixing the issue body. See
[`.claude/skills/issue-authoring/SKILL.md`](.claude/skills/issue-authoring/SKILL.md)
for the exact standard issues are held to.

### Labels

- `type:*` — `feature`, `bug`, `chore`, `docs`, `risk`, `test`
- `area:*` — `core`, `layout`, `win32`, `daemon`, `cli`, `bar`, `ipc`, `ci`, `docs`
- `good-first-issue` — approachable without deep context on the rest of the system

## Before opening a PR

Run the same gates CI runs (`docs/engineering/quality-gates.md` §8):

```bash
dotnet build Bastion.slnx --configuration Release -warnaserror
dotnet test Bastion.slnx --configuration Release --filter-query "/[Category!=Quarantined]"
dotnet format Bastion.slnx --verify-no-changes --severity error

# Only if your change touches Bastion.Win32/Daemon/Cli, any .csproj/Directory.Build.*,
# or anything CsWin32/interop-adjacent — see the quality-gate skill / quality-gates.md §8:
dotnet publish src/Bastion.Daemon -r win-x64 --configuration Release
dotnet publish src/Bastion.Cli -r win-x64 --configuration Release
```

`Bastion.Core`/`Bastion.Layout` must also build and test cleanly on Linux —
every other project requires Windows.

## Adding a new Windows API call

Read `docs/engineering/interop.md` and `DESIGN.md` §13 before writing the
call (Claude Code sessions: run the `verify-windows-api` skill, which is
mandatory here). Every reliance on undocumented-but-observed behavior needs
a Tier-5 canary before it ships.
