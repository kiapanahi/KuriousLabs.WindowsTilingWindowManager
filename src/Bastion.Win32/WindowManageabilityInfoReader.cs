using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IWindowManageabilityInfoReader"/>: gathers <see cref="WindowManageabilityInfo"/>
/// from a live window via the documented Win32/DWM reads DESIGN.md §3.3 names.
/// </summary>
/// <remarks>
/// <para>
/// DOCUMENTED CONTRACT for every read below (all verified against learn.microsoft.com):
/// <c>GetAncestor</c> (via <see cref="WindowProbe.GetRootAncestor"/>/<c>GA_ROOT</c>),
/// <c>IsWindowVisible</c>,
/// <c>DwmGetWindowAttribute(DWMWA_CLOAKED)</c> — "If the window is cloaked, provides one of the
/// following values explaining why" (nonzero); the specific reason is never inspected beyond the
/// zero/nonzero read, per DESIGN.md §3.3/§4's "Bastion deliberately does not depend on the
/// specific reason flag" —,
/// <c>GetWindowLongW(GWL_EXSTYLE)</c> (via <see cref="WindowProbe.GetExtendedStyle"/>, whose own
/// remarks carry the <c>GetWindowLongW</c>-vs-<c>GetWindowLongPtrW</c> citation, shared with GitHub
/// issue #5's <see cref="PlacementSystemAdapter"/> rather than duplicated here a second time),
/// <c>GetWindow(GW_OWNER)</c>, <c>GetWindowRect</c> (via <see cref="WindowProbe.TryGetBounds"/>),
/// <c>GetShellWindow</c>, and <c>GetClassName</c> (via <see cref="WindowProbe.GetClassName"/>).
/// </para>
/// <para>
/// CsWin32-generated shapes (pinned 0.3.298; confirmed by inspecting the actual generated partial,
/// per this repo's "confirm by inspecting the generated partial, don't guess" convention —
/// interop.md §1): <c>DwmGetWindowAttribute</c> has a friendly <c>Span&lt;byte&gt;</c> overload
/// (no raw pointer arithmetic of our own, matching the pattern already used for this exact call in
/// the Coalescer's cloak read, GitHub issue #2).
/// </para>
/// </remarks>
internal sealed class WindowManageabilityInfoReader : IWindowManageabilityInfoReader
{
    /// <inheritdoc/>
    public WindowManageabilityInfo Read(HWND hwnd)
    {
        bool isRootWindow = WindowProbe.GetRootAncestor(hwnd) == hwnd;
        bool isVisible = PInvoke.IsWindowVisible(hwnd);
        bool isCloaked = IsCloaked(hwnd);

        WINDOW_EX_STYLE exStyle = WindowProbe.GetExtendedStyle(hwnd);
        bool hasToolWindowStyle = (exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != (WINDOW_EX_STYLE)0;
        bool hasAppWindowStyle = (exStyle & WINDOW_EX_STYLE.WS_EX_APPWINDOW) != (WINDOW_EX_STYLE)0;
        bool hasNoActivateStyle = (exStyle & WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != (WINDOW_EX_STYLE)0;

        bool hasOwner = !PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull;
        bool hasEmptyRect = !WindowProbe.TryGetBounds(hwnd, out RECT bounds)
            || bounds.right <= bounds.left
            || bounds.bottom <= bounds.top;
        bool isShellWindow = hwnd == PInvoke.GetShellWindow();
        string className = WindowProbe.GetClassName(hwnd);

        return new WindowManageabilityInfo(
            IsRootWindow: isRootWindow,
            IsVisible: isVisible,
            IsCloaked: isCloaked,
            HasOwner: hasOwner,
            HasToolWindowStyle: hasToolWindowStyle,
            HasAppWindowStyle: hasAppWindowStyle,
            HasNoActivateStyle: hasNoActivateStyle,
            HasEmptyRect: hasEmptyRect,
            IsShellWindow: isShellWindow,
            ClassName: className);
    }

    private static bool IsCloaked(HWND hwnd)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        HRESULT hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, buffer);
        if (hr.Failed)
        {
            // Conservative default: no evidence of cloaking beats a false-positive "keep tracked,
            // never tile" verdict off a failed read (e.g. the window was destroyed between the
            // triggering event and this call — a routine race, not exceptional). The 5 s
            // reconciliation heartbeat (DESIGN.md §3.4, once GitHub issue #4 lands) backstops any
            // state this leaves stale.
            return false;
        }

        uint cloakedValue = BitConverter.ToUInt32(buffer);
        return cloakedValue != 0;
    }
}
