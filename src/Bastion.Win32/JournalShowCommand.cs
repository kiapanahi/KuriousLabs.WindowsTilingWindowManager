namespace Bastion.Win32;

/// <summary>
/// The on-disk, CsWin32-independent counterpart of <c>WINDOWPLACEMENT.showCmd</c> for a journaled
/// window (GitHub issue #8, DESIGN.md §3.7). Deliberately its own small enum rather than persisting
/// <c>Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD</c> directly: the journal file is a
/// long-lived on-disk format that must stay stable across a CsWin32 version bump (a regenerated
/// <c>SHOW_WINDOW_CMD</c> is not itself a contract Bastion controls), whereas this type's three
/// members are Bastion's own, version-independent vocabulary.
/// </summary>
/// <remarks>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowplacement#remarks):
/// "If the window identified by the hWnd parameter is maximized, the showCmd member is
/// SW_SHOWMAXIMIZED. If the window is minimized, showCmd is SW_SHOWMINIMIZED. Otherwise, it is
/// SW_SHOWNORMAL." <c>GetWindowPlacement</c> therefore never produces any other <c>showCmd</c>
/// value, so exactly these three members are exhaustive for anything <see cref="JournalEntryCapture"/>
/// captures — see that type and <see cref="JournalPlacementSystemAdapter"/> for the two directions
/// of conversion to/from <c>SHOW_WINDOW_CMD</c>.
/// </remarks>
internal enum JournalShowCommand
{
    /// <summary>The window was neither minimized nor maximized (<c>SW_SHOWNORMAL</c>).</summary>
    Normal,

    /// <summary>The window was minimized (<c>SW_SHOWMINIMIZED</c>).</summary>
    Minimized,

    /// <summary>The window was maximized (<c>SW_SHOWMAXIMIZED</c>).</summary>
    Maximized,
}
