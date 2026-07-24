using System.Runtime.InteropServices;

namespace Bastion.Core;

/// <summary>
/// One window's solved, gap-adjusted bounds within the work area. Originally a
/// <c>Bastion.Layout</c>-only type; relocated here alongside <see cref="ILayoutEngine"/> — see
/// that type's remarks for why.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct WindowPlacement(WindowId WindowId, Rect Bounds);
