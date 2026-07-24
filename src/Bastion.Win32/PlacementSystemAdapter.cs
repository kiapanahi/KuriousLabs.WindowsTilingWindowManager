using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Bastion.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The real <see cref="IPlacementSystem"/>: every actual Win32 call DESIGN.md §3.6 names, wrapping
/// <see cref="WindowRegistry"/> for the one piece of identity resolution the executor needs.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Marshal.GetLastPInvokeError()</c>, not <c>GetLastWin32Error()</c> or a hand-rolled
/// <c>GetLastError</c> P/Invoke — resolving docs/engineering/interop.md §6's flagged-uncertain
/// item for real.</b> Confirmed empirically this session by inspecting the actual CsWin32
/// (0.3.298)-generated partial for every failable call below, under this project's
/// <c>DisableRuntimeMarshalling=true</c>: CsWin32 does <em>not</em> emit <c>[LibraryImport]</c> for
/// any of these plain P/Invokes at all (contrary to that doc section's prior framing) — it emits
/// classic <c>[DllImport]</c> (matching <c>Bastion.Win32.csproj</c>'s own documented RS0030
/// suppression rationale), and for every one of <c>SetWindowPlacement</c>, <c>SetWindowPos</c>,
/// <c>BeginDeferWindowPos</c>, <c>DeferWindowPos</c>, and <c>EndDeferWindowPos</c> specifically
/// (all of whose own documentation says "to get extended error information, call GetLastError"),
/// wraps the raw <c>[DllImport]</c> extern in a friendly method that does exactly:
/// <c>Marshal.SetLastSystemError(0)</c> immediately before the call, then
/// <c>Marshal.SetLastPInvokeError(Marshal.GetLastSystemError())</c> immediately after. This is
/// CsWin32's own hand-rolled substitute for the built-in automatic <c>SetLastError</c> capture that
/// <c>DisableRuntimeMarshallingAttribute</c> disables for classic interop (interop.md §1.2) — and
/// per <c>Marshal.GetLastPInvokeError</c>'s own documented contract ("corresponds to the error set
/// either by the most recent platform invoke... or by a call to SetLastPInvokeError(Int32),
/// whichever happened last"; "functionally equivalent to GetLastWin32Error... should be preferred"),
/// calling <c>Marshal.GetLastPInvokeError()</c> immediately after any of these five calls reliably
/// retrieves exactly the value that bridge captured. <c>IsWindowArranged</c> and
/// <c>IsIconic</c>/<c>IsZoomed</c> carry no such bridge in the generated output (consistent: neither
/// documents an extended-error contract), so this class never calls
/// <c>Marshal.GetLastPInvokeError()</c> after them. <c>SendMessageTimeout</c>'s raw extern
/// <em>does</em> carry the bridge too, but this class deliberately never reads it — see
/// <see cref="ProbeIsHung"/>'s remarks for the documented reason a bare zero-return check is
/// correct there regardless.
/// </para>
/// <para>
/// <b><c>HDWP</c> is not a kernel handle either.</b> Extending interop.md §2's reasoning for
/// <c>HWND</c>/<c>HHOOK</c>/<c>HWINEVENTHOOK</c> to the one other non-<c>CloseHandle</c>-family
/// handle this issue introduces: <c>HDWP</c> is released via <c>EndDeferWindowPos</c> (which also
/// consumes it to actually reposition every deferred window), never <c>CloseHandle</c>, and
/// <c>DeferWindowPos</c> can return a <em>different</em> <c>HDWP</c> value than the one passed in
/// (its own documented contract) — there is no correct universal <c>SafeHandle.ReleaseHandle</c>
/// for a value whose own identity can change out from under a wrapper mid-sequence. It stays a raw
/// blittable CsWin32-generated struct, exactly like <c>HWND</c>.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered as the production IPlacementSystem once Bastion.Daemon's composition root is " +
        "wired (GitHub issue #10) — not yet wired as of this change. Same documented CA1812 " +
        "false-positive shape as Coalescer/WindowSystemAdapter/WinEventPumpService/" +
        "ReconcilerIntentPump/BastiondService.")]
internal sealed class PlacementSystemAdapter(WindowRegistry registry) : IPlacementSystem
{
    /// <inheritdoc/>
    public bool TryResolveHwnd(WindowId windowId, out HWND hwnd) => registry.TryGetHwnd(windowId, out hwnd);

    /// <inheritdoc/>
    /// <remarks>
    /// DOCUMENTED CONTRACT (verified against
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw):
    /// "If the function fails or times out, the return value is 0... the function does not always
    /// call SetLastError on failure. If the reason for failure is important to you, call
    /// SetLastError(ERROR_SUCCESS) before calling SendMessageTimeout. If the function returns 0, and
    /// GetLastError returns ERROR_SUCCESS, then treat it as a generic failure." Since this method
    /// only cares about "did it hang," not the precise reason, a bare zero-return check is
    /// sufficient and deliberately never inspects <c>Marshal.GetLastPInvokeError()</c> — doing so
    /// would require the documented <c>SetLastError(ERROR_SUCCESS)</c>-before-calling dance this
    /// method has no need for.
    /// </remarks>
    public bool ProbeIsHung(HWND hwnd, TimeSpan timeout)
    {
        LRESULT result = PInvoke.SendMessageTimeout(
            hwnd,
            PInvoke.WM_NULL,
            wParam: default,
            lParam: default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
            (uint)timeout.TotalMilliseconds);
        return result == 0;
    }

    /// <inheritdoc/>
    public WindowPlacementState ReadPlacementState(HWND hwnd)
    {
        WINDOW_EX_STYLE exStyle = WindowProbe.GetExtendedStyle(hwnd);
        return new WindowPlacementState(
            IsIconic: PInvoke.IsIconic(hwnd),
            IsZoomed: PInvoke.IsZoomed(hwnd),
            IsArranged: PInvoke.IsWindowArranged(hwnd),
            IsToolWindow: (exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != (WINDOW_EX_STYLE)0);
    }

    /// <inheritdoc/>
    public bool TryReadGeometry(HWND hwnd, out Rect windowRect, out Rect frameBounds)
    {
        if (!WindowProbe.TryGetBounds(hwnd, out RECT rawWindowRect))
        {
            windowRect = default;
            frameBounds = default;
            return false;
        }

        windowRect = ToRect(rawWindowRect);
        frameBounds = WindowProbe.TryGetExtendedFrameBounds(hwnd, out RECT rawFrameBounds) ? ToRect(rawFrameBounds) : windowRect;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// DOCUMENTED CONTRACT (verified against
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfow and
    /// https://learn.microsoft.com/windows/win32/gdi/multiple-monitor-system-metrics): "Using
    /// SPI_GETWORKAREA always returns the work area of the primary monitor." No documented failure
    /// mode is called out for this specific action; on the (undocumented) chance the call ever
    /// fails, this falls back to an all-zero <see cref="Rect"/> — i.e. "no correction" — which is a
    /// safe, conservative default (it makes <see cref="PlacementCoordinateConverter.ToWorkspaceCoordinates"/>
    /// a no-op rather than applying a bogus offset).
    /// </remarks>
    public Rect ReadPrimaryWorkArea()
    {
        RECT rect = default;
        bool succeeded;
        unsafe
        {
            succeeded = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWORKAREA, uiParam: 0, &rect, fWinIni: default);
        }

        return succeeded ? ToRect(rect) : default;
    }

    /// <inheritdoc/>
    public PlacementCallResult ApplyWindowPlacement(HWND hwnd, Rect rcNormalPosition)
    {
        uint length;
        unsafe
        {
            length = (uint)sizeof(WINDOWPLACEMENT);
        }

        var placement = new WINDOWPLACEMENT
        {
            length = length,
            flags = WINDOWPLACEMENT_FLAGS.WPF_ASYNCWINDOWPLACEMENT,
            showCmd = SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE,
            rcNormalPosition = ToRECT(rcNormalPosition),
        };

        return PInvoke.SetWindowPlacement(hwnd, placement)
            ? PlacementCallResult.Ok
            : PlacementCallResult.Fail((WIN32_ERROR)Marshal.GetLastPInvokeError());
    }

    /// <inheritdoc/>
    public HDWP BeginDefer(int windowCount) => PInvoke.BeginDeferWindowPos(windowCount);

    /// <inheritdoc/>
    public HDWP? TryDefer(HDWP batch, HWND hwnd, Rect screenBounds)
    {
        HDWP result = PInvoke.DeferWindowPos(
            batch,
            hwnd,
            hWndInsertAfter: default,
            (int)Math.Round(screenBounds.Left),
            (int)Math.Round(screenBounds.Top),
            (int)Math.Round(screenBounds.Width),
            (int)Math.Round(screenBounds.Height),
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        return result.IsNull ? null : result;
    }

    /// <inheritdoc/>
    public bool EndDefer(HDWP batch) => PInvoke.EndDeferWindowPos(batch);

    /// <inheritdoc/>
    public PlacementCallResult ApplyWindowPosFallback(HWND hwnd, Rect screenBounds)
    {
        bool succeeded = PInvoke.SetWindowPos(
            hwnd,
            hWndInsertAfter: default,
            (int)Math.Round(screenBounds.Left),
            (int)Math.Round(screenBounds.Top),
            (int)Math.Round(screenBounds.Width),
            (int)Math.Round(screenBounds.Height),
            SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        return succeeded
            ? PlacementCallResult.Ok
            : PlacementCallResult.Fail((WIN32_ERROR)Marshal.GetLastPInvokeError());
    }

    private static Rect ToRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    private static RECT ToRECT(Rect rect) => new(
        (int)Math.Round(rect.Left),
        (int)Math.Round(rect.Top),
        (int)Math.Round(rect.Right),
        (int)Math.Round(rect.Bottom));
}
