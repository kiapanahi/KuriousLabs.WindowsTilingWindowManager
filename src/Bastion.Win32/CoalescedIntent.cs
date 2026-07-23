namespace Bastion.Win32;

/// <summary>
/// A typed, storm-free signal the Coalescer (DESIGN.md §3.2) emits after draining and debouncing
/// raw <see cref="WinEvent"/>s: either <see cref="WindowAppeared"/>, <see cref="WindowVanished"/>,
/// <see cref="DragEnded"/>, <see cref="ForegroundChanged"/>, <see cref="DesktopSwitchSuspected"/>,
/// or <see cref="GeometryDrift"/>.
/// </summary>
/// <remarks>
/// Same shape as <c>Bastion.Layout</c>'s <c>SplitTreeNode</c> hierarchy: an empty abstract record
/// base purely for discrimination, with each derived record independently declaring its own
/// members (one type per file, per this repo's <c>MA0048</c> convention) rather than hoisting the
/// shared <see langword="nint"/> <c>Hwnd</c> field onto this base. A future consumer (the
/// Reconciler, GitHub issue #4) is expected to <see langword="switch"/> on the concrete derived
/// type.
/// </remarks>
internal abstract record CoalescedIntent;
