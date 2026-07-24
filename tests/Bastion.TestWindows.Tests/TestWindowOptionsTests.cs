using Bastion.TestWindows;
using Xunit;

namespace Bastion.TestWindows.Tests;

/// <summary>
/// Tier 1 unit tests (docs/engineering/testing.md §3) for <see cref="TestWindowOptions.Parse"/> —
/// the only part of this project (GitHub issue #13) testable without a live desktop session.
/// <see cref="TestWindowSpawner.Run"/>/<c>WndProc</c>/<c>WatchStdinForEof</c> need one and are
/// exercised only by the not-yet-built Tier 3 harness (DESIGN.md §11).
/// </summary>
public sealed class TestWindowOptionsTests
{
    [Fact]
    public void NoArgsProducesDefault()
    {
        var options = TestWindowOptions.Parse([]);

        Assert.Equal(TestWindowOptions.Default, options);
    }

    [Fact]
    public void DefaultIsAnUnownedAppWindowWithNoToolWindowOrNoActivateStyle()
    {
        // The manageability-filter baseline (DESIGN.md §3.3): what the pre-issue-#13 spawner always
        // produced. Every new flag below must default to preserving exactly this.
        TestWindowOptions options = TestWindowOptions.Default;

        Assert.Equal(800, options.Width);
        Assert.Equal(600, options.Height);
        Assert.Equal(200, options.MinWidth);
        Assert.Equal(150, options.MinHeight);
        Assert.Equal("Bastion Test Window", options.Title);
        Assert.False(options.ToolWindow);
        Assert.True(options.AppWindow);
        Assert.False(options.NoActivate);
        Assert.Null(options.OwnerHwnd);
    }

    [Fact]
    public void WidthFlagOverridesDefault()
    {
        var options = TestWindowOptions.Parse(["--width", "1024"]);

        Assert.Equal(1024, options.Width);
        Assert.Equal(TestWindowOptions.Default.Height, options.Height);
    }

    [Fact]
    public void HeightFlagOverridesDefault()
    {
        var options = TestWindowOptions.Parse(["--height", "768"]);

        Assert.Equal(768, options.Height);
    }

    [Fact]
    public void MinWidthFlagOverridesDefault()
    {
        var options = TestWindowOptions.Parse(["--min-width", "50"]);

        Assert.Equal(50, options.MinWidth);
    }

    [Fact]
    public void MinHeightFlagOverridesDefault()
    {
        var options = TestWindowOptions.Parse(["--min-height", "60"]);

        Assert.Equal(60, options.MinHeight);
    }

    [Fact]
    public void TitleFlagOverridesDefault()
    {
        var options = TestWindowOptions.Parse(["--title", "Custom Title"]);

        Assert.Equal("Custom Title", options.Title);
    }

    [Fact]
    public void ToolWindowFlagAloneLeavesAppWindowAtItsDefaultTrue()
    {
        // DESIGN.md §3.3's "unless WS_EX_APPWINDOW" carve-out case: TOOLWINDOW + APPWINDOW together
        // must still be manageable, and this is the combination --tool-window alone produces, since
        // AppWindow defaults to true.
        var options = TestWindowOptions.Parse(["--tool-window"]);

        Assert.True(options.ToolWindow);
        Assert.True(options.AppWindow);
    }

    [Fact]
    public void ToolWindowPlusNoAppWindowProducesTheRejectedCombination()
    {
        // DESIGN.md §3.3's filter rejects exactly this: WS_EX_TOOLWINDOW without WS_EX_APPWINDOW.
        var options = TestWindowOptions.Parse(["--tool-window", "--no-app-window"]);

        Assert.True(options.ToolWindow);
        Assert.False(options.AppWindow);
    }

    [Fact]
    public void NoAppWindowFlagAloneUnsetsTheDefaultTrue()
    {
        var options = TestWindowOptions.Parse(["--no-app-window"]);

        Assert.False(options.AppWindow);
        Assert.False(options.ToolWindow);
        Assert.Null(options.OwnerHwnd);
    }

    [Fact]
    public void NoActivateFlagSetsNoActivateStyle()
    {
        // DESIGN.md §3.3's filter rejects WS_EX_NOACTIVATE unconditionally — no AppWindow carve-out
        // unlike ToolWindow/owner, so AppWindow's default true is irrelevant to this case.
        var options = TestWindowOptions.Parse(["--no-activate"]);

        Assert.True(options.NoActivate);
    }

    [Fact]
    public void OwnerFlagAloneLeavesAppWindowAtItsDefaultTrue()
    {
        // DESIGN.md §3.3's "unless WS_EX_APPWINDOW" carve-out case: an owned window with APPWINDOW
        // must still be manageable, and this is the combination --owner alone produces.
        var options = TestWindowOptions.Parse(["--owner", "12345"]);

        Assert.Equal(12345, options.OwnerHwnd);
        Assert.True(options.AppWindow);
    }

    [Fact]
    public void OwnerPlusNoAppWindowProducesTheRejectedCombination()
    {
        // DESIGN.md §3.3's filter rejects exactly this: a non-null owner without WS_EX_APPWINDOW.
        var options = TestWindowOptions.Parse(["--owner", "999", "--no-app-window"]);

        Assert.Equal(999, options.OwnerHwnd);
        Assert.False(options.AppWindow);
    }

    [Fact]
    public void OwnerValueRoundTripsANegativeNintForAHighAddressHandle()
    {
        // A real HWND value, printed via ((nint)hwnd.Value).ToString(CultureInfo.InvariantCulture)
        // (TestWindowSpawner.Run), reads as negative once cast to a signed native int on a
        // high-address handle; --owner must round-trip that losslessly, the same way the printing
        // side never re-encodes it as unsigned.
        var options = TestWindowOptions.Parse(["--owner", "-12345"]);

        Assert.Equal(-12345, options.OwnerHwnd);
    }

    [Fact]
    public void UnknownFlagIsIgnored()
    {
        var options = TestWindowOptions.Parse(["--not-a-real-flag"]);

        Assert.Equal(TestWindowOptions.Default, options);
    }

    [Fact]
    public void TrailingValueFlagMissingItsValueIsIgnoredRatherThanThrowing()
    {
        // Regression guard for Parse's loop-bound fix: a value-taking flag as the very last
        // argument, with nothing after it, must be skipped rather than throwing
        // IndexOutOfRangeException/FormatException on a nonexistent value.
        var options = TestWindowOptions.Parse(["--width"]);

        Assert.Equal(TestWindowOptions.Default.Width, options.Width);
    }

    [Fact]
    public void PresenceOnlyFlagAsTheLastArgumentIsStillParsed()
    {
        // Regression guard for the same loop-bound fix, the opposite direction: the previous
        // `i < args.Length - 1` bound (sized only for this parser's original value-taking flags)
        // would have silently ignored a presence-only flag had it been the very last argument.
        var options = TestWindowOptions.Parse(["--width", "300", "--no-activate"]);

        Assert.Equal(300, options.Width);
        Assert.True(options.NoActivate);
    }

    [Fact]
    public void AllNewFlagsCombineIndependently()
    {
        var options = TestWindowOptions.Parse(
        [
            "--width", "320",
            "--height", "240",
            "--min-width", "64",
            "--min-height", "48",
            "--title", "Combined",
            "--tool-window",
            "--no-app-window",
            "--no-activate",
            "--owner", "42",
        ]);

        Assert.Equal(320, options.Width);
        Assert.Equal(240, options.Height);
        Assert.Equal(64, options.MinWidth);
        Assert.Equal(48, options.MinHeight);
        Assert.Equal("Combined", options.Title);
        Assert.True(options.ToolWindow);
        Assert.False(options.AppWindow);
        Assert.True(options.NoActivate);
        Assert.Equal(42, options.OwnerHwnd);
    }
}
