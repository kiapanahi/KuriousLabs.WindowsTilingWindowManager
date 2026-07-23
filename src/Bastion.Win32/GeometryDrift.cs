namespace Bastion.Win32;

/// <summary>
/// A window's geometry may have changed: an un-bracketed <c>EVENT_OBJECT_LOCATIONCHANGE</c>, or the
/// one authoritative recompute <c>EVENT_SYSTEM_MOVESIZEEND</c> triggers alongside
/// <see cref="DragEnded"/>.
/// </summary>
/// <param name="Hwnd">The window whose geometry may have drifted.</param>
/// <param name="DwmsEventTimeMs">
/// The raw <c>dwmsEventTime</c> this intent was last (re)computed from — carried through, not a
/// rect, since the Coalescer never reads geometry itself (DESIGN.md §1: "reads are truth, events
/// are hints"; an authoritative <c>GetWindowRect</c>/<c>DWMWA_EXTENDED_FRAME_BOUNDS</c> read is the
/// Reconciler/Executor's job, §3.4/§3.6). Kept for the reassert-budget accounting the Reconciler
/// (GitHub issue #4) will eventually do against this timestamp.
/// </param>
internal sealed record GeometryDrift(nint Hwnd, uint DwmsEventTimeMs) : CoalescedIntent;
