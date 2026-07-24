using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3) for <see cref="WindowProbe"/>.
/// </summary>
/// <remarks>
/// These exercise the real Win32 desktop of whatever machine/CI runner executes them — there is
/// no faking of <c>EnumWindows</c>/<c>GetWindowRect</c>/<c>GetAncestor</c> here. They therefore
/// only assert invariants that hold for *any* live desktop (non-null enumeration, well-formed
/// rectangles, root-ancestor self-resolution for an already-top-level window), never a specific
/// window count or a specific window's identity — a real, empty-desktop CI runner must still pass.
/// Tier 3's dedicated <c>Bastion.TestWindows</c>-driven scenarios (spawn-and-assert-placement) are
/// a separate, quarantined suite per DESIGN.md §11 and are not duplicated here.
/// </remarks>
public sealed class WindowProbeTests
{
    [Fact]
    public void EnumerateVisibleTopLevelWindowsReturnsANonNullList()
    {
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        Assert.NotNull(windows);
    }

    [Fact]
    public void EnumeratedWindowBoundsAreWellFormedWhenQueryable()
    {
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // A window can legitimately be destroyed between enumeration and this call — a
            // routine race (WindowProbe.TryGetBounds's own doc remark), not a test failure.
            if (WindowProbe.TryGetBounds(window, out RECT bounds))
            {
                Assert.True(bounds.right >= bounds.left);
                Assert.True(bounds.bottom >= bounds.top);
            }
        }
    }

    [Fact]
    public void RootAncestorOfATopLevelWindowIsItself()
    {
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // EnumWindows only yields already-top-level windows, so GA_ROOT should resolve back
            // to the same window — DESIGN.md §3.1's WinEvent-normalization rule exists for
            // child/owned windows reported via WinEvents, not for windows already obtained from
            // EnumWindows.
            Assert.Equal(window, WindowProbe.GetRootAncestor(window));
        }
    }

    [Fact]
    public void ExtendedFrameBoundsAreWellFormedWhenQueryable()
    {
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // A window can legitimately be destroyed between enumeration and this call, or the
            // DWM read itself can fail for other reasons — a routine race (this method's own doc
            // remarks), not a test failure.
            if (WindowProbe.TryGetExtendedFrameBounds(window, out RECT frameBounds))
            {
                Assert.True(frameBounds.right >= frameBounds.left);
                Assert.True(frameBounds.bottom >= frameBounds.top);
            }
        }
    }

    [Fact]
    public void GetExtendedStyleNeverThrowsForAnyEnumeratedWindow()
    {
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // GetWindowLongW has no documented failure mode distinct from "returns 0" (which is
            // itself a valid, all-styles-clear reading) -- this only asserts the call never throws
            // for whatever a live desktop has open.
            _ = WindowProbe.GetExtendedStyle(window);
        }
    }
}
