using System.Runtime.InteropServices;

namespace Bastion.Core;

/// <summary>
/// A pure, Win32-free axis-aligned rectangle used for Layout Engine input/output and, since
/// GitHub issue #4, the Reconciler's <see cref="DesiredWorkspace"/>/<see cref="ObservedWindow"/>/
/// <see cref="PlacementInstruction"/> geometry.
/// </summary>
/// <remarks>
/// Deliberately distinct from any Win32 <c>RECT</c> — <c>Bastion.Win32</c>'s adapter ring owns the
/// conversion at the boundary (DESIGN.md §3, §10). Coordinates are <see cref="double"/> rather
/// than <see cref="int"/> so split arithmetic (ratio-based dwindle/master-stack splits) stays
/// exact without intermediate rounding; the adapter ring rounds to integer device pixels only at
/// the final <c>SetWindowPos</c> boundary. Originally a <c>Bastion.Layout</c>-only type; relocated
/// here alongside <see cref="Bastion.Core.ILayoutEngine"/> (see that type's remarks) because
/// <c>Bastion.Layout</c> already references <c>Bastion.Core</c> for <see cref="WindowId"/>, and
/// the Reconciler (this project) needs this type too — the reverse reference would be circular.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct Rect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    /// <summary>True if this rectangle and <paramref name="other"/> share any interior area.</summary>
    /// <remarks>
    /// Uses strict inequalities so two rectangles that merely touch along a shared edge (the
    /// common case at a split boundary, or across an inner gap) are not considered overlapping —
    /// this is exactly the "no-overlap" invariant testing.md §3 asserts over Layout output.
    /// </remarks>
    public bool IntersectsWith(Rect other) =>
        Left < other.Right && other.Left < Right &&
        Top < other.Bottom && other.Top < Bottom;

    /// <summary>Shrinks all four edges inward by <paramref name="amount"/> (an outer gap).</summary>
    public Rect Deflate(double amount) =>
        new(Left + amount, Top + amount, Right - amount, Bottom - amount);
}
