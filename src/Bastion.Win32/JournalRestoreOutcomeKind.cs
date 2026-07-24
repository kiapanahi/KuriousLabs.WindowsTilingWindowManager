namespace Bastion.Win32;

/// <summary>What happened when <see cref="HwndJournalRestorer"/> attempted to restore one <see cref="JournalEntry"/>.</summary>
internal enum JournalRestoreOutcomeKind
{
    /// <summary><c>SetWindowPlacement</c> succeeded.</summary>
    Restored,

    /// <summary>
    /// The window no longer exists (<c>GetWindowThreadProcessId</c> failed for the journaled HWND
    /// value) — nothing to restore. Not an error: the window was legitimately closed since the
    /// entry was journaled.
    /// </summary>
    SkippedWindowGone,

    /// <summary>
    /// DESIGN.md §9's HWND-recycling honesty note, made concrete: the journaled HWND value now
    /// resolves to a live window, but one owned by a <em>different</em> process than
    /// <see cref="JournalEntry.ProcessId"/> recorded — the original window is gone and this numeric
    /// value has been recycled to something unrelated. Restoring onto it would move a stranger's
    /// window, so this entry is skipped instead.
    /// </summary>
    SkippedHwndRecycled,

    /// <summary>
    /// The window still exists and is still owned by the recorded process, but
    /// <c>SetWindowPlacement</c> itself failed (e.g. <c>ERROR_ACCESS_DENIED</c> against an elevated
    /// window, DESIGN.md §3.6/§9). Retained in the journal (not cleared) so a future restore attempt
    /// can retry — see <see cref="HwndJournalRestorer"/>'s remarks.
    /// </summary>
    Failed,
}
