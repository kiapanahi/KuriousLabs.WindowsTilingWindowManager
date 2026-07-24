namespace Bastion.Win32;

/// <summary>
/// A schema slot in <see cref="JournalEntry"/> for the corner-preference cosmetic flag DESIGN.md
/// §3.6/§3.7/§13.1 describes (<c>DWMWA_WINDOW_CORNER_PREFERENCE</c>, journaled for crash-recovery
/// undo) — GitHub issue #34 owns actually toggling it, not this issue. <see cref="Unset"/> is the
/// only value this issue ever writes; the shape exists so issue #34 can add real members
/// (mirroring <c>DWMWCP_DEFAULT</c>/<c>DWMWCP_DONOTROUND</c>/<c>DWMWCP_ROUND</c>/
/// <c>DWMWCP_ROUNDSMALL</c>) without changing <see cref="JournalEntry"/>'s field set — only this
/// enum's member list grows.
/// </summary>
internal enum JournalCornerPreference
{
    /// <summary>No corner-preference state was recorded (issue #34 not yet implemented).</summary>
    Unset,
}
