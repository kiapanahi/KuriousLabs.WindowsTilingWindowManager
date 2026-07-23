using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3) for the real
/// <see cref="WindowManageabilityInfoReader"/>, matching <see cref="WindowProbeTests"/>'s style:
/// only invariants that hold for <em>any</em> live desktop, never a specific window's identity, so
/// an empty-desktop CI runner still passes.
/// </summary>
public sealed class WindowManageabilityInfoReaderTests
{
    [Fact]
    public void EveryEnumeratedTopLevelWindowReadsAsARootWindow()
    {
        var reader = new WindowManageabilityInfoReader();
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // EnumWindows only yields already-top-level windows, so every one of them must read
            // back as its own GA_ROOT — the same invariant WindowProbeTests.cs's own
            // RootAncestorOfATopLevelWindowIsItself asserts directly against GetRootAncestor.
            WindowManageabilityInfo info = reader.Read(window);

            Assert.True(info.IsRootWindow);
        }
    }

    [Fact]
    public void ReadNeverThrowsForAnInvalidWindowHandle()
    {
        var reader = new WindowManageabilityInfoReader();

        // Every underlying Win32/DWM read documents graceful failure (zero/FALSE/a failing
        // HRESULT) for an invalid handle, never an exception — this asserts the reader surfaces
        // that as a well-formed WindowManageabilityInfo, not a throw. IsWindowVisible(NULL) is
        // unambiguously documented to return FALSE, so that field is the one asserted here — a
        // default HWND's GetAncestor(GA_ROOT) also fails and returns NULL, which trivially equals
        // the default input itself, making IsRootWindow *true* for this degenerate input; that is
        // harmless in the real pipeline (WindowRegistry never reaches this reader for a null
        // handle — IWindowProcessIdReader.TryReadProcessId(default) already returns null first)
        // but makes IsRootWindow the wrong field to assert on here.
        WindowManageabilityInfo info = reader.Read(default);

        Assert.False(info.IsVisible);
    }
}
