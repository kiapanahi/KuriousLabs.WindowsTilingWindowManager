using Bastion.Core;
using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop test (docs/engineering/testing.md §3) wiring every <em>real</em>
/// implementation this issue adds — <see cref="WindowProcessIdReader"/>,
/// <see cref="WindowManageabilityInfoReader"/>, <see cref="WindowIdentityResolver"/> (with its four
/// real rungs), and <see cref="ShellComThread"/> — into one real <see cref="WindowRegistry"/> and
/// running it against a live desktop. Every other test in this project exercises these types in
/// isolation against fakes; this file is the one place that proves the assembled whole compiles,
/// constructs, and runs end to end without throwing, matching <see cref="WindowProbeTests"/>'s
/// tolerance of an empty-desktop CI runner — no assertion here depends on any specific window
/// actually being admitted.
/// </summary>
public sealed class WindowRegistryRealDesktopTests
{
    [Fact]
    public async Task AdmittingEveryEnumeratedWindowCompletesWithoutThrowing()
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

        foreach (HWND window in WindowProbe.EnumerateVisibleTopLevelWindows())
        {
            // No assertion on the outcome itself — real desktops have a wide, environment-specific
            // mix of manageable and unmanageable windows. The invariant under test is that the
            // fully-wired real pipeline never throws for whatever a live desktop has open.
            WindowId? admitted = await registry.TryAdmitAsync(window, TestContext.Current.CancellationToken);
            if (admitted is { } windowId)
            {
                Assert.Equal(windowId, registry.TryGetEntry(window)?.WindowId);
            }
        }
    }
}
