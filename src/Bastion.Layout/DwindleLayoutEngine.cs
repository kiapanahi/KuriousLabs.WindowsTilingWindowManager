using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// Zero-config binary-spiral ("dwindle") layout: each new window splits off from the
/// previously-added window's own tile, alternating horizontal/vertical orientation.
/// </summary>
/// <remarks>
/// Backed by <see cref="SplitTree"/> (DESIGN.md §3.5, §6, §12 v0.1) rather than a flat window
/// list, so a single insert/remove perturbs only the affected subtree
/// (docs/engineering/testing.md §3's <c>InsertPerturbsOnlyAffectedSubtree</c> property) for an
/// arbitrary insert position — the flat-list predecessor only supported stable append-at-tail.
///
/// <see cref="ILayoutEngine.Solve"/>'s signature takes a flat, ordered list with no tree handle
/// persisted across calls, so this engine rebuilds the whole tree via <c>windows.Count - 1</c>
/// sequential <see cref="SplitTree.Insert"/> calls on every invocation — O(n²) reconstruction,
/// none of <see cref="SplitTree"/>'s incremental-reuse benefit realized at this call site. That's
/// expected here: the point of this type is establishing correct persistent primitives for a
/// future stateful consumer (e.g. the Reconciler, DESIGN.md §3.4), not speeding up this call site.
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

        SplitTree tree = SplitTree.Empty.InsertFirst(windows[0]);
        bool horizontal = true;

        for (int i = 1; i < windows.Count; i++)
        {
            tree = tree.Insert(windows[i - 1], windows[i], horizontal ? SplitOrientation.Horizontal : SplitOrientation.Vertical);
            horizontal = !horizontal;
        }

        return SplitTreeLayout.Solve(tree, workArea, constraints, gaps);
    }
}
