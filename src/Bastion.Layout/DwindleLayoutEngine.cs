using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// Minimal binary-spiral ("dwindle") layout: the first window takes half the remaining area,
/// alternating horizontal/vertical split orientation down an ordered window list.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §3.5, §6, §12 v0.1): this operates over a flat ordered window list, not the
/// persistent split-tree (insert/remove-stable subtree identity) design DESIGN.md ultimately
/// commits to — it satisfies the no-overlap, full-coverage, and determinism invariants
/// (docs/engineering/testing.md §3) by construction, but does not yet satisfy the
/// subtree-locality invariant for an arbitrary insert position (only append-at-tail is
/// currently stable). Replace with a real <c>SplitTree</c>-backed engine before v0.1 ships.
///
/// Deliberately iterative, not recursive, per the non-negotiable "guard recursion depth
/// everywhere it can occur (layout tree operations)" rule (CLAUDE.md §5) — an arbitrarily large
/// window count never grows the call stack here.
/// </remarks>
public sealed class DwindleLayoutEngine : ILayoutEngine
{
    public IReadOnlyList<WindowPlacement> Solve(
        IReadOnlyList<WindowId> windows,
        Rect workArea,
        LayoutConstraints constraints,
        LayoutGaps gaps)
    {
        ArgumentNullException.ThrowIfNull(windows);

        if (windows.Count == 0)
        {
            return [];
        }

        var placements = new List<WindowPlacement>(windows.Count);
        Rect area = workArea.Deflate(gaps.Outer);
        var halfInnerGap = gaps.Inner / 2.0;
        var horizontal = true;

        for (var i = 0; i < windows.Count - 1; i++)
        {
            Rect first;
            Rect rest;

            if (horizontal)
            {
                var mid = area.Left + (area.Width / 2.0);
                first = area with { Right = mid - halfInnerGap };
                rest = area with { Left = mid + halfInnerGap };
            }
            else
            {
                var mid = area.Top + (area.Height / 2.0);
                first = area with { Bottom = mid - halfInnerGap };
                rest = area with { Top = mid + halfInnerGap };
            }

            placements.Add(new WindowPlacement(windows[i], first));
            area = rest;
            horizontal = !horizontal;
        }

        placements.Add(new WindowPlacement(windows[^1], area));
        return placements;
    }
}
