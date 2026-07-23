namespace Bastion.Layout;

/// <summary>
/// Split direction for a <see cref="SplitNode"/>. <see cref="Horizontal"/> means the two
/// children sit side by side, dividing the parent rect's <em>width</em> — not "a horizontal
/// dividing line," which is the opposite convention some tiling window managers (e.g. i3) use
/// for the same word. This matches the flat <c>DwindleLayoutEngine</c> placeholder it replaces.
/// </summary>
public enum SplitOrientation
{
    /// <summary>Children are side by side; the split divides the parent's width.</summary>
    Horizontal,

    /// <summary>Children are stacked top/bottom; the split divides the parent's height.</summary>
    Vertical,
}
