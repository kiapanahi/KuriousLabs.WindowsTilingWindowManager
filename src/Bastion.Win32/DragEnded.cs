namespace Bastion.Win32;

/// <summary>A user-driven move/resize interaction finished (<c>EVENT_SYSTEM_MOVESIZEEND</c>).</summary>
/// <param name="Hwnd">The window whose drag/resize ended.</param>
/// <remarks>
/// DESIGN.md §7: "EVENT_SYSTEM_MOVESIZESTART/END bracket the drag... re-tile happens once on
/// MOVESIZEEND via a Defer batch." <see cref="Coalescer"/> emits this immediately (never
/// debounced) alongside one final <see cref="GeometryDrift"/> for the same window — see its
/// handling of <c>MOVESIZEEND</c> for why both are emitted together.
/// </remarks>
internal sealed record DragEnded(nint Hwnd) : CoalescedIntent;
