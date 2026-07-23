using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3) for the real
/// <see cref="WindowProcessIdReader"/>, matching <see cref="WindowProbeTests"/>'s own style: only
/// invariants that hold for <em>any</em> live desktop, never a specific window count or identity,
/// so an empty-desktop CI runner still passes.
/// </summary>
public sealed class WindowProcessIdReaderTests
{
    [Fact]
    public void EveryEnumeratedWindowHasAResolvablePidWhenStillQueryable()
    {
        var reader = new WindowProcessIdReader();
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            uint? pid = reader.TryReadProcessId(window);

            // A window can legitimately be destroyed between enumeration and this call (the same
            // routine race WindowProbeTests.cs's own bounds test documents) — only assert that a
            // resolved pid, when present, is never the sentinel "no process" value.
            if (pid is { } resolvedPid)
            {
                Assert.NotEqual(0u, resolvedPid);
            }
        }
    }

    [Fact]
    public void InvalidWindowHandleReturnsNull()
    {
        var reader = new WindowProcessIdReader();

        uint? pid = reader.TryReadProcessId(default);

        Assert.Null(pid);
    }
}
