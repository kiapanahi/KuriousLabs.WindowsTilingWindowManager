using Bastion.Core;
using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3, mirroring
/// <c>WindowProbeTests</c>' own precedent) for <see cref="SystemMinTrackSizeReader"/> -- the
/// GitHub issue #6 <c>GetSystemMetricsForDpi(SM_CXMINTRACK/SM_CYMINTRACK)</c> seed adapter.
/// </summary>
/// <remarks>
/// These exercise the real Win32 desktop of whatever machine/CI runner executes them -- there is no
/// faking of <c>GetSystemMetricsForDpi</c>/<c>GetDpiForSystem</c> here. Unlike
/// <c>WindowProbeTests</c>, this asserts a specific (if unbounded-in-value) invariant rather than
/// only "well-formed": a real Windows desktop's minimum window tracking size is documented,
/// long-standing OS behavior that is never legitimately zero, so a positive reading is a safe
/// assertion on any real runner -- hosted or self-hosted, empty desktop or not.
/// </remarks>
public sealed class SystemMinTrackSizeReaderTests
{
    [Fact]
    public void ReadSeedFloorReturnsAPositiveWidthAndHeightOnARealDesktop()
    {
        LayoutConstraints seed = SystemMinTrackSizeReader.ReadSeedFloor();

        Assert.True(seed.MinWidth > 0, $"Expected a positive SM_CXMINTRACK reading, got {seed.MinWidth}.");
        Assert.True(seed.MinHeight > 0, $"Expected a positive SM_CYMINTRACK reading, got {seed.MinHeight}.");
    }

    [Fact]
    public void ReadSeedFloorIsDeterministicAcrossRepeatedCallsWithinTheSameProcess()
    {
        // GetDpiForSystem/GetSystemMetricsForDpi read live system state on every call (this type's
        // own remarks: "You should not cache the system DPI") -- but absent an intervening DPI/
        // display-settings change mid-test-run, two back-to-back reads must agree.
        LayoutConstraints first = SystemMinTrackSizeReader.ReadSeedFloor();
        LayoutConstraints second = SystemMinTrackSizeReader.ReadSeedFloor();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Feeds a real seed reading straight into <see cref="EffectiveMinSizeCache"/>'s constructor --
    /// demonstrating the "thin adapter reads the system floor once and hands it to the Core-side
    /// cache" hand-off this issue's design guidance describes actually type-checks and behaves.
    /// </summary>
    [Fact]
    public void RealSeedFloorConstructsAValidEffectiveMinSizeCache()
    {
        LayoutConstraints seed = SystemMinTrackSizeReader.ReadSeedFloor();
        var cache = new EffectiveMinSizeCache(seed, TimeProvider.System);

        Assert.Equal(seed, cache.SystemFloor);
        Assert.Equal(seed, cache.GetEffectiveMinSize(new RuleKey("never-seen")));
    }
}
