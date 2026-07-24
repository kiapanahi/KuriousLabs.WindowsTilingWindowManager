using System.Runtime.InteropServices;
using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// Plain-data, JSON-serializable mirror of the restore-relevant fields of Win32's
/// <c>WINDOWPLACEMENT</c> struct, captured by <see cref="JournalEntryCapture"/> before a window is
/// hidden/moved away and replayed by <see cref="HwndJournalRestorer"/> (GitHub issue #8, DESIGN.md
/// §3.7's "pre-management <c>WINDOWPLACEMENT</c>"). <c>WINDOWPLACEMENT</c> itself is a Win32 struct
/// (and, per CLAUDE.md §3, must never cross out of <c>Bastion.Win32</c> — irrelevant for a JSON file
/// but kept anyway for the same "own, stable schema, not a codegen artifact" reason as
/// <see cref="JournalShowCommand"/>), so this record carries only plain, portable field types.
/// </summary>
/// <param name="ShowCommand">The window's show state at capture time — see <see cref="JournalShowCommand"/>'s own remarks for why <c>GetWindowPlacement</c> guarantees exactly one of its three members here.</param>
/// <param name="MinPositionX">
/// <c>ptMinPosition.X</c> — the window's upper-left corner when minimized (DOCUMENTED CONTRACT,
/// https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-windowplacement#members).
/// </param>
/// <param name="MinPositionY"><c>ptMinPosition.Y</c> — see <paramref name="MinPositionX"/>.</param>
/// <param name="MaxPositionX">
/// <c>ptMaxPosition.X</c> — the window's upper-left corner when maximized (same documented
/// source as <paramref name="MinPositionX"/>).
/// </param>
/// <param name="MaxPositionY"><c>ptMaxPosition.Y</c> — see <paramref name="MaxPositionX"/>.</param>
/// <param name="NormalPosition">
/// <c>rcNormalPosition</c> — the window's restored-position coordinates (workspace coordinates for
/// a non-tool-window top-level window, screen coordinates otherwise — same documented source as
/// <paramref name="MinPositionX"/>; this record does not itself resolve which space applies, since
/// that depends on the live <c>WS_EX_TOOLWINDOW</c> style at capture time, already accounted for by
/// however <see cref="JournalEntryCapture"/> read it).
/// </param>
/// <remarks>
/// <para>
/// <b>Deliberately omits two <c>WINDOWPLACEMENT</c> fields.</b> <c>flags</c> is omitted because
/// <c>GetWindowPlacement</c>'s own documented contract states "The flags member of
/// <c>WINDOWPLACEMENT</c> retrieved by this function is always zero" — persisting a field whose
/// captured value is contractually always the same constant would be pure noise; the restore side
/// (<see cref="JournalPlacementSystemAdapter"/>) instead always sets <c>WPF_ASYNCWINDOWPLACEMENT</c>
/// on write, matching <c>PlacementSystemAdapter.ApplyWindowPlacement</c>'s own established
/// convention for the identical reason (avoids blocking on a foreign window's input queue). The
/// legacy <c>rcDevice</c> field is omitted because it is not even present on this project's
/// CsWin32-generated <c>WINDOWPLACEMENT</c> projection (confirmed against the actual generated
/// output this session) and its own documentation is silent on its semantics.
/// </para>
/// <para>
/// <b>Honesty note (DESIGN.md §9's "User snaps a window" row).</b> A snapped ("arranged") window's
/// <c>GetWindowPlacement</c> capture reads back as <see cref="JournalShowCommand.Normal"/> — per
/// <c>IsWindowArranged</c>'s own documented remarks, "the <i>showCmd</i> member on the returned
/// <c>WINDOWPLACEMENT</c> can have a value of <c>SW_SHOWNORMAL</c> even if the window is arranged" —
/// and there is no documented <c>SW_*</c> value that puts a window back into the arranged state.
/// Restoring a journaled entry therefore cannot resurrect Snap layout membership; it lands the
/// window at <c>rcNormalPosition</c> as an ordinary restored window. This is an accepted limitation
/// of the documented surface, not a bug to work around with an undocumented trick (CLAUDE.md §2).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct JournalWindowPlacement(
    JournalShowCommand ShowCommand,
    int MinPositionX,
    int MinPositionY,
    int MaxPositionX,
    int MaxPositionY,
    Rect NormalPosition);
