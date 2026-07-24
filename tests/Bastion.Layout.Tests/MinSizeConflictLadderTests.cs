using Bastion.Core;
using FsCheck.Xunit;
using Xunit;

namespace Bastion.Layout.Tests;

/// <summary>
/// Tier 1 tests (docs/engineering/testing.md §1/§3) for <see cref="MinSizeConflictLadder"/> --
/// DESIGN.md §6's three-step min-size conflict ladder (GitHub issue #6), implemented as a
/// standalone post-processing stage over already-solved placements (see that type's own remarks
/// for the Option A/B design decision this suite exercises the consequences of).
/// </summary>
[Properties(Arbitrary = [typeof(SplitTreeGenerators)])]
public sealed class MinSizeConflictLadderTests
{
    private static readonly LayoutConstraints s_noMinSize = new(0, 0);

    // --- Construction & validation --------------------------------------------------------------

    [Fact]
    public void ResolveRejectsNullPlacements()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MinSizeConflictLadder.Resolve(null!, s_noMinSize, new Dictionary<WindowId, LayoutConstraints>(), new Rect(0, 0, 100, 100)));
    }

    [Fact]
    public void ResolveRejectsNullEffectiveMinSizes()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MinSizeConflictLadder.Resolve([], s_noMinSize, null!, new Rect(0, 0, 100, 100)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void ResolveRejectsAnInvalidMaxTolerableFraction(double invalidFraction)
    {
        var options = new MinSizeConflictLadderOptions { MaxTolerableFraction = invalidFraction };
        List<WindowPlacement> placements = [new(WindowId.FromOpaqueValue(0), new Rect(0, 0, 100, 100))];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MinSizeConflictLadder.Resolve(placements, s_noMinSize, new Dictionary<WindowId, LayoutConstraints>(), new Rect(0, 0, 100, 100), options));
    }

    [Fact]
    public void ResolveOfAnEmptyPlacementListReturnsAnEmptyResult()
    {
        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            [], s_noMinSize, new Dictionary<WindowId, LayoutConstraints>(), new Rect(0, 0, 100, 100));

        Assert.Empty(result.Placements);
        Assert.Empty(result.Overlaps);
        Assert.Empty(result.AutoFloats);
        Assert.False(result.HasAnyConflict);
    }

    // --- No-op when nothing is constrained -------------------------------------------------------

    [Fact]
    public void AlreadySatisfiedPlacementsPassThroughUnchanged()
    {
        var a = WindowId.FromOpaqueValue(0);
        var b = WindowId.FromOpaqueValue(1);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 50, 100)), new(b, new Rect(50, 0, 100, 100))];

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, new Dictionary<WindowId, LayoutConstraints>(), new Rect(0, 0, 100, 100));

        Assert.False(result.HasAnyConflict);
        Assert.Equal(placements, result.Placements);
    }

    [Fact]
    public void DefaultMinimumIsNeverUndercutByASmallerPerWindowCacheEntry()
    {
        var a = WindowId.FromOpaqueValue(0);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 20, 20))];
        var defaultMinimum = new LayoutConstraints(50, 50);
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(10, 10) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, defaultMinimum, effectiveMinSizes, new Rect(0, 0, 1000, 1000));

        // No neighbor exists at all, so step 1 cannot apply -- step 2 (bounded overlap) must still
        // grow it to the 50x50 default floor, not the smaller 10x10 cache entry.
        Rect bounds = result.Placements.Single(p => p.WindowId == a).Bounds;
        Assert.True(bounds.Width >= 50 - 1e-6);
        Assert.True(bounds.Height >= 50 - 1e-6);
    }

    // --- Step 1: redistribute along the (deficient) axis -----------------------------------------

    [Fact]
    public void StepOneRedistributesExactlyTheDeficitFromAFullSpanNeighbor()
    {
        var a = WindowId.FromOpaqueValue(0);
        var b = WindowId.FromOpaqueValue(1);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 40, 100)), new(b, new Rect(40, 0, 100, 100))];
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(70, 0) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Assert.Empty(result.AutoFloats);
        Assert.Empty(result.Overlaps);
        Assert.Contains(result.Redistributions, r => r.WindowId == a); // Codex review finding: successful step 1 is itself a tracked conflict.
        Assert.True(result.HasAnyConflict);
        Rect aBounds = result.Placements.Single(p => p.WindowId == a).Bounds;
        Rect bBounds = result.Placements.Single(p => p.WindowId == b).Bounds;
        Assert.Equal(70.0, aBounds.Width, precision: 6);
        Assert.Equal(30.0, bBounds.Width, precision: 6);
        Assert.Equal(aBounds.Right, bBounds.Left, precision: 6); // Still touching -- the (zero) gap between them is preserved.
    }

    [Fact]
    public void StepOneRedistributesFromABeforeNeighborToo()
    {
        var a = WindowId.FromOpaqueValue(0);
        var b = WindowId.FromOpaqueValue(1);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 60, 100)), new(b, new Rect(60, 0, 100, 100))];
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [b] = new LayoutConstraints(70, 0) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Assert.Empty(result.AutoFloats);
        Assert.Empty(result.Overlaps);
        Assert.Contains(result.Redistributions, r => r.WindowId == b);
        Rect bBounds = result.Placements.Single(p => p.WindowId == b).Bounds;
        Assert.Equal(70.0, bBounds.Width, precision: 6);
    }

    [Fact]
    public void StepOneRespectsTheNeighborsOwnMinimumRatherThanShrinkingItBelowTheirs()
    {
        var a = WindowId.FromOpaqueValue(0);
        var b = WindowId.FromOpaqueValue(1);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 40, 100)), new(b, new Rect(40, 0, 100, 100))];

        // Redistributing A's full deficit (needs 40 more) would shrink B from 60 to 20, below B's
        // own required minimum of 55 -- step 1 must decline and leave B untouched.
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new()
        {
            [a] = new LayoutConstraints(80, 0),
            [b] = new LayoutConstraints(55, 0),
        };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Rect bBounds = result.Placements.Single(p => p.WindowId == b).Bounds;
        Assert.Equal(60.0, bBounds.Width, precision: 6); // Untouched by the declined redistribution.
        Assert.Contains(result.Overlaps, o => o.WindowId == a); // A instead falls through to step 2.
    }

    // --- Step 2: bounded overlap --------------------------------------------------------------

    [Fact]
    public void StepTwoOverlapsWhenNeitherNeighborCanAbsorbTheDeficit()
    {
        var a = WindowId.FromOpaqueValue(0);
        var b = WindowId.FromOpaqueValue(1);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 40, 100)), new(b, new Rect(40, 0, 100, 100))];

        // A needs 90 (exactly at, not over, the default 0.9 tolerable fraction of a 100-wide work
        // area -- step 3 must not trigger); B needs 50, so redistributing A's full deficit would
        // shrink B to 10, below B's own minimum -- step 1 declines.
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new()
        {
            [a] = new LayoutConstraints(90, 0),
            [b] = new LayoutConstraints(50, 0),
        };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Assert.Empty(result.AutoFloats);
        BoundedOverlapPlacement overlap = Assert.Single(result.Overlaps, o => o.WindowId == a);
        Assert.True(overlap.Bounds.Width >= 90 - 1e-6);
        Assert.Equal(overlap.Bounds, result.Placements.Single(p => p.WindowId == a).Bounds);
    }

    [Fact]
    public void StepTwoOverlapsALoneWindowWithNoNeighborAtAll()
    {
        var a = WindowId.FromOpaqueValue(0);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 40, 40))];
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(80, 80) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 1000, 1000));

        BoundedOverlapPlacement overlap = Assert.Single(result.Overlaps);
        Assert.Equal(a, overlap.WindowId);
        Assert.True(overlap.Bounds.Width >= 80 - 1e-6);
        Assert.True(overlap.Bounds.Height >= 80 - 1e-6);
    }

    [Fact]
    public void StepTwoTranslatesRatherThanShrinksToStayWithinTheWorkAreaWhenPossible()
    {
        var a = WindowId.FromOpaqueValue(0);
        // Positioned hard against the left edge -- a naive symmetric inflation around its own
        // center would push part of the grown rect to negative X.
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 20, 20))];
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(60, 20) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 1000, 1000));

        Rect bounds = result.Placements.Single(p => p.WindowId == a).Bounds;
        Assert.Equal(60.0, bounds.Width, precision: 6);
        Assert.True(bounds.Left >= 0 - 1e-6); // Translated to stay on-screen, not left partially off it.
    }

    // --- Step 3: auto-float -----------------------------------------------------------------------

    [Fact]
    public void StepThreeAutoFloatsWhenTheRequiredMinimumExceedsTheTolerableFraction()
    {
        var a = WindowId.FromOpaqueValue(0);
        var b = WindowId.FromOpaqueValue(1);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 40, 100)), new(b, new Rect(40, 0, 100, 100))];

        // 95 > 0.9 * 100 -- exceeds the default tolerable fraction outright.
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(95, 0) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Assert.Empty(result.Overlaps);
        AutoFloatDecision decision = Assert.Single(result.AutoFloats);
        Assert.Equal(a, decision.WindowId);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        Assert.DoesNotContain(result.Placements, p => p.WindowId == a);
        Assert.Contains(result.Placements, p => p.WindowId == b); // The untouched sibling stays tiled.
    }

    /// <summary>
    /// Codex review finding on this PR: a window whose current tile already meets an oversized
    /// per-window requirement must pass through unchanged -- the tolerable-fraction test is about
    /// whether the <em>requirement</em> is reasonable, not a substitute for checking whether a
    /// deficit exists at all. A monocle-style tile filling the whole work area is the canonical
    /// case: its own effective minimum can legitimately exceed 90% of the work area's dimensions
    /// without that ever being a real conflict.
    /// </summary>
    [Fact]
    public void AnAlreadySatisfiedTileIsNeverFloatedEvenWhenItsRequirementExceedsTheTolerableFraction()
    {
        var a = WindowId.FromOpaqueValue(0);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 100, 100))];

        // 95 > 0.9 * 100 on both axes -- would exceed the tolerable fraction -- but the tile is
        // already 100x100, which comfortably satisfies it.
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(95, 95) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Assert.False(result.HasAnyConflict);
        Assert.Equal(placements, result.Placements);
    }

    [Fact]
    public void StepThreeIsReachableViaTheHeightAxisIndependentlyOfWidth()
    {
        var a = WindowId.FromOpaqueValue(0);
        List<WindowPlacement> placements = [new(a, new Rect(0, 0, 40, 40))];

        // Width (10) is well within tolerance; height (95) alone exceeds it.
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [a] = new LayoutConstraints(10, 95) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements, s_noMinSize, effectiveMinSizes, new Rect(0, 0, 100, 100));

        Assert.Single(result.AutoFloats);
    }

    // --- Realistic geometry from the real solver (SplitTreeLayout.Solve), not hand-built rects ---

    [Fact]
    public void LadderCorrectlyRedistributesOverRealSplitTreeLayoutOutput()
    {
        var first = WindowId.FromOpaqueValue(0);
        var second = WindowId.FromOpaqueValue(1);
        SplitTree tree = SplitTree.Empty.InsertFirst(first).Insert(first, second, SplitOrientation.Horizontal, ratio: 0.5);

        Rect workArea = new(0, 0, 200, 100);
        var gaps = new LayoutGaps(0, 0);
        IReadOnlyList<WindowPlacement> solved = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, gaps);

        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new() { [first] = new LayoutConstraints(150, 0) };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(solved, s_noMinSize, effectiveMinSizes, workArea);

        Assert.Empty(result.AutoFloats);
        Assert.Empty(result.Overlaps);
        Assert.Contains(result.Redistributions, r => r.WindowId == first);
        Rect firstBounds = result.Placements.Single(p => p.WindowId == first).Bounds;
        Rect secondBounds = result.Placements.Single(p => p.WindowId == second).Bounds;
        Assert.Equal(150.0, firstBounds.Width, precision: 6);
        Assert.Equal(50.0, secondBounds.Width, precision: 6);
    }

    // --- General invariants over realistic (SplitTreeLayout-solved) input, property-tested --------

    /// <summary>
    /// Every input window ends up in exactly one of <see cref="MinSizeConflictResult.Placements"/>/
    /// <see cref="MinSizeConflictResult.AutoFloats"/>, regardless of which (if any) window gets an
    /// extreme per-window override.
    /// </summary>
    [Property]
    public bool EveryInputWindowEndsUpInExactlyOnePlacementsOrAutoFloats(
        SplitTree tree, int rawWidth, int rawHeight, int overrideSeed, int rawOverrideMinWidth)
    {
        Rect workArea = ClampWorkArea(rawWidth, rawHeight);
        IReadOnlyList<WindowPlacement> solved = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, new LayoutGaps(2, 2));
        if (solved.Count == 0)
        {
            return true;
        }

        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = BuildSingleOverride(solved, overrideSeed, rawOverrideMinWidth);

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(solved, s_noMinSize, effectiveMinSizes, workArea);

        var placedIds = result.Placements.Select(p => p.WindowId).ToHashSet();
        var floatedIds = result.AutoFloats.Select(a => a.WindowId).ToHashSet();
        var inputIds = solved.Select(p => p.WindowId).ToHashSet();

        return placedIds.Count + floatedIds.Count == inputIds.Count
            && !placedIds.Overlaps(floatedIds)
            && placedIds.SetEquals(inputIds.Except(floatedIds))
            && result.Overlaps.All(o => placedIds.Contains(o.WindowId))
            && result.Redistributions.All(r => placedIds.Contains(r.WindowId));
    }

    [Property]
    public bool ResolveIsDeterministic(SplitTree tree, int rawWidth, int rawHeight, int overrideSeed, int rawOverrideMinWidth)
    {
        Rect workArea = ClampWorkArea(rawWidth, rawHeight);
        IReadOnlyList<WindowPlacement> solved = SplitTreeLayout.Solve(tree, workArea, s_noMinSize, new LayoutGaps(2, 2));
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = BuildSingleOverride(solved, overrideSeed, rawOverrideMinWidth);

        MinSizeConflictResult first = MinSizeConflictLadder.Resolve(solved, s_noMinSize, effectiveMinSizes, workArea);
        MinSizeConflictResult second = MinSizeConflictLadder.Resolve(solved, s_noMinSize, effectiveMinSizes, workArea);

        return first.Placements.SequenceEqual(second.Placements)
            && first.Redistributions.SequenceEqual(second.Redistributions)
            && first.Overlaps.SequenceEqual(second.Overlaps)
            && first.AutoFloats.SequenceEqual(second.AutoFloats);
    }

    private static Dictionary<WindowId, LayoutConstraints> BuildSingleOverride(
        IReadOnlyList<WindowPlacement> solved, int overrideSeed, int rawOverrideMinWidth)
    {
        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = [];
        if (solved.Count > 0)
        {
            WindowId overridden = solved[(int)(Math.Abs((long)overrideSeed) % solved.Count)].WindowId;

            // A wide range including values that fit via redistribution, values large enough to
            // force overlap, and values large enough to force auto-float.
            double overrideMinWidth = Math.Abs((long)rawOverrideMinWidth) % 5000;
            effectiveMinSizes[overridden] = new LayoutConstraints(overrideMinWidth, 0);
        }

        return effectiveMinSizes;
    }

    private static Rect ClampWorkArea(int rawWidth, int rawHeight)
    {
        long width = (Math.Abs((long)rawWidth) % 3000) + 600;
        long height = (Math.Abs((long)rawHeight) % 3000) + 600;
        return new Rect(0, 0, width, height);
    }
}
