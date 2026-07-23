using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3) for the real
/// <see cref="PropertyStoreAumidReader"/> running on a real <see cref="ShellComThread"/> — the full
/// COM path (<c>SHGetPropertyStoreForWindow</c> + <c>IPropertyStore::GetValue</c> +
/// <c>PropVariantClear</c>) end to end. Ordinary desktop windows rarely carry an explicit
/// <c>PKEY_AppUserModel_ID</c>, so this mainly exercises the documented "not present -> VT_EMPTY"
/// path — the invariant asserted is "completes without throwing or hanging," matching
/// <see cref="WindowProbeTests"/>'s tolerance of an empty-desktop CI runner, never a specific
/// window's AUMID value.
/// </summary>
public sealed class PropertyStoreAumidReaderTests
{
    [Fact]
    public async Task ReadingEveryEnumeratedWindowCompletesWithoutThrowing()
    {
        using var shellComThread = new ShellComThread();
        var reader = new PropertyStoreAumidReader(shellComThread);
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // No assertion on the returned value itself (null or a real AUMID are both valid,
            // window-dependent outcomes) — the invariant under test is that the real COM path
            // completes cleanly for whatever a live desktop happens to have open.
            _ = await reader.TryGetAumidAsync(window, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task InvalidWindowHandleReturnsNullRatherThanThrowing()
    {
        using var shellComThread = new ShellComThread();
        var reader = new PropertyStoreAumidReader(shellComThread);

        string? aumid = await reader.TryGetAumidAsync(default, TestContext.Current.CancellationToken);

        Assert.Null(aumid);
    }
}
