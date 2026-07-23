using Bastion.Core;
using FsCheck.Xunit;
using Xunit;

namespace Bastion.Layout.Tests;

/// <summary>
/// Tier 1 property tests (docs/engineering/testing.md §3) for <see cref="DwindleLayoutEngine"/>,
/// the only <see cref="ILayoutEngine"/> implementation that exists today.
/// </summary>
/// <remarks>
/// Generates directly over primitive inputs (window count, work-area size, gap sizes) rather than
/// a full custom FsCheck <c>Arbitrary&lt;Rect&gt;</c>/<c>Arbitrary&lt;LayoutGaps&gt;</c> — a
/// deliberately minimal first property suite. The clamping bounds below (window count 1..6,
/// width/height &gt;= 600, gaps 0..3) were hand-verified against
/// <see cref="DwindleLayoutEngine"/>'s actual split arithmetic: the worst case achievable with 6
/// windows (5 alternating splits, 3 of which land on the same axis) leaves a strictly positive
/// remaining span at these bounds, so <see cref="PlacementsNeverOverlap"/> never fails on a
/// degenerate/negative-width rectangle that would otherwise mask a real regression as a flaky
/// generator. DESIGN.md §3.5/§6's eventual <c>SplitTree</c>-backed engine and its subtree-locality
/// property (testing.md §3) are TODOs, not yet implementable against this flat engine — nor is a
/// min-size-respected property, since <see cref="LayoutConstraints"/> is accepted but not yet
/// enforced by <see cref="DwindleLayoutEngine"/> (see its own TODO remark).
/// </remarks>
public sealed class DwindleLayoutEngineTests
{
    private static readonly DwindleLayoutEngine s_engine = new();

    [Property]
    public bool PlacementsNeverOverlap(int rawWindowCount, int rawWidth, int rawHeight, int rawOuterGap, int rawInnerGap)
    {
        int windowCount = ClampWindowCount(rawWindowCount);
        Rect workArea = ClampWorkArea(rawWidth, rawHeight);
        LayoutGaps gaps = ClampGaps(rawOuterGap, rawInnerGap);

        IReadOnlyList<WindowPlacement> placements = Solve(windowCount, workArea, gaps);

        for (var i = 0; i < placements.Count; i++)
        {
            for (var j = i + 1; j < placements.Count; j++)
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
    public bool SolvingTwiceWithIdenticalInputsIsDeterministic(int rawWindowCount, int rawWidth, int rawHeight, int rawOuterGap, int rawInnerGap)
    {
        int windowCount = ClampWindowCount(rawWindowCount);
        Rect workArea = ClampWorkArea(rawWidth, rawHeight);
        LayoutGaps gaps = ClampGaps(rawOuterGap, rawInnerGap);

        IReadOnlyList<WindowPlacement> first = Solve(windowCount, workArea, gaps);
        IReadOnlyList<WindowPlacement> second = Solve(windowCount, workArea, gaps);

        return first.SequenceEqual(second);
    }

    [Fact]
    public void ThreeWindowsProduceOnePlacementEach()
    {
        IReadOnlyList<WindowPlacement> placements = Solve(windowCount: 3, ClampWorkArea(1920, 1080), ClampGaps(0, 0));

        Assert.Equal(3, placements.Count);
        Assert.Equal(3, placements.Select(p => p.WindowId).Distinct().Count());
    }

    [Fact]
    public void SolveDoesNotThrowWhenWindowCountExceedsSplitTreeDepthCap()
    {
        var windows = Enumerable.Range(0, SplitTree.MaxDepth + 2)
            .Select(i => WindowId.FromOpaqueValue((ulong)i))
            .ToList();

        Rect workArea = new(0, 0, 1920, 1080);
        LayoutGaps gaps = new(Outer: 12, Inner: 6);

        IReadOnlyList<WindowPlacement> placements = s_engine.Solve(windows, workArea, new LayoutConstraints(0, 0), gaps);

        Assert.Equal(windows.Count, placements.Count);
        Assert.Equal(windows, placements.Select(p => p.WindowId).ToList());
    }

    private static IReadOnlyList<WindowPlacement> Solve(int windowCount, Rect workArea, LayoutGaps gaps)
    {
        var windows = Enumerable.Range(0, windowCount)
            .Select(i => WindowId.FromOpaqueValue((ulong)i))
            .ToList();

        return s_engine.Solve(windows, workArea, new LayoutConstraints(0, 0), gaps);
    }

    // Widened to long before Math.Abs: FsCheck can and does generate int.MinValue, and
    // Math.Abs(int.MinValue) throws OverflowException (there is no positive int counterpart) —
    // Math.Abs(long) has no such problem since long comfortably represents -int.MinValue.
    private static int ClampWindowCount(int raw) => (int)(Math.Abs((long)raw) % 6) + 1;

    private static Rect ClampWorkArea(int rawWidth, int rawHeight)
    {
        var width = (Math.Abs((long)rawWidth) % 3000) + 600;
        var height = (Math.Abs((long)rawHeight) % 3000) + 600;
        return new Rect(0, 0, width, height);
    }

    private static LayoutGaps ClampGaps(int rawOuter, int rawInner) =>
        new(Math.Abs((long)rawOuter) % 4, Math.Abs((long)rawInner) % 4);
}
