---
name: verify-windows-api
description: Verify a Windows API against official Microsoft docs before first use; classify documented contract vs observed behavior; wire a Tier-5 canary when behavior is undocumented. Mandatory for any new API call — enforces the no-hacks constraint.
---

# verify-windows-api

Makes DESIGN.md §1's constraint mechanical: every new API surface gets a documented-contract
citation, and every observed-but-undocumented reliance gets labeled, made non-load-bearing, and
pinned by a Tier-5 canary before it ships.

## When to use

Before adding any Windows API call, COM interface, window message, DWM attribute, registry read,
or behavioral assumption not already used in the codebase — or when reviewing a PR that does. This
includes: a new CsWin32 `NativeMethods.txt` entry, a new COM interface consumed/implemented, a new
`WM_*`/`EVENT_*` constant, a new `DwmGetWindowAttribute`/`DwmSetWindowAttribute` flag, any registry
key read, and any claim in a comment or PR description of the form "Windows does X" that isn't
already cited in DESIGN.md.

Do not skip this because the API "looks obviously fine" or matches a pattern from another WM
(komorebi/GlazeWM/Whim/FancyZones) — their source is not a documentation source; re-verify
independently.

## Procedure

1. **Fetch the official documentation.** Use `microsoft_docs_search` first for a quick overview,
   then `microsoft_docs_fetch` on the specific API reference page for the full contract (remarks,
   parameters, return values, thread/apartment requirements). If no page exists on
   `learn.microsoft.com` for the API, function, message, or attribute in question, treat it as
   **presumptively banned** — check DESIGN.md §2 (Non-Goals) before writing any code that depends
   on it; most private-surface temptations (`IApplicationViewCollection`, undocumented registry
   desktop names, `WH_CBT`, DLL injection) are already enumerated there with the rejected
   alternative and rationale.

2. **Classify every property the change relies on** into exactly one bucket. A single API call
   can straddle more than one bucket — classify each behavioral claim separately, not the API as a
   whole:

   a. **DOCUMENTED CONTRACT** — the doc page states it outright. Quote the exact sentence and
      record the URL (+ section/heading) in the code comment and PR description. This is the only
      bucket you may treat as load-bearing without further mitigation.

   b. **OBSERVED BEHAVIOR** — true in the field but absent from the documented contract: error
      codes not contractually bound to that specific API (e.g. `ERROR_ACCESS_DENIED` from
      `SetWindowPos`/`ShowWindow` under UIPI — DESIGN.md §3.6), event-firing patterns not
      guaranteed by the docs (cloak bursts correlating with desktop switches — DESIGN.md §4),
      shell/process internal structure (`ApplicationFrameHost` child window layout — DESIGN.md
      §9), or cloak-reason semantics (`DWM_CLOAKED_SHELL` on inactive-desktop windows — DESIGN.md
      §3.3).

   c. **UNDOCUMENTED/PRIVATE** — private COM interfaces, reverse-engineered GUIDs, undocumented
      registry values as a *load-bearing* data source, code injection, or any technique requiring
      `[ComImport]` semantics CsWin32/GeneratedComInterface cannot express. **HARD STOP**: banned
      by DESIGN.md §1 outright. Find the documented fallback (see DESIGN.md §2/§4 for the
      fallback already chosen for virtual desktops, cloaking, corner preference, etc.) or, if
      genuinely no fallback exists, that capability becomes a new §2 non-goal — do not implement
      it via the private route "just this once."

3. **For every bucket-(b) item, apply the DESIGN.md pattern** (§3.6 UIPI detection and §4 cloak
   inference are the canonical worked examples — read them before writing your own):

   - **Make it non-load-bearing.** Name the documented truth source that confirms or corrects the
     observed behavior, and verify correctness survives the observed behavior vanishing entirely
     on a future build. Concrete mechanisms already established in this codebase: an authoritative
     read (`EnumWindows` + `DWMWA_EXTENDED_FRAME_BOUNDS`), a documented re-check
     (`IsWindowOnCurrentVirtualDesktop`/`GetWindowDesktopId` rather than trusting the cloak reason
     flag), a verify-after-move readback, or the 5-second reconciliation heartbeat as the ultimate
     backstop. If you cannot name a truth source that bounds the staleness/wrongness when the
     heuristic fails, the design is incomplete — go back to step 2 and reconsider whether this is
     really bucket (b) or should be treated as (c).
   - **Label it in code.** A comment at the call site (or on the field/property it feeds) stating:
     this is OBSERVED BEHAVIOR, what was observed, on which Windows build (default: 25H2, build
     26200 per DESIGN.md's target — record the actual build you tested if different), and which
     canary test pins it. Do not let an observed-behavior reliance sit uncommented — the next
     reader must be able to find the citation without re-deriving it.
   - **Wire a Tier-5 canary.** Add or extend a test in the canary suite tagged
     `[Trait("Category", "Canary")]` that asserts the observed behavior against a real system, so a
     servicing update flips the canary red before users hit the regression in the field.
     DESIGN.md §11 Tier 5 lists the existing canary set (`MoveWindowToDesktop` own vs. foreign
     process, shell-cloaking of inactive-desktop windows, UIPI `ERROR_ACCESS_DENIED`,
     `ERROR_HOTKEY_ALREADY_REGISTERED`, `SW_SHOWNOACTIVATE` restore-from-arranged,
     cross-process `DWMWA_WINDOW_CORNER_PREFERENCE`, `ApplicationFrameHost` child structure) —
     match that style for any new canary rather than inventing a new test shape.
   - **Note the runner caveat.** Per `docs/engineering/testing.md` §7, hosted `windows-latest`
     runs Windows Server 2025 (build 26100), not the 25H2/26200 target DESIGN.md pins Tier 5
     against. Mark any new canary as needing the self-hosted runner or Tier 4 Windows Sandbox leg
     for true target-build coverage; a hosted-runner-only canary is a weaker proxy, not equivalent
     validation.

4. **Cross-check the flagged-uncertain findings tracked across the owning engineering docs.** If
   the item you're verifying is one of the flagged-uncertain findings already on record —
   `SetLastError`/`GetLastError` semantics under `LibraryImportAttribute` +
   `DisableRuntimeMarshalling`, `InvariantGlobalization` scope for Bastion.Bar's culture-aware
   formatting, Serilog (or whichever logging library) AOT compatibility, `[UnmanagedCallersOnly]`
   fail-fast/crash semantics on an escaping exception, or `ITaskbarList`'s STA/apartment
   requirement — verify it against primary docs **now**, in this change, rather than deferring.
   Update the corresponding doc's "uncertain" section — `docs/engineering/interop.md` for the
   CsWin32/COM items, `docs/engineering/testing.md` or `docs/engineering/concurrency-performance.md`
   as applicable, `docs/engineering/quality-gates.md` for the `InvariantGlobalization` scope item
   (its "InvariantGlobalization — candidate for Daemon/Cli, not yet repo-wide" section), or
   `docs/engineering/daemon-architecture.md` for the Serilog/logging-library AOT compatibility item
   (its "[uncertain] Serilog's Native AOT compatibility..." section) — with the outcome. Never let an
   "uncertain" entry get silently treated as settled fact elsewhere in the codebase — promote it
   explicitly, with the citation, or leave it marked uncertain.

5. **Record the verdict** so the next session doesn't re-litigate the same question:
   - Rejected as banned (bucket c, no fallback) → add it to DESIGN.md §2 with the same rationale
     style already used there (what's banned, why, what documented alternative was chosen instead).
   - Adopted as documented contract or as a mitigated observed-behavior reliance → update the
     relevant DESIGN.md section (§3.x/§4/§8/§9 as applicable) or the owning
     `docs/engineering/*.md` file (see routing below) with the citation and classification.

## Which doc owns the detail

Do not duplicate the full ruleset here — cite and defer to the owning document for anything beyond
the verification procedure itself:

- `docs/engineering/interop.md` — CsWin32 settings (`CsWin32RunAsBuildTask`,
  `useComSourceGenerators`, `allowMarshaling`), `[UnmanagedCallersOnly]` hook-callback rules,
  `delegate* unmanaged` function pointers, handle-type modeling (why HWND/HHOOK never get a
  `SafeHandle`), and `[GeneratedComInterface]`/`ComWrappers` COM authoring.
- `docs/engineering/concurrency-performance.md` — bounded-channel sizing, dedicated-thread vs.
  task-pool decisions, STA thread ownership for shell COM calls, GC/allocation policy, testable
  time (`TimeProvider`/`FakeTimeProvider`), and hot-path diagnostics.
- `docs/engineering/daemon-architecture.md` — Generic Host wiring, hosted-service lifetime rules,
  AOT-safe logging/config binding, immutable state snapshots, must-not-die exception policy,
  single-instance enforcement.
- `docs/engineering/testing.md` — Tier 1-5 mechanics, xUnit v3/MTP, property-based testing,
  Tier-2 replay seam, Verify snapshots, CI runner realities (including the `windows-latest`
  build-26100-vs-26200 gap this skill's step 3 references), coverage, mutation testing.
- `docs/engineering/quality-gates.md` — CPM, `Directory.Build.props`/`.targets`,
  analyzer/warning escalation, `BannedApiAnalyzers`, style enforcement, `LangVersion` policy,
  per-project AOT property placement, exact CI gate commands.

## Hard stops (never proceed past these without a documented citation)

- No `learn.microsoft.com` page for the API/message/attribute → presumptively banned; check §2
  first.
- Reliance classified as UNDOCUMENTED/PRIVATE (bucket c) → banned outright; find the documented
  fallback or make it a non-goal. Never ship it "temporarily."
- An OBSERVED BEHAVIOR reliance with no named documented truth source that bounds its failure →
  incomplete design; do not merge until a truth source and canary exist.
- A claim asserted as fact in a PR/comment without a URL + quoted sentence, when it's the first use
  of that API surface in the repo → re-verify before merging, don't take it on faith from a prior
  WM's source or from training-data recall.
