using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-desktop tests (docs/engineering/testing.md §3) for
/// <see cref="DwmCloakStateReader"/> — same style as <see cref="WindowProbeTests"/>: no faking of
/// <c>DwmGetWindowAttribute</c> here, only invariants that hold for <em>any</em> live desktop
/// (never a specific window's cloak state), so a real, empty-desktop CI runner still passes.
/// </summary>
public sealed class DwmCloakStateReaderTests
{
    [Fact]
    public void IsCloakedCompletesWithoutThrowingForEveryVisibleTopLevelWindow()
    {
        var reader = new DwmCloakStateReader();
        IReadOnlyList<HWND> windows = WindowProbe.EnumerateVisibleTopLevelWindows();

        foreach (HWND window in windows)
        {
            // No assertion on the specific value — a live runner's actual cloak state is unknown
            // and irrelevant here. This only confirms the real DwmGetWindowAttribute call
            // (HRESULT handling, Span<byte> buffer marshaling — interop.md §1) succeeds
            // structurally against real windows; CoalescerTests exercises the resulting bool
            // through Coalescer's cloak-burst heuristic via the fake seam instead.
            _ = reader.IsCloaked(window);
        }
    }

    [Fact]
    public void IsCloakedReturnsFalseForAnInvalidWindowHandleRatherThanThrowing()
    {
        var reader = new DwmCloakStateReader();

        // An HWND value that (almost certainly) never identifies a real window on this session —
        // exercises the documented-failure path (a HWND that doesn't resolve makes
        // DwmGetWindowAttribute return a failing HRESULT) without needing a live window to
        // fabricate the failure. DwmCloakStateReader's own remarks document the conservative
        // false-on-failure default this asserts.
        bool result = reader.IsCloaked(unchecked((nint)0xDEADBEEF));

        Assert.False(result);
    }
}
