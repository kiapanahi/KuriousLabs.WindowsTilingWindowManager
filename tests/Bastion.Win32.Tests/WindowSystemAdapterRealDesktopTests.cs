using Bastion.Core;
using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop test (docs/engineering/testing.md §3) wiring the real
/// <see cref="WindowSystemAdapter"/> — <see cref="WindowRegistry"/> (with every one of its own
/// real readers, matching <see cref="WindowRegistryRealDesktopTests"/>) plus
/// <see cref="DwmCloakStateReader"/> — and running it against a live desktop. Same style as
/// <see cref="WindowProbeTests"/>/<see cref="DwmCloakStateReaderTests"/>/
/// <see cref="WindowRegistryRealDesktopTests"/>: no assumption about specific windows or counts, so
/// an empty-desktop CI runner still passes; the invariant under test is that the fully-wired real
/// pipeline (<c>EnumWindows</c> + admission + <c>GetWindowRect</c> + <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>
/// + <c>DWMWA_CLOAKED</c> + <c>IsIconic</c>/<c>IsZoomed</c>) never throws for whatever a live desktop
/// has open, and returns well-formed data for whatever it does admit.
/// </summary>
public sealed class WindowSystemAdapterRealDesktopTests
{
    [Fact]
    public async Task ReadAllAsyncCompletesWithoutThrowingAndReturnsNeverDefault()
    {
        using var shellComThread = new ShellComThread();
        var registry = new WindowRegistry(
            new WindowProcessIdReader(),
            new WindowManageabilityInfoReader(),
            new WindowIdentityResolver(
                new ApplicationFrameUwpAttributionProvider(new ProcessAumidReader()),
                new PropertyStoreAumidReader(shellComThread),
                new ProcessAumidReader(),
                new ProcessImagePathReader()),
            WindowClassBlocklist.Default,
            new WindowIdMinter(),
            TimeProvider.System);
        var adapter = new WindowSystemAdapter(registry, new DwmCloakStateReader());

        System.Collections.Immutable.ImmutableArray<ObservedWindow> observed =
            await adapter.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.False(observed.IsDefault);

        foreach (ObservedWindow window in observed)
        {
            // No assertion on specific values (cloak/iconic/zoomed state is environment-specific)
            // -- only that geometry reads are internally consistent when non-degenerate, matching
            // WindowProbeTests' own tolerance for a live, unpredictable desktop.
            Assert.True(window.FrameBounds.Right >= window.FrameBounds.Left);
            Assert.True(window.FrameBounds.Bottom >= window.FrameBounds.Top);
            Assert.True(window.WindowRect.Right >= window.WindowRect.Left);
            Assert.True(window.WindowRect.Bottom >= window.WindowRect.Top);
        }
    }
}
