using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Bastion.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The real <see cref="IJournalPlacementSystem"/>: <c>GetWindowPlacement</c>/<c>SetWindowPlacement</c>
/// for the write-ahead journal (GitHub issue #8, DESIGN.md §3.7).
/// </summary>
/// <remarks>
/// <c>Marshal.GetLastPInvokeError()</c> on failure, not <c>GetLastWin32Error()</c> — same
/// confirmed-empirically bridge <see cref="PlacementSystemAdapter"/>'s own remarks document for
/// <c>SetWindowPlacement</c>; <c>GetWindowPlacement</c>'s generated wrapper carries the identical
/// <c>Marshal.SetLastSystemError(0)</c>/<c>Marshal.SetLastPInvokeError</c> bridge (confirmed against
/// the actual generated partial this session), so the same retrieval mechanism applies to both
/// calls this adapter makes.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and by Bastion.Cli's " +
        "Program.cs (a different assembly) for `bastionc restore-windows`; intended to also be " +
        "registered once Bastion.Daemon's composition root is wired (GitHub issue #10). Same " +
        "documented CA1812 false-positive shape as PlacementSystemAdapter/WindowSystemAdapter.")]
internal sealed class JournalPlacementSystemAdapter : IJournalPlacementSystem
{
    /// <inheritdoc/>
    public bool TryCapturePlacement(HWND hwnd, out JournalWindowPlacement placement)
    {
        uint length;
        unsafe
        {
            length = (uint)sizeof(WINDOWPLACEMENT);
        }

        var native = new WINDOWPLACEMENT { length = length };
        if (!PInvoke.GetWindowPlacement(hwnd, ref native))
        {
            placement = default;
            return false;
        }

        placement = new JournalWindowPlacement(
            ToJournalShowCommand(native.showCmd),
            native.ptMinPosition.X,
            native.ptMinPosition.Y,
            native.ptMaxPosition.X,
            native.ptMaxPosition.Y,
            ToRect(native.rcNormalPosition));
        return true;
    }

    /// <inheritdoc/>
    public PlacementCallResult ApplyWindowPlacement(HWND hwnd, JournalWindowPlacement placement)
    {
        uint length;
        unsafe
        {
            length = (uint)sizeof(WINDOWPLACEMENT);
        }

        var native = new WINDOWPLACEMENT
        {
            length = length,
            // Always posted, never blocking -- matching PlacementSystemAdapter.ApplyWindowPlacement's
            // own established convention (see JournalWindowPlacement's remarks for why this journal
            // schema does not itself persist a captured `flags` value: GetWindowPlacement always
            // reads it back as zero).
            flags = WINDOWPLACEMENT_FLAGS.WPF_ASYNCWINDOWPLACEMENT,
            showCmd = ToShowWindowCmd(placement.ShowCommand),
            ptMinPosition = new System.Drawing.Point(placement.MinPositionX, placement.MinPositionY),
            ptMaxPosition = new System.Drawing.Point(placement.MaxPositionX, placement.MaxPositionY),
            rcNormalPosition = ToRECT(placement.NormalPosition),
        };

        return PInvoke.SetWindowPlacement(hwnd, native)
            ? PlacementCallResult.Ok
            : PlacementCallResult.Fail((WIN32_ERROR)Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// DOCUMENTED CONTRACT (verified against
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowplacement#remarks):
    /// "If the window identified by the hWnd parameter is maximized, the showCmd member is
    /// SW_SHOWMAXIMIZED. If the window is minimized, showCmd is SW_SHOWMINIMIZED. Otherwise, it is
    /// SW_SHOWNORMAL." — exhaustive, so the fallback arm below is reached only for
    /// <c>SW_SHOWNORMAL</c> in practice, never a genuinely unrecognized value.
    /// </summary>
    private static JournalShowCommand ToJournalShowCommand(SHOW_WINDOW_CMD showCmd) => showCmd switch
    {
        SHOW_WINDOW_CMD.SW_SHOWMINIMIZED => JournalShowCommand.Minimized,
        SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED => JournalShowCommand.Maximized,
        _ => JournalShowCommand.Normal,
    };

    private static SHOW_WINDOW_CMD ToShowWindowCmd(JournalShowCommand command) => command switch
    {
        JournalShowCommand.Minimized => SHOW_WINDOW_CMD.SW_SHOWMINIMIZED,
        JournalShowCommand.Maximized => SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED,
        _ => SHOW_WINDOW_CMD.SW_SHOWNORMAL,
    };

    private static Rect ToRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    private static RECT ToRECT(Rect rect) => new(
        (int)Math.Round(rect.Left),
        (int)Math.Round(rect.Top),
        (int)Math.Round(rect.Right),
        (int)Math.Round(rect.Bottom));
}
