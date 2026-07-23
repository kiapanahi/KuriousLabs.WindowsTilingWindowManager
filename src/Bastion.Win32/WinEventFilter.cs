using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The WinEvent admission filter from DESIGN.md §3.1: <c>hwnd != NULL &amp;&amp; idObject ==
/// OBJID_WINDOW &amp;&amp; idChild == CHILDID_SELF</c>. Extracted out of
/// <see cref="WinEventPumpService"/>'s native callback as a small, directly-callable, pure
/// predicate — no live HWND, hook, or window is required to exercise it — so this admission logic
/// is independently unit-testable with synthetic handles (see <c>WinEventFilterTests</c>) rather
/// than only reachable via a Tier 3 real-window integration test.
/// </summary>
internal static class WinEventFilter
{
    /// <summary>
    /// Returns <see langword="true"/> only for a top-level-window "self" object event — the
    /// narrow slice of every <c>WinEventProc</c> invocation DESIGN.md §3.1 wants enqueued.
    /// Excludes non-window accessible children (scrollbars, menus, the caret, etc. — negative
    /// <paramref name="idObject"/> values per the <c>OBJID_*</c> family) and non-self
    /// <paramref name="idChild"/> values, as well as the sentinel null <paramref name="hwnd"/>
    /// some events report.
    /// </summary>
    public static bool IsRelevantWindowEvent(HWND hwnd, int idObject, int idChild) =>
        !hwnd.IsNull
        && idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW
        && idChild == (int)PInvoke.CHILDID_SELF;
}
