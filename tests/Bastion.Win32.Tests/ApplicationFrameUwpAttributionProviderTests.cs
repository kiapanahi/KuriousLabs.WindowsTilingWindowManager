using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3) for the real
/// <see cref="ApplicationFrameUwpAttributionProvider"/>'s class-name fast path. The
/// <c>EnumChildWindows</c>/<c>CoreWindow</c>-walk path requires an actual running UWP app's
/// ApplicationFrameWindow, which is not guaranteed present on any given desktop or CI runner — per
/// this issue's scope, that path is exercised only indirectly (DESIGN.md §13's risk-register issue
/// tracks its own Tier-5 canary separately, per this issue's task brief). This file only asserts
/// the documented, always-true invariant: an ordinary window (not classed
/// <c>ApplicationFrameWindow</c>) is never attributed.
/// </summary>
public sealed class ApplicationFrameUwpAttributionProviderTests
{
    [Fact]
    public void OrdinaryEnumeratedWindowsAreNeverAttributed()
    {
        var provider = new ApplicationFrameUwpAttributionProvider(new ProcessAumidReader());
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            if (!string.Equals(WindowProbe.GetClassName(window), "ApplicationFrameWindow", StringComparison.Ordinal))
            {
                Assert.Null(provider.TryGetAumid(window));
            }
        }
    }

    [Fact]
    public void InvalidWindowHandleReturnsNull()
    {
        var provider = new ApplicationFrameUwpAttributionProvider(new ProcessAumidReader());

        Assert.Null(provider.TryGetAumid(default));
    }
}
