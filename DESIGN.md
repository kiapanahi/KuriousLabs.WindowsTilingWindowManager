# Bastion — Design Document

**A self-healing, documented-API-only tiling window manager for Windows 11 25H2**
Repository: `KuriousLabs.WindowsTilingWindowManager` · Processes: `bastiond` (daemon), `bastionc` (CLI), `bastion-bar` (status bar)

---

## 1. Vision & Principles

Bastion is a tiling window manager for Windows 11 25H2 built under one hard, non-negotiable constraint: **only documented, supported, public Windows APIs**. No private `IVirtualDesktop` internals, no DLL injection, no `WH_CBT` global hooks, no reverse-engineered GUIDs, no undocumented registry reads. Where a capability is impossible without hacks, this document says so and specifies the best documented fallback. Where Bastion leans on *observed behavior of documented APIs* (behavior real in the field but absent from the API contract), that reliance is named explicitly, kept non-load-bearing, and canary-tested (§11 Tier 5).

That constraint forces a philosophy: **the Windows desktop is a hostile, eventually-consistent distributed system.** WinEvents are hints, never truth. Every cross-process call is a request the target may refuse, clamp, or ignore. Explorer, monitors, and apps can crash or storm at any moment. Bastion's core is therefore a *reconciliation engine* — a single-threaded actor that holds desired state, periodically re-derives observed state from authoritative reads (`EnumWindows` + `DWMWA_EXTENDED_FRAME_BOUNDS`), diffs, and converges — not an event-delta machine that corrupts when an event is missed.

Five principles govern every design decision:

1. **Reads are truth; events are scheduling hints.** Any event may be dropped. Correctness comes from a 5-second full re-sync heartbeat and distrust-triggered immediate re-syncs.
2. **Every feature has an explicit degradation ladder**: batch → async per-window → quarantine → float → unmanage. Nothing hard-fails; everything degrades to a labeled, user-visible state.
3. **Recoverability beats polish.** The workspace hiding primitive is chosen for its crash-recovery story, not its animation quality. A dead `bastiond` must never strand a window.
4. **Courtesy toward the shell and the user.** Bastion adopts user-snapped windows rather than fighting Snap; it tiles *around* windows it cannot manage; it explains its automatic decisions in toasts instead of moving things silently.
5. **Honesty about limits.** Documented-API limitations are surfaced in onboarding copy, the bar, and `bastion doctor` — never papered over with fragile tricks.

---

## 2. Non-Goals (with rationale)

- **Native virtual-desktop control.** Enumerating, creating, renaming, or switching desktops, reading desktop names, or moving *other* apps' windows between desktops. The entire documented surface is `IVirtualDesktopManager`'s three per-window methods; `MoveWindowToDesktop` fails with `E_ACCESSDENIED` for foreign HWNDs (Microsoft's archived WinSDK blog + field reports), and desktop names live in undocumented registry internals — even Microsoft's FancyZones reads undocumented `HKCU` values here. Bastion uses the documented surface read-only and builds its own workspaces (§4). The single concession: an off-by-default `SendInput` Win+Ctrl+Arrow passthrough, labeled best-effort.
- **Cloak-based workspace hiding.** Requires undocumented `IApplicationViewCollection`/`IApplicationView::SetCloak`; documented `DWMWA_CLOAK` works only on the caller's own windows. GlazeWM's cloak bug tail (Explorer-restart breakage #1273, permanently vanished windows #1358) is the cautionary tale. `SW_MINIMIZE` + a write-ahead recovery journal is the deliberate trade.
- **Managing SYSTEM-integrity UI and the secure desktop** (UAC prompts, lock screen, Ctrl+Alt+Del) — unreachable at any documented privilege level, including elevated and uiAccess.
- **Smooth animated movement of foreign windows** as a supported feature. `AnimateWindow` is own-thread-only; no cross-process move-animation API exists. Windows jump, as in FancyZones. A self-driven `SetWindowPos` tween (gated on `SPI_GETANIMATION`/`SPI_GETCLIENTAREAANIMATION`) is a possible post-1.0 opt-in.
- **Synchronously vetoing other apps' self-initiated moves/resizes** — requires in-context `WH_CBT` hooks (injection, banned). Bastion is reactive: observe, re-assert within a budget, then adapt.
- **Per-application suppression of Snap Assist/Snap Layouts/Win+Z** — no documented API; only the global `SPI_SETWINARRANGING` family exists (offered opt-in, captured and restored). Bastion's default is not suppression but *adoption* (§9).
- **Intercepting Win+L or secure-attention sequences**; reclaiming shell-reserved bare Win chords via `RegisterHotKey` (the `MOD_WIN` docs reserve them for the OS — an advisory reservation, not a failure mode: registration may even succeed while the shell still owns the combo, so Bastion does not fight for them; the opt-in LL hook covers most, never Win+L).
- **Reparenting foreign top-level windows** (cross-process `SetParent` is unsupported territory), drawing tab UI inside foreign windows, and managing MDI children — invisible to any external manager.
- **Cross-process window cosmetics as a supported capability.** There is no documented API for a third party to change another process's corner preference — `DWMWA_WINDOW_CORNER_PREFERENCE` is documented strictly as a per-app self-opt-in, and Microsoft guidance (Old New Thing, 2021-01-18) calls manipulating other apps' windows unsupported. See §3.6 and §13.1 for the off-by-default cosmetic flag.
- **Pinning windows to all virtual desktops** — no documented API; Bastion re-asserts only its *own* bar's desktop via `MoveWindowToDesktop`.
- **uiAccess=true distribution** — documented, but scoped to assistive technology, requires Authenticode + secure-location install (hostile to OSS forks), and grants input/z-order rights, not general repositioning of elevated windows.

---

## 3. System Architecture

```
                      ┌────────────────────────── bastiond.exe ───────────────────────────┐
 OS WinEvents ──────► │ Event Ingest ─► Coalescer ─► ┌──────────────────────────┐         │
 (SetWinEventHook,    │  (pump thread)  (intents)    │  RECONCILER (actor)      │         │
  narrow ranges)      │                              │  DesiredState ⇄ Observed │◄─ 5 s   │
                      │ Input Service ─────────────► │  diff → placement plan   │  heartbeat
 RegisterHotKey /     │  (pump thread)               └───────────┬──────────────┘         │
 WH_KEYBOARD_LL ────► │                                          ▼                        │
                      │ Monitor Topology Svc ──►  Layout Engine (pure lib)                │
 WM_DISPLAYCHANGE ──► │ Fullscreen Sentinel ──►   Placement Executor ─► SetWindowPos /    │
                      │ Workspace Manager ◄──────  (verify-after-move)   DeferWindowPos   │
                      │ Event Log (record/replay, causation IDs)                          │
                      │ IPC server (named pipes) ◄──────────────────────────────┐         │
                      └──────────────────────────────────────────────────────── │ ────────┘
                                bastionc.exe (CLI)  ·  bastion-bar.exe (appbar) ┘
```

All state mutation flows through the single-threaded **Reconciler** actor (komorebi's mutex-serialized `WindowManager` lesson, made structural). The **Layout Engine** is a pure, Win32-free library. Everything that touches an HWND lives in a thin adapter ring; the core sees only an opaque `WindowId`, so HWND recycling is an adapter concern and the state-bearing code is testable on Linux.

### 3.1 Event Ingest (WinEvent pump thread)

Sole push source for foreign-window signals. A dedicated thread runs `GetMessage`/`DispatchMessage` (documented requirement for out-of-context hooks) and registers **multiple narrow hook ranges** via `SetWinEventHook(WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)`: `EVENT_SYSTEM_FOREGROUND` (0x0003), MOVESIZESTART/END (0x000A–B), MINIMIZESTART/END (0x0016–17), OBJECT CREATE/DESTROY/SHOW/HIDE (0x8000–3), LOCATIONCHANGE/NAMECHANGE (0x800B–C), CLOAKED/UNCLOAKED (0x8017–18). The callback (an `[UnmanagedCallersOnly]` static) does nothing but filter (`hwnd != NULL && idObject == OBJID_WINDOW && idChild == CHILDID_SELF`), normalize via `GetAncestor(GA_ROOT)`, and enqueue `(hwnd, event, dwmsEventTime)` into a bounded channel. **Queue overflow is not an error**: it sets a reconcile-now flag and drops deltas — the Reconciler recovers from authoritative reads. `WINEVENT_SKIPOWNPROCESS` prevents feedback loops from Bastion's own `SetWindowPos` calls and overlay windows. `RegisterShellHookWindow` (`HSHELL_FLASH`, `HSHELL_RUDEAPPACTIVATED`) is wired as an optional secondary channel, never load-bearing — it carries the documented "not intended for general use" banner.

Note on CLOAKED/UNCLOAKED: the documented contract for these events is only the generic "sent when a window is cloaked/uncloaked" — there is no documented guarantee they fire on virtual-desktop switches. Bastion treats them strictly as scheduling hints (§3.2, §4), never as the source of truth.

### 3.2 Coalescer / Debouncer

Drains the channel and turns storms into typed intents: per-HWND ~75 ms coalescing keyed on `dwmsEventTime`; `EVENT_OBJECT_LOCATIONCHANGE` suppressed between a window's MOVESIZESTART and MOVESIZEEND (recompute once on END); bursts of CLOAKED/UNCLOAKED on windows whose `DWMWA_CLOAKED` reads nonzero collapse into one `DesktopSwitchSuspected` intent — an observed-behavior heuristic, not a contract (§4), always confirmed against documented `IsWindowOnCurrentVirtualDesktop` re-checks and backstopped by the heartbeat; NAMECHANGE re-evaluation rate-limited. Emits `WindowAppeared`, `WindowVanished`, `DragEnded`, `ForegroundChanged`, `DesktopSwitchSuspected`, `GeometryDrift`. Debounce values (75 ms coalesce, 150 ms admission grace, 500 ms display-settle) are engineering practice, not documented constants — they live in config.

### 3.3 Window Registry & Manageability Filter

Decides what is tile-able and owns identity. Filter: `hwnd == GetAncestor(GA_ROOT)`; `IsWindowVisible`; `DWMWA_CLOAKED == 0` (any nonzero cloak value → keep tracked, never tile, never forget; whether the window is "on another native desktop" is determined via the documented `IsWindowOnCurrentVirtualDesktop`/`GetWindowDesktopId`, **not** by the cloak reason flag — that inactive-desktop windows read `DWM_CLOAKED_SHELL` is observed behavior, per Raymond Chen's Old New Thing 2020-03-02 post, not an API contract); exstyle lacks `WS_EX_TOOLWINDOW` unless `WS_EX_APPWINDOW`; `GetWindow(GW_OWNER) == NULL` unless `WS_EX_APPWINDOW`; skip `WS_EX_NOACTIVATE`, empty rects, and `GetShellWindow()`. Windows are admitted on SHOW/UNCLOAKED — **never CREATE** (Electron/Chromium create hidden zero-sized windows) — and re-evaluated on NAMECHANGE (late titles drive rules). Rule identity: window-level `PKEY_AppUserModel_ID` via `SHGetPropertyStoreForWindow`, then process AUMID via `GetApplicationUserModelId`, then exe path via `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW`. Internal key is `(HWND, PID, first-seen timestamp)`; entries are purged only on `EVENT_OBJECT_DESTROY` — never by `IsWindow` polling, whose docs warn handles are recycled. UWP attribution (ApplicationFrameWindow → `EnumChildWindows` for the `Windows.UI.Core.CoreWindow` child → child PID → AUMID) is isolated behind `IUwpAttributionProvider` because the hosting structure is observed behavior, not contract; failure degrades to exe-path identity, retried on later SHOW/NAMECHANGE/FOREGROUND. The class-name blocklist (`Progman`/`WorkerW`/`Shell_TrayWnd` etc.) is user-editable config, not code — those names are shell implementation details.

### 3.4 Reconciler (core state actor)

Owns `DesiredState` (monitors → workspaces → layout trees) and `ObservedState` (last authoritative read per window). Three convergence triggers: **(1)** coalesced intents; **(2)** a periodic full re-sync every 5 s (`EnumWindows` + per-window `GetWindowRect` + `DWMWA_EXTENDED_FRAME_BOUNDS` + `DWMWA_CLOAKED` + `IsIconic`/`IsZoomed` + `GetWindowDesktopId`) that catches every missed event; **(3)** distrust escalation — verify-after-move mismatch, queue overflow, `WM_DISPLAYCHANGE`, or Explorer-restart signal forces an immediate re-sync. Convergence diffs desired rects against observed frame bounds and hands a placement plan to the Executor.

**Reassert budget (explicit counter).** Each managed window carries a named, config-tunable `reassertBudget` (default 2 per 2-second window, refilled on user-initiated layout changes). Post-DPI self-resizes and app-initiated geometry drift consume it; when exhausted, Bastion adapts to the window — records its effective minimum size or floats it — instead of entering a `SetWindowPos` war. A counter is cleaner to reason about and unit-test than an implicit time-window heuristic, and it appears verbatim in the event log.

**Event log with causation IDs.** Every intent, reconciliation decision, effect (syscall issued), and `ActualRectObserved` readback is appended to a bounded in-memory ring, each effect carrying the causation ID of the intent that produced it. This answers *why a window moved*, not just whether state converged: a support query can walk intent → syscall → observed-result mechanically, exonerating the heartbeat when an app moved itself and making fight-loop/clamp diagnosis a lookup. `bastion debug record` persists the ring plus subsequent traffic to a trace file (with an explicit PII/redaction pass: window titles and exe paths hashed unless `--include-titles`); `bastion debug replay` replays it deterministically through the pure core against the fake adapter, turning any user's jump-around report into a failing test.

### 3.5 Layout Engine (pure library)

Pure function: `Layout(tree, workArea, constraints, gaps) → [(WindowId, RECT visibleBounds)]`. Zero Win32 dependencies; property-tested cross-platform (no overlap, full coverage, min-size respect, stability under insert/remove/resize; single insert/remove perturbs only the affected subtree). Each algorithm owns its data structure outright (Whim's lesson). Details in §6.

### 3.6 Placement Executor

Turns plans into Win32 calls and verifies results. Per window: **(a)** hang probe `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 200 ms)` — hung windows are quarantined with backoff, never allowed to stall a batch (`IsHungAppWindow` is deliberately non-load-bearing); **(b)** state normalization — if `IsIconic`/`IsZoomed`/`IsWindowArranged`, restore *directly into the tile* via `SetWindowPlacement` with `rcNormalPosition` preset to the target and `showCmd = SW_SHOWNOACTIVATE` + `WPF_ASYNCWINDOWPLACEMENT`, so restore never flashes at stale geometry. This mechanism is documented (`WINDOWPLACEMENT.showCmd` accepts any `ShowWindow` `nCmdShow` value; `SW_SHOWNOACTIVATE` is `SW_SHOWNORMAL` without activation; `SetWindowPlacement` is documented to override the restored position), with caveats the Executor owns: `rcNormalPosition` is in *workspace* coordinates — origin at the **primary monitor's** work-area top-left, not each monitor's own work area — and **only** for top-level windows without `WS_EX_TOOLWINDOW`; for tool windows it is screen coordinates, so the Executor converts per window from the live extended style. If a supplied rect would land completely off-screen, the system silently adjusts the coordinates, so the window may not land exactly where asked — verify-after-move (e) remains the source of truth. The 'arranged' (snapped) restore path is documented only transitively (`SW_SHOWNOACTIVATE` is "similar to SW_SHOWNORMAL", whose entry covers arranged windows), so it is canary-tested (Tier 5) with `SW_RESTORE`-then-place as fallback; **(c)** invisible-border correction — read `GetWindowRect` and `DWMWA_EXTENDED_FRAME_BOUNDS` fresh per move (never cached per-class; recomputed after DPI changes) and apply the per-edge delta so visible edges land exactly on tile edges; **(d)** batch apply via `BeginDeferWindowPos`/`DeferWindowPos`/`EndDeferWindowPos` with `SWP_NOACTIVATE | SWP_NOZORDER` for one repaint cycle — if any `DeferWindowPos` fails, abandon the HDWP (documented rule) and fall back to per-window `SetWindowPos(SWP_ASYNCWINDOWPOS)`, which is also the standing mode for any window ever seen hung (`EndDeferWindowPos` sends synchronously; one hung window stalls the batch); **(e)** verify-after-move — re-read frame bounds under the same causation ID; a clamped result is recorded as the window's effective minimum in the constraint cache and the Reconciler re-lays-out around it.

Optional cosmetics — **off by default, explicitly undocumented**: `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_DONOTROUND)` on manage, `DWMWCP_DEFAULT` restored on unmanage, journaled for crash-recovery undo. There is no documented API for a third party to change another process's corner preference; the documented surface is per-app self-opt-in only (and even in-process the attribute is documented as "a hint to the system"), and Microsoft guidance (Old New Thing, 2021-01-18) calls manipulating other apps' windows unsupported — the target application owns the attribute and may reset it at any time (theme change, window re-creation, its own `DwmSetWindowAttribute` calls). Accordingly this flag is purely cosmetic: it may silently fail or be reverted, and **no layout, sizing, or gap correctness depends on it**. The default, documented-only path accounts for the system corner radius in gap styling; where square-corner visuals matter, Bastion draws them with its *own* overlay/border windows.

UIPI detection lives here. Empirically verified on this design's target (Windows 11 25H2, build 26200): `SetWindowPos`/`ShowWindow` from a medium-IL process against a high-IL window fail *detectably* — FALSE return with `GetLastError() == ERROR_ACCESS_DENIED` (5) — not silently. Two honesty notes: the documented UIPI contract enumerates handle validation, `SendMessage`/`PostMessage` (those *are* documented to silently drop), thread hooks, journal hooks, and DLL injection — `SetWindowPos`/`ShowWindow` blocking and its error code are *undocumented API behavior*, corroborated only by Microsoft's PowerToys product docs, so the specific error code is treated as observed behavior and canary-tested (Tier 5). The Executor therefore combines the failure signal with a token elevation check (`OpenProcess` + `GetTokenInformation`, or the `OpenProcess`-failure heuristic) and verify-after-move readback before marking a window `Unmanaged(Elevated)` — badged in the bar, handled per §9.

### 3.7 Workspace Manager

Implements Bastion-owned workspaces (§4). Hides outgoing sets with `ShowWindowAsync(SW_MINIMIZE)` (default) or `SW_HIDE` (per-rule opt-in, optionally with `ITaskbarList::DeleteTab/AddTab` curation); shows incoming sets by restoring directly into tiles via `SetWindowPlacement` (coordinate-space conversion per §3.6b). **Write-ahead journal ordering**: the HWND journal entry (window → workspace → pre-management `WINDOWPLACEMENT` → corner-preference state) is flushed to `%LOCALAPPDATA%\Bastion\hwnd-journal.json` *before* any hide call is issued, so a crash between journal and hide can never strand an unrecorded window. `bastion restore-windows` force-restores everything even with the daemon dead; clean shutdown restores all windows first. Never cloaks foreign windows; never parks windows off-screen (documented calls, unspecified behavior — Windows repositions off-screen windows on `WM_DISPLAYCHANGE`/RDP reconnect).

### 3.8 Input Service, Focus Manager, Monitor Topology, Sentinel, Bar

Summarized in §7, §8, and §9; APIs as listed in the component table above.

### 3.9 IPC + CLI + Config

Named-pipe JSON IPC (request/reply command pipe + broadcast state-subscription pipe), ACL'd to the interactive user via `PipeSecurity`; `bastionc` is a thin client; the schema is language-agnostic so third-party bars/scripts integrate. Config is JSONC with a published JSON Schema in `%USERPROFILE%\.config\bastion\`: user config layered over a **shipped, curated community rules file** (§9) — games, PiP players, Teams/OneDrive popups, installers pre-classified float/ignore so the first hour never depends on the user building a blocklist. Hot-reload watches the *directory* (editors do atomic rename-replace) with 200 ms debounce; parse into a new immutable config and atomically swap; parse errors keep the old config and raise a bar notification. Runtime overrides flow over IPC (komorebi's static-config post-mortem lesson; no config-as-compiled-code).

**Extensibility is two-tier**: (1) in-process `ILayoutEngine` plugins — pure code that consumes `(tree, workArea, constraints, gaps)` and returns rects, structurally unable to touch Win32; (2) everything else out-of-process over the IPC schema (bars, scripting, automation). This avoids workspacer's config-as-compiled-code trap without banning power users.

### 3.10 Lifecycle, Elevation & Crash Recovery

Startup adopts existing windows via an `EnumWindows` pass, journaling pre-management placement per window. Shutdown/crash restores placements, corner prefs, and any flipped SPI values. Autostart: `HKCU\...\CurrentVersion\Run` by default (visible in Task Manager Startup); opt-in "manage elevated windows" mode installs a Task Scheduler logon task with `TASK_RUNLEVEL_HIGHEST` (one elevated consent at install, no per-logon UAC — FancyZones' sanctioned pattern); the elevated daemon spawns user apps de-elevated via the scheduler. A tiny watchdog relaunches a crashed daemon and runs restore-windows first if the journal's dirty flag is set.

---

## 4. The Workspace Model (the crux)

**The documented reality.** The entire documented virtual-desktop surface is `IVirtualDesktopManager` (CLSID_VirtualDesktopManager): `IsWindowOnCurrentVirtualDesktop`, `GetWindowDesktopId`, `MoveWindowToDesktop`. There is no documented way to enumerate, create, rename, or switch desktops; no current-desktop query; no switch notification; and `MoveWindowToDesktop` fails with `E_ACCESSDENIED` for foreign HWNDs. Native desktops therefore *cannot* be the workspace mechanism without the banned internals. Every mature WM (komorebi, GlazeWM, Whim) independently converged on WM-owned workspaces; Bastion follows.

**The hiding primitive, ranked by failure mode.** Cloaking foreign windows requires undocumented `IApplicationViewCollection::SetCloak` — excluded (GlazeWM's issue tail shows Explorer-restart breakage and permanently lost windows). `SW_HIDE` removes taskbar/Alt-Tab presence, so a crashed WM strands invisible windows, and some Electron apps misbehave (komorebi marks Hide end-of-life). Off-screen parking relies on unspecified behavior and is undone by display changes. **`ShowWindowAsync(SW_MINIMIZE)` wins** despite its costs (visible minimize animation; apps observe `SIZE_MINIMIZED` — media players pause, games release devices) because its recovery story is perfect: even if Bastion dies mid-switch, every window remains reachable from the taskbar, since minimized state is kernel/user32 state, not shell COM state. This trade is stated *verbatim in onboarding copy*: "Native desktops = OS-integrated switches via Task View. Bastion workspaces = keyboard-instant, windows minimize to the taskbar." Managed expectations, not a perceived bug.

**Workspace switch**: write-ahead journal → minimize outgoing set → for each incoming window `SetWindowPlacement{rcNormalPosition = its tile, showCmd = SW_SHOWNOACTIVATE}` (with the per-window coordinate-space conversion and off-screen-adjustment caveats of §3.6b) so restore lands directly in the tile with no flash → one Defer batch to true up.

**Native-desktop coexistence.** Bastion scopes management to windows where the documented `IsWindowOnCurrentVirtualDesktop` is true and `DWMWA_CLOAKED == 0`. Windows with any nonzero cloak value are kept tracked but untouched; Bastion deliberately does **not** depend on the specific reason flag. Honesty note: that windows on inactive virtual desktops are shell-cloaked (typically reading `DWM_CLOAKED_SHELL`) is observed behavior — corroborated by Raymond Chen's Old New Thing post of 2020-03-02 and relied on by komorebi/GlazeWM/Chromium — but the API docs define `DWM_CLOAKED_SHELL` only as "cloaked by the Shell" with no virtual-desktop linkage, and `EVENT_OBJECT_CLOAKED`/`UNCLOAKED` are documented only generically as "sent when a window is cloaked/uncloaked," with no guarantee they fire on desktop switches. These are therefore *heuristics, not contracts*, and canary-tested (Tier 5). Desktop switches are accordingly *inferred*: coalesced CLOAKED/UNCLOAKED bursts serve as a scheduling hint, always confirmed by documented `IsWindowOnCurrentVirtualDesktop`/`GetWindowDesktopId` re-checks, with the 5 s heartbeat as the polling fallback if the events never arrive; desktop GUIDs are learned lazily via `GetWindowDesktopId` (no enumeration exists). Bastion maintains an **independent, persistent layout tree per (native-desktop GUID × monitor)** — each OS desktop the user visits is tiled with its own state, advisory and never load-bearing, so tiling state doesn't bleed across desktops. Known limit: an idle desktop switch (no windows on either side) emits no events, so the active partition can lag until the next event or heartbeat sweep — accepted, documented. Optional, feature-flagged: "native desktop switch passthrough" commands inject Win+Ctrl+Arrow via `SendInput` (documented function driving a shell UX convention — no-ops if rebound, eaten by UIPI when an elevated window is foreground; labeled best-effort). Bastion re-pins its *own* bar after each suspected switch via `MoveWindowToDesktop` on its own HWND, since "pin to all desktops" has no documented API.

---

## 5. Window Lifecycle & Tracking

**Window opens**: app calls `CreateWindow` → `EVENT_OBJECT_CREATE` (ignored for admission) → app shows it → `EVENT_OBJECT_SHOW` → ingest filters and enqueues → Coalescer applies ~150 ms admission grace (lets Electron/UWP self-size; UWP attribution retried here) → Registry runs the manageability filter + rules → Reconciler inserts into the focused workspace's layout tree → Layout → Executor (hang-probe, normalize, border deltas, Defer batch) → verify-after-move readback → if clamped, record effective min size and re-layout once (reassert budget prevents loops) → bar notified over IPC. A late `EVENT_OBJECT_NAMECHANGE` re-runs rules and may re-home the window.

**Window vanishes**: `EVENT_OBJECT_DESTROY` purges the registry entry and journal row; HIDE/CLOAKED keep the slot (UWP suspend and desktop switches are hides, never destroys). UWP splash windows churn through SHOW/HIDE/DESTROY and are handled by re-evaluating on every transition rather than caching a verdict.

**Heartbeat**: every 5 s the Reconciler re-syncs from reads regardless of events — the self-healing backstop that makes missed, dropped, or storm-suppressed events non-fatal by design.

**Monitor unplugged mid-operation**: any in-flight batch targeting the dead monitor fails or lands stale — harmless, because the Topology Service (§8) debounces, invalidates, re-enumerates, re-homes, and forces a full reconciliation.

---

## 6. Layout Engine

**Data structures.** Each workspace owns an n-ary **split tree** (i3-style): internal nodes are horizontal/vertical splits with float ratios; leaves are containers holding an ordered *stack* of one or more windows (komorebi's containers as first-class — stacked windows share a tile, surfaced via the bar, since Bastion cannot draw tabs inside foreign windows). Each algorithm owns its own structure: **dwindle/spiral** (the zero-config default — new windows split the focused tile alternately, sane gaps out of the box, no split commands to learn), **manual split tree** (i3 semantics, opt-in mode), **master-stack**, and **monocle** are separate engines producing rects from the same inputs.

**Constraints are learned, not queried.** `WM_GETMINMAXINFO` is un-interrogable cross-process (pointer-bearing message, framework-dynamic values, hang risk — hard limit). The Executor's verify-after-move readback feeds an effective-min-size cache, seeded with `GetSystemMetrics(SM_CXMINTRACK/SM_CYMINTRACK)` floors, persisted per rule-key with decay so app updates can shrink it.

**Min-size conflict ladder**: (1) re-solve giving the constrained tile exactly its minimum, redistributing along its split axis; (2) if the axis cannot absorb it, allow bounded overlap (constrained window on top, flagged in the bar); (3) if the minimum exceeds the configured tolerable fraction of the work area, **auto-float and toast the reason**: *"Spotify won't shrink below 800 px — floated."* Documented-API limitations read as transparency, not bugs; the toast offers one click to persist a rules-file entry. Non-resizable windows (no `WS_THICKFRAME`), owned dialogs, and tool windows float by default via rules.

**Coordinates.** All engine rects are *visible* bounds. The Executor alone translates to `SetWindowPos` coordinates using per-window, per-move `GetWindowRect` vs `DWMWA_EXTENDED_FRAME_BOUNDS` deltas, optionally overlapping 1 px using `DWMWA_VISIBLE_FRAME_BORDER_THICKNESS` for seamless seams. Because the process is PerMonitorV2, all math is physical pixels in one virtual-screen space — cross-DPI moves are plain `SetWindowPos`, with one expected post-move self-resize from PMv2 targets (`WM_DPICHANGED`) absorbed by a single budgeted re-assertion.

---

## 7. Input & Focus

**Hotkeys, two tiers.** Tier 1 (default): `RegisterHotKey` with `MOD_NOREPEAT` on a dedicated pump thread, defaulting to Alt-based and Win+Shift/Win+Ctrl chords; every registration is probed at startup via the documented contract — a zero `BOOL` return plus `GetLastError` — and conflicts surfaced. Two honesty notes: the docs hedge conflict failure as "typical" (some pre-existing OS default hotkeys, e.g. PrintScreen→Snipping Tool, are documented as overridable when the app's window is foreground), and the specific `ERROR_HOTKEY_ALREADY_REGISTERED` (1409) code is observed behavior, not contractually bound to `RegisterHotKey` — so Bastion treats *any* failed registration as a conflict rather than matching the code. The `MOD_WIN` docs reserve Windows-key shortcuts for the OS, but that reservation is advisory, not a failure mode: `RegisterHotKey(MOD_WIN…)` may *succeed* even for combos the shell handles, so Bastion never uses registration failure to detect OS-claimed Win chords — it simply defaults away from bare Win+X. Tier 2 (opt-in): `WH_KEYBOARD_LL` on its own thread that *only enqueues and returns* (LowLevelHooksTimeout is hard-capped at 1000 ms and timed-out hooks are silently removed), enabling leader-key/modal schemes and swallowing most Win chords — never Win+L or the secure desktop. A watchdog re-installs the LL hook if a synthetic health-check keystroke pattern stops arriving. `SendInput` is used only for modifier-state cleanup after swallowed chords and the opt-in desktop-switch passthrough. Known gap: hotkeys and the LL hook go dead while an elevated window has focus under a medium-IL Bastion (UIPI) — real, and reflected in Microsoft's UIAccess design doc and PowerToys admin docs, though absent from the `SetWindowsHookEx` API contract itself — surfaced by `doctor` and the bar; the documented remedies are elevated mode or a signed uiAccess=true binary (a non-goal, §2).

**Focus.** Bastion changes focus *only synchronously from the user's own input*, satisfying `SetForegroundWindow`'s documented "calling process received the last input event" condition; it verifies with `GetForegroundWindow`, retries once via `AllowSetForegroundWindow`, and otherwise accepts the system's taskbar-flash degradation (denial is documented as possible even when conditions hold). Mouse-follows-focus: on `EVENT_SYSTEM_FOREGROUND`, `SetCursorPos` to the frame-bounds center, with self-caused-change suppression. Focus-follows-mouse delegates to the OS via `SPI_SETACTIVEWINDOWTRACKING` (+TRKZORDER/TRKTIMEOUT) — the system activates, bypassing foreground-lock issues; original SPI values are captured on start and restored on exit. **Never** `AttachThreadInput` or synthetic-Alt tricks. Monocle/stack restacking uses per-window `SetWindowPos(hwndInsertAfter, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` honoring documented z-order/owned-window rules.

**Drag-to-swap.** `EVENT_SYSTEM_MOVESIZESTART`/`END` bracket the drag; a WM-owned click-through highlight window (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` + `SetLayeredWindowAttributes`) previews the drop tile; re-tile happens once on MOVESIZEEND via a Defer batch.

---

## 8. Multi-Monitor & DPI

**Enumeration and identity.** `EnumDisplayMonitors` + `GetMonitorInfoW`; tiles go into `rcWork`, never `rcMonitor`; virtual-screen coordinates can be negative. **Runtime correlation** (session-scoped): HMONITOR ↔ CCD path via `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` + `DisplayConfigGetDeviceInfo`, matching `GET_SOURCE_NAME.viewGdiDeviceName == MONITORINFOEX.szDevice` — both are documented as the GDI device name for the source, so the join lives in a documented namespace, with two caveats: the equality is an inference from shared documented semantics rather than a single stated guarantee, and the join keys on the *source* — in clone topologies one HMONITOR/`szDevice` maps to **multiple** targets/`monitorDevicePath`s, so the mapping is not 1:1. **Persistence identity**: `monitorDevicePath` is *not* a documented stable key — Microsoft explicitly disclaims stability for this identifier (the `DisplayTarget.StableMonitorId` remarks note that reconnecting the same monitor to a different GPU or a different output on the same GPU can change the device interface path, which is exactly what `monitorDevicePath` is). Bastion therefore persists monitor-keyed state on `Windows.Devices.Display.Core.DisplayTarget.StableMonitorId` — the only identifier Microsoft documents as stable for persisting monitor-specific settings (Windows 10 1809+) — correlated to `monitorDevicePath` at runtime via `DisplayTarget.DeviceInterfacePath`. Where StableMonitorId is unobtainable, the fallback re-match strategy uses the documented EDID manufacture/product ID fields of `DISPLAYCONFIG_TARGET_DEVICE_NAME` (plus `connectorInstance` for identical twins), with `monitorDevicePath` kept only as the session key whose cross-reconnection stability is explicitly not guaranteed.

**Topology changes.** Triggers: `WM_DISPLAYCHANGE`, `WM_SETTINGCHANGE(SPI_SETWORKAREA)` (taskbar/appbar moved), `WM_DEVICECHANGE`; all debounced ~500 ms (multiple messages per hotplug). Then: treat **all** cached HMONITORs as suspect and re-enumerate (documented: when `WM_DISPLAYCHANGE` is sent, any monitor *may* have been removed from the desktop, invalidating its HMONITOR — the documented response is to re-check/re-enumerate, and Bastion conservatively rebuilds the whole map rather than trusting any cached handle), re-match by the stable identity above, re-home orphaned workspaces to the primary, force full reconciliation. **Dock/undock is a headline scenario**: workspace→monitor assignments persist keyed on the stable identity (StableMonitorId, EDID fallback); when a monitor returns, displaced workspaces *migrate home* automatically. A monitor unplugged mid-batch is safe by construction — the Executor re-resolves targets per plan and any failure just schedules reconciliation.

**DPI.** PerMonitorV2 manifest is non-negotiable: the frame-bounds-vs-window-rect subtraction requires both rects in the same physical coordinate space. `DWMWA_EXTENDED_FRAME_BOUNDS` is documented as *not* DPI-virtualized while `GetWindowRect` is, so the two rects are comparable only when the calling thread is per-monitor DPI aware (V1 or V2 — under DPI-unaware or system-DPI-aware contexts `GetWindowRect` returns virtualized coordinates while the extended frame bounds remain physical); Bastion uses Per-Monitor V2, the recommended per-monitor mode. Bastion's own windows honor `WM_DPICHANGED`'s suggested rect. Foreign-window DPI is read via `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`, not `GetDpiForWindow` (returns 96 for unaware targets); non-client math uses `AdjustWindowRectExForDpi`.

---

## 9. Edge Cases & Degradation Matrix

| Situation | Detection | Behavior | Ladder floor |
|---|---|---|---|
| Elevated window (UIPI) | `SetWindowPos`/`ShowWindow` fail with `ERROR_ACCESS_DENIED` (5) — observed on 25H2, undocumented (§3.6) — + token elevation check + verify-after-move | `Unmanaged(Elevated)`; **its screen area is subtracted from the work area** so tiles go *around* it, not under it; bar badge offers one-click "2 elevated windows unmanaged — enable admin mode?" onboarding to the scheduled-task elevated mode | Unmanage; SYSTEM-IL/secure desktop permanently unreachable |
| User snaps a window (Snap Layouts) | `IsWindowArranged` + `GetWindowPlacement` | **Adopt, don't fight**: the window becomes floating-at-its-snap-position; Snap is a first-class user gesture, and the missing per-app Snap-suppression API becomes a non-problem. A command re-tiles it on demand (`SW_RESTORE`-into-tile). Global `SPI_SETWINARRANGING` flip remains opt-in, captured-and-restored | Float |
| Hung app | `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 200 ms)` | Quarantine: skip in Defer batches, manage via `SWP_ASYNCWINDOWPOS` after recovery probe | Quarantine |
| Fullscreen game | `SHQueryUserNotificationState` polled at 2 s (docs: no start/stop notification exists) + eager re-check on FOREGROUND + geometric check (frame == `rcMonitor`, no `WS_CAPTION`) | Per-monitor management pause; bar drops to bottom on `ABN_FULLSCREENAPP`; per-game rules in shipped rules file | Pause monitor |
| Electron/Chromium | Zero-size hidden CREATE; late titles; min-size clamps; post-DPI bounds re-assertion | Admit on SHOW + grace; NAMECHANGE re-rules; constraint cache; one budgeted re-assert then adapt | Float + toast |
| UWP/ApplicationFrameHost | Attribution adapter (observed structure, not contract) | Manage the GA_ROOT frame only; cloak = hide, never destroy; attribution failure → exe-path identity, retried | Degraded identity |
| Explorer restart | broadcast `TaskbarCreated` message | Core pipeline has no shell dependency and keeps running; re-register appbar (ABM_NEW sequence), re-init `ITaskbarList`, force full reconciliation; SW_MINIMIZE workspaces survive because minimized state is kernel state, not shell COM state | None needed |
| Event storm / queue overflow | Bounded channel overflow flag | Drop deltas, reconcile from reads; heartbeat backstop | Full re-sync |
| Self-moving app | GeometryDrift intents exceed reassert budget | Auto-float + toast + suggested rules entry logged | Float |
| HWND recycling | — | Identity is `(HWND, PID, first-seen)`; purge only on `EVENT_OBJECT_DESTROY` (documented `IsWindow` recycling warning) | — |

**Curated rules seed.** Bastion ships a community-maintained rules database pre-classifying games (ignore), PiP players (float, always-on-top respected), Teams/OneDrive/notification popups (ignore), installers and setup wizards (float). User rules layer on top; the auto-float toast's "remember this" writes into the user layer.

**Diagnostics.** `bastion doctor` runs at startup and on demand: hotkey registration conflicts (which chord, which failure), LL-hook health (installed? watchdog heartbeat age?), elevated-window count and the admin-mode hint, UWP attribution-adapter health, appbar registration state, journal dirty flag, and heartbeat timing. Silent degradation becomes a user-runnable report. `bastion debug record/replay` (§3.4) turns field bugs into deterministic traces.

---

## 10. Technology Stack & Project Structure

**C# on .NET 10, NativeAOT-first.** `Microsoft.Windows.CsWin32` with `CsWin32RunAsBuildTask=true`, `DisableRuntimeMarshalling`, and `"allowMarshaling": false`; COM (`IVirtualDesktopManager`, `ITaskbarList`, `ITaskService`, `IPropertyStore`) via `[GeneratedComInterface]`/`ComWrappers` (built-in `[ComImport]` is unsupported under AOT); `WinEventProc` and `LowLevelKeyboardProc` as `[UnmanagedCallersOnly]` statics. Rationale: the daemon is I/O-bound on window events; the hook path allocates nothing; GlazeWM/workspacer/Whim prove the runtime; the contributor pool beats Rust for a Windows-desktop OSS project.

```
src/
  Bastion.Core/        # pure: WindowId, state, reconciler logic, event log — no Win32 types
  Bastion.Layout/      # pure: ILayoutEngine + dwindle/tree/master-stack/monocle
  Bastion.Win32/       # adapter ring: ingest, executor, topology, input, workspace mgr
  Bastion.Daemon/      # bastiond composition root, IPC server
  Bastion.Cli/         # bastionc
  Bastion.Bar/         # WinUI 3 / Windows App SDK (AppWindow: own windows only, per docs)
  Bastion.TestWindows/ # parameterized Win32 test-window spawner
```

The core boundary uses an **opaque `WindowId`** — no HWND enters state-bearing code; recycling, PIDs, and timestamps are adapter concerns, and untestable-on-Windows-only code shrinks to `Bastion.Win32`.

Manifests: `<dpiAwareness>PerMonitorV2</dpiAwareness>`, `asInvoker`. IPC: message-mode named pipes with `PipeSecurity` (chosen over AF_UNIX: peer identity + ACLs). Config: JSONC + JSON Schema, `System.Text.Json` source-gen. Packaging: portable zip + Inno Setup + winget manifest; **MSIX rejected** (blocks install-time elevated scheduled-task registration; virtualization surprises).

---

## 11. Testing Strategy

- **Tier 1 — pure libraries** (`Core`, `Layout`): property tests on Linux CI. Invariants: no overlap, full coverage, min-size respect, determinism, subtree-local perturbation.
- **Tier 2 — replay**: an `IWindowSystem` fake replays recorded WinEvent storm/reentrancy traces through the real Coalescer + Reconciler; every `debug record` trace from the field becomes a regression test. This tier carries the main regression burden.
- **Tier 3 — integration**: quarantined CI job on `windows-latest` driving the purpose-built test-window spawner (parameterized styles/min-sizes via `CreateWindowExW` — never Notepad), asserting via `DWMWA_EXTENDED_FRAME_BOUNDS` readbacks. Expected flaky; quarantined so it can't erode trust.
- **Tier 4 — sandbox smoke**: Windows Sandbox `.wsb` install-and-smoke, local/self-hosted only.
- **Tier 5 — behavior canaries**: a dedicated CI suite pinning *field-verified-but-undocumented* behaviors against each new Windows build: `MoveWindowToDesktop` own-process success / foreign-process `E_ACCESSDENIED`, shell-cloaking of inactive-desktop windows (`DWMWA_CLOAKED` nonzero / `DWM_CLOAKED_SHELL` value) and cloak-burst desktop-switch inference, UIPI `ERROR_ACCESS_DENIED` failures of `SetWindowPos`/`ShowWindow` against elevated windows, `ERROR_HOTKEY_ALREADY_REGISTERED` on hotkey conflicts, `SW_SHOWNOACTIVATE` restore-from-arranged semantics, cross-process `DWMWA_WINDOW_CORNER_PREFERENCE` acceptance, ApplicationFrameHost child structure. This is the cheapest early-warning system for servicing-update breakage; failures open tracked issues before users hit them.

---

## 12. Phased Roadmap

- **v0.1 (MVP)**: ingest → coalescer → reconciler → executor pipeline; dwindle default layout; single workspace per monitor; verify-after-move + constraint cache; heartbeat re-sync; `RegisterHotKey` tier 1; journal + `restore-windows`; shipped rules seed; PerMonitorV2.
- **v0.2**: Bastion workspaces with write-ahead journal ordering; `SetWindowPlacement`-into-tile switching (with per-window coordinate-space conversion); monitor topology service with StableMonitorId/EDID-keyed persistence; Snap adoption; elevated-window detection + tile-around + badge.
- **v0.3**: status bar (appbar registration, badges, toasts, onboarding prompts); drag overlay + drag-to-swap; manual split-tree and master-stack/monocle engines; focus manager complete (SPI xmouse, mouse-follows-focus).
- **v0.4**: event log + causation IDs + `debug record/replay` with redaction; `doctor`; LL-hook tier 2 with watchdog; per-(desktop-GUID × monitor) layout partitions; desktop-switch passthrough flag.
- **v0.5**: elevated mode (scheduled task, de-elevated spawn); dock/undock migrate-home; `ILayoutEngine` plugin loading; behavior-canary CI suite.
- **v1.0**: hardening against the full edge-case matrix; docs stating every hard limit; CONTRIBUTING policy holding the documented-API line (ecosystem pressure for cloak-quality switching is expected and must be resisted publicly, or the constraint erodes one PR at a time).

---

## 13. Open Questions & Accepted Risks

1. **Cross-process `DwmSetWindowAttribute` cosmetics** (corner preference, transitions) have *no documented cross-process support at all* — the documented surface is per-app self-opt-in (and a "hint," even then), and Microsoft guidance calls manipulating other apps' windows unsupported; the target app owns the attribute and can reset it at any time. Shipped off by default as a purely cosmetic flag; journaled; no layout, sizing, or gap correctness depends on it — the default styling accounts for the system corner radius, and required square-corner visuals come from Bastion's own overlay windows. Canary-tested (Tier 5).
2. **SW_MINIMIZE UX ceiling**: minimize animations, `SIZE_MINIMIZED`-reactive apps, taskbar buttons for "hidden" windows. Mitigations (per-rule `SW_HIDE`, `ITaskbarList` curation) exist, but the UX will never match cloak-based WMs. This is the price of the constraint; onboarding says so up front.
3. **Desktop-switch inference** rests on undocumented behavior (shell-cloaking of inactive-desktop windows and cloak WinEvents firing on switches — heuristics per §4), and lags on idle switches — accepted; documented `IsWindowOnCurrentVirtualDesktop`/`GetWindowDesktopId` re-checks are the truth source and the heartbeat bounds staleness at ≤5 s even if the events never fire.
4. **UIPI-based elevated-window classification** keys on an observed, undocumented failure mode (`ERROR_ACCESS_DENIED` from `SetWindowPos`/`ShowWindow`; the documented UIPI contract covers only messaging, hooks, and injection) plus token checks, and can still misclassify briefly; canary-tested, and the badge + onboarding prompt convert the support ticket into a feature discovery.
5. **ApplicationFrameHost structure** is observed behavior; a servicing change degrades UWP identity to exe-path. Isolated, health-logged, canary-tested.
6. **Dead-hotkey windows**: LL-hook silent removal and `RegisterHotKey` conflicts cannot be fully eliminated — and conflict detection itself is best-effort (failure-on-conflict is documented only as "typical," and registration can *succeed* for shell-handled Win chords, so OS-claimed combos are not reliably detectable) — watchdog + `doctor` shrink them.
7. **Should the reassert budget refill on focus change or only on user layout commands?** Field-tune via config; the event log makes the tuning data-driven.
8. **Self-driven move tween** (documented `SetWindowPos` frames gated on animation SPIs) — deferred past 1.0; it multiplies moving parts against the robustness priority.
9. **SetWindowPlacement restore-from-arranged** is documented only transitively (`SW_SHOWNOACTIVATE` "similar to SW_SHOWNORMAL", whose entry covers arranged windows); if exact arranged-state semantics prove load-bearing, validate empirically (Tier 5) or fall back to explicit `SW_RESTORE`-then-place.
