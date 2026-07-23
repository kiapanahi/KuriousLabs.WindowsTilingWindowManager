using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="WinEventRootNormalizer.NormalizeRoot"/> — the pure fallback extracted from
/// <see cref="WinEventPumpService"/>'s native callback so a documented-but-unspecified
/// <c>GetAncestor</c> failure (a null root) never discards a perfectly good window identity — no
/// live HWND, hook, or window required.
/// </summary>
public sealed class WinEventRootNormalizerTests
{
    private static readonly HWND s_originalWindow = new(new IntPtr(0x1234));
    private static readonly HWND s_rootWindow = new(new IntPtr(0x5678));

    [Fact]
    public void NonNullRootAncestorPassesThroughUnchanged()
    {
        HWND result = WinEventRootNormalizer.NormalizeRoot(s_originalWindow, s_rootWindow);

        Assert.Equal(s_rootWindow, result);
    }

    [Fact]
    public void NullRootAncestorFallsBackToTheOriginalHwnd()
    {
        HWND result = WinEventRootNormalizer.NormalizeRoot(s_originalWindow, HWND.Null);

        Assert.Equal(s_originalWindow, result);
    }
}
