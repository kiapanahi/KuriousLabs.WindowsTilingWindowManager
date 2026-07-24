using Bastion.Core;
using Bastion.Layout;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>
/// Tier 1 tests (docs/engineering/testing.md §1) for <see cref="EffectiveMinSizeCache"/> -- the
/// DESIGN.md §6 learned effective-min-size cache (GitHub issue #6) -- against a
/// <see cref="FakeTimeProvider"/>, with zero interop types anywhere in this file. Runs on Linux CI
/// exactly like <see cref="ReconcilerTests"/> already does (pure-core skill; no
/// <c>Bastion.Win32</c> reference).
/// </summary>
public sealed class EffectiveMinSizeCacheTests
{
    private static readonly LayoutConstraints s_systemFloor = new(100, 50);
    private static readonly RuleKey s_ruleKey = new("exe:C:\\Program Files\\Example\\example.exe");

    // --- Construction & validation ------------------------------------------------------------

    [Fact]
    public void ConstructorRejectsANullTimeProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new EffectiveMinSizeCache(s_systemFloor, null!));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ConstructorRejectsAnInvalidSystemFloorWidth(double invalidWidth)
    {
        var time = new FakeTimeProvider();
        var floor = new LayoutConstraints(invalidWidth, 50);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveMinSizeCache(floor, time));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ConstructorRejectsAnInvalidSystemFloorHeight(double invalidHeight)
    {
        var time = new FakeTimeProvider();
        var floor = new LayoutConstraints(100, invalidHeight);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveMinSizeCache(floor, time));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ConstructorRejectsANonPositiveDecayInterval(double seconds)
    {
        var time = new FakeTimeProvider();
        var options = new EffectiveMinSizeCacheOptions { DecayInterval = TimeSpan.FromSeconds(seconds) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveMinSizeCache(s_systemFloor, time, options));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ConstructorRejectsADecayFactorOutsideTheOpenClosedUnitInterval(double invalidFactor)
    {
        var time = new FakeTimeProvider();
        var options = new EffectiveMinSizeCacheOptions { DecayFactor = invalidFactor };
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectiveMinSizeCache(s_systemFloor, time, options));
    }

    [Fact]
    public void ConstructorAcceptsADecayFactorOfExactlyOneAsANoDecayOptOut()
    {
        var time = new FakeTimeProvider();
        var options = new EffectiveMinSizeCacheOptions { DecayFactor = 1.0 };
        var cache = new EffectiveMinSizeCache(s_systemFloor, time, options);

        Assert.Equal(s_systemFloor, cache.SystemFloor);
    }

    // --- Seeding (acceptance criteria: "seeding from GetSystemMetrics floors") ----------------

    [Fact]
    public void SystemFloorExposesExactlyWhatTheCacheWasConstructedWith()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        Assert.Equal(s_systemFloor, cache.SystemFloor);
    }

    [Fact]
    public void ARuleKeyNeverBeforeSeenReturnsExactlyTheSystemFloor()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        Assert.Equal(s_systemFloor, cache.GetEffectiveMinSize(s_ruleKey));
        Assert.Equal(s_systemFloor, cache.GetEffectiveMinSize(new RuleKey("some-other-key")));
    }

    // --- Cache update on a clamp (acceptance criteria) -----------------------------------------

    [Fact]
    public void RecordClampGrowsTheLearnedMinimumAboveTheFloor()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: 800, clampedHeight: 600);

        Assert.Equal(new LayoutConstraints(800, 600), result);
        Assert.Equal(new LayoutConstraints(800, 600), cache.GetEffectiveMinSize(s_ruleKey));
    }

    [Fact]
    public void RecordClampNeverGrowsAnAxisBelowTheSystemFloorEvenWhenObservedIsSmaller()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: 10, clampedHeight: 10);

        Assert.Equal(s_systemFloor, result);
    }

    [Fact]
    public void RecordClampIsAHighWaterMarkNotARawOverwrite()
    {
        // A single anomalously-small clamp reading must never instantly undo previously-learned,
        // larger evidence -- see EffectiveMinSizeCache's own remarks. Zero elapsed time between
        // calls isolates this from decay (covered separately below).
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(s_systemFloor, time);

        cache.RecordClamp(s_ruleKey, clampedWidth: 800, clampedHeight: 600);
        LayoutConstraints afterSmallerClamp = cache.RecordClamp(s_ruleKey, clampedWidth: 200, clampedHeight: 150);

        Assert.Equal(new LayoutConstraints(800, 600), afterSmallerClamp);
        Assert.Equal(new LayoutConstraints(800, 600), cache.GetEffectiveMinSize(s_ruleKey));
    }

    [Fact]
    public void RecordClampTracksEachAxisIndependently()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        cache.RecordClamp(s_ruleKey, clampedWidth: 800, clampedHeight: 60);
        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: 120, clampedHeight: 600);

        // Width's high-water mark (800) survives even though the second clamp's own width (120) was
        // smaller; height's high-water mark (600) is the new, larger value.
        Assert.Equal(new LayoutConstraints(800, 600), result);
    }

    [Fact]
    public void DistinctRuleKeysAreTrackedIndependently()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());
        var otherKey = new RuleKey("aumid:Contoso.Example_1.0.0.0_x64__abcdef");

        cache.RecordClamp(s_ruleKey, clampedWidth: 900, clampedHeight: 700);

        Assert.Equal(new LayoutConstraints(900, 700), cache.GetEffectiveMinSize(s_ruleKey));
        Assert.Equal(s_systemFloor, cache.GetEffectiveMinSize(otherKey));
    }

    // --- Per-axis nullability (Codex review finding on this PR: a whole-rect ClampedTo readback
    //     must never pollute an axis that was not itself clamped) --------------------------------

    [Fact]
    public void AnUnclampedAxisPassedAsNullIsNeverAffectedByARecordClampCall()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        // Only width clamped this observation (e.g. a 400x800 request clamped to 500x800 -- the
        // height of 800 was simply what was requested, not evidence of a real height floor).
        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: 500, clampedHeight: null);

        Assert.Equal(500.0, result.MinWidth, precision: 6);
        Assert.Equal(s_systemFloor.MinHeight, result.MinHeight, precision: 6); // Untouched -- still exactly the floor.
    }

    [Fact]
    public void APreviouslyLearnedAxisSurvivesALaterCallThatOmitsIt()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());
        cache.RecordClamp(s_ruleKey, clampedWidth: 500, clampedHeight: 700);

        // A later observation only reconfirms height -- width's own learned value (and decay clock)
        // must be left exactly as it was, not reset or discarded.
        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: null, clampedHeight: 750);

        Assert.Equal(500.0, result.MinWidth, precision: 6);
        Assert.Equal(750.0, result.MinHeight, precision: 6);
    }

    [Fact]
    public void RecordClampWithBothAxesNullIsAHarmlessNoOp()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());
        cache.RecordClamp(s_ruleKey, clampedWidth: 900, clampedHeight: 700);

        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: null, clampedHeight: null);

        Assert.Equal(new LayoutConstraints(900, 700), result);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RecordClampRejectsAnInvalidClampedWidth(double invalidWidth)
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.RecordClamp(s_ruleKey, clampedWidth: invalidWidth, clampedHeight: null));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RecordClampRejectsAnInvalidClampedHeight(double invalidHeight)
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.RecordClamp(s_ruleKey, clampedWidth: null, clampedHeight: invalidHeight));
    }

    // --- Decay (acceptance criteria: decay mechanism; schedule recorded in EffectiveMinSizeCacheOptions) --

    [Fact]
    public void DecayHalvesTheExcessAfterExactlyOneDecayIntervalUnderTheDefaultSchedule()
    {
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(s_systemFloor, time); // Default: 24h interval, 0.5 factor.
        cache.RecordClamp(s_ruleKey, clampedWidth: 300, clampedHeight: 50); // Excess of 200 on width.

        time.Advance(EffectiveMinSizeCacheOptions.Default.DecayInterval);

        LayoutConstraints decayed = cache.GetEffectiveMinSize(s_ruleKey);
        Assert.Equal(200.0, decayed.MinWidth, precision: 6); // 100 (floor) + 200 * 0.5
    }

    [Fact]
    public void DecayNeverCrossesBelowTheSystemFloorEvenAfterManyIntervals()
    {
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(s_systemFloor, time);
        cache.RecordClamp(s_ruleKey, clampedWidth: 10_000, clampedHeight: 10_000);

        time.Advance(TimeSpan.FromDays(365 * 10)); // Absurdly long -- must still floor, never go below.

        LayoutConstraints decayed = cache.GetEffectiveMinSize(s_ruleKey);
        Assert.True(decayed.MinWidth >= s_systemFloor.MinWidth);
        Assert.True(decayed.MinHeight >= s_systemFloor.MinHeight);
        Assert.Equal(s_systemFloor.MinWidth, decayed.MinWidth, precision: 3);
        Assert.Equal(s_systemFloor.MinHeight, decayed.MinHeight, precision: 3);
    }

    [Fact]
    public void ADecayFactorOfOneDisablesDecayEntirely()
    {
        var time = new FakeTimeProvider();
        var options = new EffectiveMinSizeCacheOptions { DecayFactor = 1.0, DecayInterval = TimeSpan.FromMinutes(1) };
        var cache = new EffectiveMinSizeCache(s_systemFloor, time, options);
        cache.RecordClamp(s_ruleKey, clampedWidth: 800, clampedHeight: 600);

        time.Advance(TimeSpan.FromDays(3650));

        Assert.Equal(new LayoutConstraints(800, 600), cache.GetEffectiveMinSize(s_ruleKey));
    }

    [Fact]
    public void GetEffectiveMinSizeIsAPureReadThatReflectsAdvancingTimeOnEachCall()
    {
        // Two reads separated by an intervening time advance, with no RecordClamp in between, must
        // observe progressively more decay -- GetEffectiveMinSize must never cache/freeze a
        // snapshot of the decayed value at the first read.
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(s_systemFloor, time);
        cache.RecordClamp(s_ruleKey, clampedWidth: 500, clampedHeight: 50);

        LayoutConstraints firstRead = cache.GetEffectiveMinSize(s_ruleKey);
        time.Advance(EffectiveMinSizeCacheOptions.Default.DecayInterval);
        LayoutConstraints secondRead = cache.GetEffectiveMinSize(s_ruleKey);

        Assert.True(secondRead.MinWidth < firstRead.MinWidth);
    }

    [Fact]
    public void DecayClocksAreIndependentPerAxis()
    {
        // Width is reconfirmed partway through the interval that would otherwise decay it; height
        // is left alone and must decay on its own, unaffected schedule.
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(s_systemFloor, time);
        cache.RecordClamp(s_ruleKey, clampedWidth: 300, clampedHeight: 300); // Excess 200 on both axes.

        time.Advance(TimeSpan.FromHours(12));
        cache.RecordClamp(s_ruleKey, clampedWidth: 300, clampedHeight: null); // Only width reconfirmed.

        time.Advance(TimeSpan.FromHours(12)); // Width's clock: 12h since reconfirmation. Height's clock: 24h since its only observation.

        LayoutConstraints result = cache.GetEffectiveMinSize(s_ruleKey);
        Assert.True(result.MinWidth > result.MinHeight, "Width (reconfirmed at the halfway point) should have decayed less than height (never reconfirmed).");
    }

    [Fact]
    public void RecordClampAfterDecayFoldsInTheAlreadyDecayedValueBeforeApplyingTheNewObservation()
    {
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(s_systemFloor, time);
        cache.RecordClamp(s_ruleKey, clampedWidth: 300, clampedHeight: 50); // Excess 200.

        time.Advance(EffectiveMinSizeCacheOptions.Default.DecayInterval); // Excess decays to 100 -> width 200.

        // A fresh clamp smaller than the pre-decay peak (300) but larger than the now-decayed value
        // (200) must still raise the learned minimum to the fresh observation.
        LayoutConstraints result = cache.RecordClamp(s_ruleKey, clampedWidth: 250, clampedHeight: null);
        Assert.Equal(250.0, result.MinWidth, precision: 6);
    }

    // --- Purge ----------------------------------------------------------------------------------

    [Fact]
    public void PurgeRevertsARuleKeyBackToTheSystemFloor()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());
        cache.RecordClamp(s_ruleKey, clampedWidth: 800, clampedHeight: 600);

        cache.Purge(s_ruleKey);

        Assert.Equal(s_systemFloor, cache.GetEffectiveMinSize(s_ruleKey));
    }

    [Fact]
    public void PurgeOfAnUnknownRuleKeyIsANoOp()
    {
        var cache = new EffectiveMinSizeCache(s_systemFloor, new FakeTimeProvider());

        cache.Purge(new RuleKey("never-seen"));

        Assert.Equal(s_systemFloor, cache.GetEffectiveMinSize(new RuleKey("never-seen")));
    }

    // --- Composition with MinSizeConflictLadder (demonstrates the two standalone components fit
    //     together with zero glue code -- see MinSizeConflictLadder's own remarks on this issue's
    //     Option B design decision) ---------------------------------------------------------------

    [Fact]
    public void CacheLearnedMinimumFeedsTheLaddersRedistributionStepDirectly()
    {
        var time = new FakeTimeProvider();
        var cache = new EffectiveMinSizeCache(new LayoutConstraints(20, 20), time);

        var constrained = WindowId.FromOpaqueValue(1);
        var sibling = WindowId.FromOpaqueValue(2);

        // The cache has learned (from a prior clamp) that `constrained` needs 700px of width --
        // far more than the flat default the layout was originally solved with.
        cache.RecordClamp(new RuleKey("exe:constrained.exe"), clampedWidth: 700, clampedHeight: null);

        var placements = new List<WindowPlacement>
        {
            new(constrained, new Rect(0, 0, 400, 1000)), // Solved too narrow relative to the cache's learned minimum.
            new(sibling, new Rect(400, 0, 1000, 1000)),
        };

        Dictionary<WindowId, LayoutConstraints> effectiveMinSizes = new()
        {
            [constrained] = cache.GetEffectiveMinSize(new RuleKey("exe:constrained.exe")),
        };

        MinSizeConflictResult result = MinSizeConflictLadder.Resolve(
            placements,
            new LayoutConstraints(20, 20),
            effectiveMinSizes,
            new Rect(0, 0, 1000, 1000));

        Assert.Empty(result.AutoFloats);
        Assert.Empty(result.Overlaps); // Redistribution alone should satisfy it -- the sibling has ample room to give.
        Assert.Contains(result.Redistributions, r => r.WindowId == constrained);
        Rect constrainedBounds = result.Placements.Single(p => p.WindowId == constrained).Bounds;
        Assert.Equal(700.0, constrainedBounds.Width, precision: 6);
    }
}
