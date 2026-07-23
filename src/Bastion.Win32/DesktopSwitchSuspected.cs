namespace Bastion.Win32;

/// <summary>
/// A burst of <c>EVENT_OBJECT_CLOAKED</c>/<c>EVENT_OBJECT_UNCLOAKED</c> events on a window whose
/// <c>DWMWA_CLOAKED</c> currently reads nonzero — heuristic evidence a native virtual-desktop
/// switch just happened.
/// </summary>
/// <param name="Hwnd">The window observed cloaked/uncloaked.</param>
/// <remarks>
/// DESIGN.md §3.2/§4: an <b>observed-behavior heuristic, not a contract</b> — shell-cloaking of
/// inactive-desktop windows and cloak WinEvents firing on desktop switches are both real in the
/// field but absent from the documented contract of <c>DWM_CLOAKED_SHELL</c>/
/// <c>EVENT_OBJECT_CLOAKED</c>/<c>UNCLOAKED</c>. Already classified and cited in DESIGN.md §4, with
/// its own Tier-5 canary tracked by GitHub issue #33 — this intent is deliberately
/// <b>non-load-bearing</b>: DESIGN.md requires any consumer to always confirm it against the
/// documented <c>IsWindowOnCurrentVirtualDesktop</c>/<c>GetWindowDesktopId</c> re-checks, backstopped
/// by the 5 s reconciliation heartbeat (§3.4) if the events never arrive at all.
/// </remarks>
internal sealed record DesktopSwitchSuspected(nint Hwnd) : CoalescedIntent;
