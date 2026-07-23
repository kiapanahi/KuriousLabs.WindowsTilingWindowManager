---
name: issue-authoring
description: Checklist for creating or editing GitHub issues in this repo so any contributor can pick one up and start work using only the issue text, its linked material, and the project files — no conversation history required. Mandatory whenever creating or substantially rewriting an issue.
---

# issue-authoring

## Purpose

This repo's backlog lives entirely in GitHub Issues (no separate roadmap doc
or project board — see CLAUDE.md/README.md). That only works if every issue
is self-contained: understandable and actionable by someone who has never
seen the conversation, chat session, or design discussion that produced it.

## When to use

- Before creating any GitHub issue.
- Before substantially rewriting an existing issue's scope.
- Before closing an issue as anything other than "completed" — the reason
  must be written into the closing comment, not left implicit.

## The rule

An issue is ready only if everything needed to start work is available from:

1. The issue's own text.
2. Material it links to (a DESIGN.md section, a docs/engineering/*.md
   section, another issue, a specific file/line in the repo).
3. The project files as they exist in the repo right now.

If a decision behind the issue — why this approach, why this scope, why now
— only exists in a chat transcript, that is a defect in the issue, not an
acceptable gap. Transcribe the decision into the issue body (or, if it's a
durable cross-cutting rule, into a doc under `docs/` and link it) before
considering the issue ready to pick up.

## Required shape

- **Title**: concrete and action-oriented ("Implement the WinEvent ingest
  pump", not "Ingest pump" or "Events").
- **Problem / goal**: what should exist and why, in plain language. Do not
  assume the reader has read DESIGN.md — point them to it instead of
  restating it wholesale, but don't skip stating the goal in your own words
  either.
- **References**: specific section numbers, e.g. `DESIGN.md §3.1`,
  `docs/engineering/interop.md §3`, a `file.cs:42`-style pointer to existing
  code. Never just "see the design doc."
- **Acceptance criteria**: a checklist a reviewer can verify literally
  against the diff — not "works correctly," but the specific, checkable
  behaviors DESIGN.md or the linked doc actually specifies.
- **Labels**: at least one `type:*` and one `area:*` (see taxonomy below).
  Add `good-first-issue` only if it's genuinely approachable without having
  internalized the rest of the architecture.
- **Milestone**: the DESIGN.md §12 roadmap phase it belongs to, or leave
  unset for unscheduled backlog/risk-tracking issues — don't leave the
  scheduling ambiguous in body text when the milestone field can just say it.

## Label taxonomy

`type:feature` · `type:bug` · `type:chore` · `type:docs` · `type:risk`
(tracks an accepted risk / undocumented-behavior dependency from DESIGN.md
§13, not a concrete unit of work) · `type:test`

`area:core` · `area:layout` · `area:win32` · `area:daemon` · `area:cli` ·
`area:bar` · `area:ipc` · `area:ci` · `area:docs`

`good-first-issue` — orthogonal to the above, stack with any type/area pair.

## Forbidden

- Referencing "as discussed", "per our conversation", "as Claude suggested",
  or any other pointer to a conversation the next reader cannot see.
- Leaving a non-obvious design decision implicit because "it's the obvious
  approach" — if DESIGN.md doesn't already say it, write it down in the issue.
- Filing an issue with no acceptance criteria, or acceptance criteria too
  vague to check against a diff.
- Shipping an issue with no labels, or with a `type:*`/`area:*` missing.
