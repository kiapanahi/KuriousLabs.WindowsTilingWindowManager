using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="ICloakStateReader"/>, backed by <c>DwmGetWindowAttribute(DWMWA_CLOAKED)</c>.
/// </summary>
/// <remarks>
/// <para>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/dwmapi/nf-dwmapi-dwmgetwindowattribute and
/// https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute):
/// <c>DwmGetWindowAttribute(HWND, DWORD dwAttribute, PVOID pvAttribute, DWORD cbAttribute) -&gt;
/// HRESULT</c>; with <c>dwAttribute = DWMWA_CLOAKED</c>, "If the window is cloaked, provides one of
/// the following values explaining why: DWM_CLOAKED_APP (0x1), DWM_CLOAKED_SHELL (0x2),
/// DWM_CLOAKED_INHERITED (0x4)." The docs do not separately spell out a not-cloaked reading of
/// exactly zero, but DESIGN.md §3.3/§4 already commits to that reading ("DWMWA_CLOAKED == 0") as
/// the inverse of "provides one of the following [nonzero] values" — this class implements that
/// existing, already-cited design decision rather than re-deriving it.
/// </para>
/// <para>
/// CsWin32-generated shape (pinned 0.3.298; confirmed by inspecting the actual generated partial
/// per this repo's "confirm by inspecting the generated partial, don't guess" convention —
/// interop.md §1): CsWin32 emits a friendly <c>Span&lt;byte&gt;</c>-taking overload,
/// <c>internal static unsafe HRESULT DwmGetWindowAttribute(HWND hwnd, DWMWINDOWATTRIBUTE
/// dwAttribute, Span&lt;byte&gt; pvAttribute)</c>, alongside the raw <c>void* pvAttribute, uint
/// cbAttribute</c> overload. This class uses the <c>Span&lt;byte&gt;</c> overload with a 4-byte
/// <c>stackalloc</c> buffer (<c>DWMWA_CLOAKED</c>'s documented retrieved type is <c>DWORD</c>) —
/// no heap allocation, and no raw pointer arithmetic of our own. The generated <c>HRESULT</c>
/// struct exposes <c>internal bool Succeeded</c>/<c>Failed</c> properties (also confirmed from the
/// generated output), used below rather than a bare integer comparison.
/// </para>
/// </remarks>
internal sealed class DwmCloakStateReader : ICloakStateReader
{
    /// <inheritdoc/>
    public bool IsCloaked(nint hwnd)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];

        HRESULT hr;
        unsafe
        {
            // The Span<byte> overload's generated signature is itself `unsafe` (it takes the
            // address of the span internally) even though nothing in this call site needs a raw
            // pointer of our own — CsWin32 emits it this way, so the call must sit in an unsafe
            // context regardless (interop.md §1: "confirm by inspecting the generated partial").
            hr = PInvoke.DwmGetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, buffer);
        }

        if (hr.Failed)
        {
            // Conservative default: no evidence of cloaking beats risking a false-positive
            // DesktopSwitchSuspected off a failed read (e.g. the window was destroyed between the
            // WinEvent firing and this call — a routine race, not exceptional). The 5 s
            // reconciliation heartbeat (DESIGN.md §3.4) backstops any state this leaves stale.
            return false;
        }

        uint cloakedValue = BitConverter.ToUInt32(buffer);
        return cloakedValue != 0;
    }
}
