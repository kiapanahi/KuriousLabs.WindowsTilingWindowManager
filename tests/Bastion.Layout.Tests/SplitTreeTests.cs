using Bastion.Core;
using FsCheck.Xunit;

namespace Bastion.Layout.Tests;

/// <summary>
/// Tier 1 property tests (docs/engineering/testing.md §3) for <see cref="SplitTree"/> and
/// <see cref="SplitTreeLayout"/> — the persistent tree that replaces
/// <see cref="DwindleLayoutEngine"/>'s former flat window list.
/// </summary>
[Properties(Arbitrary = [typeof(SplitTreeGenerators)])]
public sealed class SplitTreeTests
{
    private static readonly LayoutConstraints s_noMinSize = new(0, 0);

    [Property]
    public bool NoOverlap(SplitTree tree, int rawWidth, int rawHeight, int rawOuterGap, int rawInnerGap)
    {
        Rect workArea = ClampWorkArea(rawWidth, rawHeight, minDimension: 600);
        LayoutGaps gaps = ClampGaps(rawOuterGap, rawInnerGap);
        IReadOnlyList<WindowPlacement> placements = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, gaps);

        for (int i = 0; i < placements.Count; i++)
        {
            for (int j = i + 1; j < placements.Count; j++)
            {
                if (placements[i].Bounds.IntersectsWith(placements[j].Bounds))
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Property]
    public bool Determinism(SplitTree tree, int rawWidth, int rawHeight, int rawOuterGap, int rawInnerGap, int rawMinWidth, int rawMinHeight)
    {
        Rect workArea = ClampWorkArea(rawWidth, rawHeight, minDimension: 600);
        LayoutGaps gaps = ClampGaps(rawOuterGap, rawInnerGap);
        LayoutConstraints constraints = ClampSmallConstraints(rawMinWidth, rawMinHeight);

        IReadOnlyList<WindowPlacement> first = SplitTreeLayout.Solve(tree, workArea, constraints, gaps);
        IReadOnlyList<WindowPlacement> second = SplitTreeLayout.Solve(tree, workArea, constraints, gaps);

        return first.SequenceEqual(second);
    }

    /// <summary>
    /// Full coverage, isolated from gap bookkeeping: with zero gaps, no-overlap (tested
    /// separately) plus an exact area match rules out both overlap and dead space at once.
    /// </summary>
    [Property]
    public bool FullCoverageInZeroGapCase(SplitTree tree, int rawWidth, int rawHeight)
    {
        Rect workArea = ClampWorkArea(rawWidth, rawHeight, minDimension: 600);
        IReadOnlyList<WindowPlacement> placements = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, new LayoutGaps(0, 0));

        double totalArea = placements.Sum(p => p.Bounds.Width * p.Bounds.Height);
        return AlmostEqual(totalArea, workArea.Width * workArea.Height);
    }

    /// <summary>
    /// Full coverage with real (nonzero) gaps, isolated to a single split: no-overlap under a
    /// nonzero gap doesn't rule out a dead sliver from e.g. an off-by-one in the half-gap split
    /// math, since it only checks for overlap, not missing coverage. Generating over
    /// <see cref="SplitTreeLayout.SplitRect"/> directly (rather than walking a whole solved tree)
    /// tests the arithmetic in isolation, without depending on tree-traversal correctness too.
    /// </summary>
    [Property]
    public bool SplitRectReconstitutesParent(int rawWidth, int rawHeight, bool horizontal, int rawRatio, int rawGap)
    {
        Rect area = ClampWorkArea(rawWidth, rawHeight, minDimension: 600);
        SplitOrientation orientation = horizontal ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        double ratio = 0.05 + ((Math.Abs((long)rawRatio) % 91) * 0.01);
        double gap = Math.Abs((long)rawGap) % 4;

        (Rect first, Rect second) = SplitTreeLayout.SplitRect(area, orientation, ratio, gap);

        if (orientation == SplitOrientation.Horizontal)
        {
            return AlmostEqual(first.Width + gap + second.Width, area.Width)
                && AlmostEqual(first.Top, area.Top) && AlmostEqual(first.Bottom, area.Bottom)
                && AlmostEqual(second.Top, area.Top) && AlmostEqual(second.Bottom, area.Bottom)
                && AlmostEqual(first.Left, area.Left) && AlmostEqual(second.Right, area.Right);
        }

        return AlmostEqual(first.Height + gap + second.Height, area.Height)
            && AlmostEqual(first.Left, area.Left) && AlmostEqual(first.Right, area.Right)
            && AlmostEqual(second.Left, area.Left) && AlmostEqual(second.Right, area.Right)
            && AlmostEqual(first.Top, area.Top) && AlmostEqual(second.Bottom, area.Bottom);
    }

    /// <summary>
    /// The flat, non-cascading clamp's guarantee, exercised with a generous margin (see
    /// <see cref="SplitTreeGenerators"/>'s remarks on the worst-case chain depth this bounds
    /// against) rather than an aggregate, always-correct one — see
    /// <see cref="SplitTreeLayout.Solve"/>'s own remarks for why the stronger guarantee was
    /// rejected (it breaks subtree-locality).
    /// </summary>
    [Property]
    public bool MinSizeRespected(SplitTree tree, int rawWidth, int rawHeight, int rawMinWidth, int rawMinHeight)
    {
        Rect workArea = ClampWorkArea(rawWidth, rawHeight, minDimension: 8000);
        LayoutConstraints constraints = ClampSmallConstraints(rawMinWidth, rawMinHeight);

        IReadOnlyList<WindowPlacement> placements = SplitTreeLayout.Solve(tree, workArea, constraints, new LayoutGaps(0, 0));

        return placements.All(p => p.Bounds.Width >= constraints.MinWidth - Epsilon && p.Bounds.Height >= constraints.MinHeight - Epsilon);
    }

    /// <summary>
    /// The required metamorphic property (docs/engineering/testing.md §3, DESIGN.md §3.5):
    /// inserting one window at an arbitrary position perturbs only that window's own new
    /// placement — every other window's solved rect is byte-identical before and after.
    /// </summary>
    [Property]
    public bool InsertPerturbsOnlyAffectedSubtree(SplitTree tree, int rawWidth, int rawHeight, int anchorSeed, bool horizontal, int rawRatio)
    {
        ArgumentNullException.ThrowIfNull(tree);

        IReadOnlyList<WindowId> existing = tree.Windows;
        if (existing.Count == 0)
        {
            return true;
        }

        Rect workArea = ClampWorkArea(rawWidth, rawHeight, minDimension: 600);
        LayoutGaps gaps = new(2, 2);
        WindowId anchor = existing[(int)(Math.Abs((long)anchorSeed) % existing.Count)];

        // SplitTreeGenerators always mints window IDs 0..count-1, so `count` itself is guaranteed
        // unused — a test-local fact about the generator, not a general SplitTree guarantee.
        var newWindow = WindowId.FromOpaqueValue((ulong)existing.Count);
        SplitOrientation orientation = horizontal ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        double ratio = 0.3 + ((Math.Abs((long)rawRatio) % 41) * 0.01);

        var before = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, gaps)
            .ToDictionary(p => p.WindowId, p => p.Bounds);

        SplitTree after = tree.Insert(anchor, newWindow, orientation, ratio);
        var afterPlacements = SplitTreeLayout.Solve(after, workArea, s_noMinSize, gaps)
            .Where(p => p.WindowId != newWindow)
            .ToDictionary(p => p.WindowId, p => p.Bounds);

        // The anchor's own leaf is exactly what Insert splits, so its rect is expected to shrink
        // — "only the affected subtree" means every window OTHER than the anchor and the new
        // one, not literally every window (docs/engineering/testing.md §3's illustrative example
        // elides the anchor entirely, which is why it doesn't need this exclusion explicitly).
        return before.Keys.Where(id => id != anchor).All(id => afterPlacements.TryGetValue(id, out Rect r) && r == before[id]);
    }

    /// <summary>
    /// A second metamorphic property covering <see cref="SplitTree.Remove"/>'s correctness:
    /// inserting a window and then immediately removing it must reconstitute the exact original
    /// layout for every remaining window (the collapse in <c>Remove</c> exactly undoes the split
    /// <c>Insert</c> created), not just an equivalent one.
    /// </summary>
    [Property]
    public bool InsertThenRemoveIsLayoutNoOp(SplitTree tree, int rawWidth, int rawHeight, int anchorSeed, bool horizontal, int rawRatio)
    {
        ArgumentNullException.ThrowIfNull(tree);

        IReadOnlyList<WindowId> existing = tree.Windows;
        if (existing.Count == 0)
        {
            return true;
        }

        Rect workArea = ClampWorkArea(rawWidth, rawHeight, minDimension: 600);
        LayoutGaps gaps = new(2, 2);
        WindowId anchor = existing[(int)(Math.Abs((long)anchorSeed) % existing.Count)];
        var newWindow = WindowId.FromOpaqueValue((ulong)existing.Count);
        SplitOrientation orientation = horizontal ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        double ratio = 0.3 + ((Math.Abs((long)rawRatio) % 41) * 0.01);

        IReadOnlyList<WindowPlacement> before = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, gaps);
        SplitTree roundTripped = tree.Insert(anchor, newWindow, orientation, ratio).Remove(newWindow);
        IReadOnlyList<WindowPlacement> after = SplitTreeLayout.Solve(roundTripped, workArea, s_noMinSize, gaps);

        var beforeMap = before.ToDictionary(p => p.WindowId, p => p.Bounds);
        var afterMap = after.ToDictionary(p => p.WindowId, p => p.Bounds);

        return beforeMap.Count == afterMap.Count
            && beforeMap.All(kvp => afterMap.TryGetValue(kvp.Key, out Rect r) && r == kvp.Value);
    }

    private const double Epsilon = 1e-6;

    private static bool AlmostEqual(double a, double b) => Math.Abs(a - b) < Epsilon;

    private static Rect ClampWorkArea(int rawWidth, int rawHeight, int minDimension)
    {
        long width = (Math.Abs((long)rawWidth) % 3000) + minDimension;
        long height = (Math.Abs((long)rawHeight) % 3000) + minDimension;
        return new Rect(0, 0, width, height);
    }

    private static LayoutGaps ClampGaps(int rawOuter, int rawInner) =>
        new(Math.Abs((long)rawOuter) % 4, Math.Abs((long)rawInner) % 4);

    private static LayoutConstraints ClampSmallConstraints(int rawMinWidth, int rawMinHeight) =>
        new((Math.Abs((long)rawMinWidth) % 15) + 1, (Math.Abs((long)rawMinHeight) % 15) + 1);
}
