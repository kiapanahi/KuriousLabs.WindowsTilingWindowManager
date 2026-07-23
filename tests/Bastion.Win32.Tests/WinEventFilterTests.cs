using Bastion.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="WinEventFilter.IsRelevantWindowEvent"/> — the pure predicate extracted from
/// <see cref="WinEventPumpService"/>'s native callback so DESIGN.md §3.1's admission filter
/// (<c>hwnd != NULL &amp;&amp; idObject == OBJID_WINDOW &amp;&amp; idChild == CHILDID_SELF</c>) is
/// unit-testable with synthetic handles — no real hook or window required.
/// </summary>
public sealed class WinEventFilterTests
{
    private static readonly HWND s_arbitraryWindow = new(new IntPtr(0x1234));

    [Fact]
    public void AcceptsATopLevelWindowSelfObjectEvent()
    {
        bool relevant = WinEventFilter.IsRelevantWindowEvent(
            s_arbitraryWindow,
            idObject: (int)OBJECT_IDENTIFIER.OBJID_WINDOW,
            idChild: (int)PInvoke.CHILDID_SELF);

        Assert.True(relevant);
    }

    [Fact]
    public void RejectsANullWindowHandle()
    {
        bool relevant = WinEventFilter.IsRelevantWindowEvent(
            HWND.Null,
            idObject: (int)OBJECT_IDENTIFIER.OBJID_WINDOW,
            idChild: (int)PInvoke.CHILDID_SELF);

        Assert.False(relevant);
    }

    [Theory]
    [InlineData((int)OBJECT_IDENTIFIER.OBJID_CLIENT)]
    [InlineData((int)OBJECT_IDENTIFIER.OBJID_TITLEBAR)]
    [InlineData((int)OBJECT_IDENTIFIER.OBJID_VSCROLL)]
    [InlineData((int)OBJECT_IDENTIFIER.OBJID_SYSMENU)]
    public void RejectsNonWindowObjectIds(int idObject)
    {
        bool relevant = WinEventFilter.IsRelevantWindowEvent(
            s_arbitraryWindow,
            idObject,
            idChild: (int)PInvoke.CHILDID_SELF);

        Assert.False(relevant);
    }

    [Fact]
    public void RejectsANonSelfChildId()
    {
        bool relevant = WinEventFilter.IsRelevantWindowEvent(
            s_arbitraryWindow,
            idObject: (int)OBJECT_IDENTIFIER.OBJID_WINDOW,
            idChild: 1); // any non-zero child index — an element within the window, not itself.

        Assert.False(relevant);
    }
}
