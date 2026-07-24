using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// One journaled window's result from an <see cref="HwndJournalRestorer.RestoreAllAsync"/> pass —
/// mirrors <see cref="PlacementOutcome"/>'s own Kind-plus-factory-methods shape (GitHub issue #8).
/// </summary>
/// <param name="Entry">The journal entry this outcome describes.</param>
/// <param name="Kind">What happened.</param>
/// <param name="ErrorCode">The Win32 error code, when <paramref name="Kind"/> is <see cref="JournalRestoreOutcomeKind.Failed"/>; <see langword="null"/> otherwise.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct JournalRestoreOutcome(JournalEntry Entry, JournalRestoreOutcomeKind Kind, WIN32_ERROR? ErrorCode)
{
    /// <summary>Creates a <see cref="JournalRestoreOutcomeKind.Restored"/> outcome.</summary>
    public static JournalRestoreOutcome Restored(JournalEntry entry) => new(entry, JournalRestoreOutcomeKind.Restored, null);

    /// <summary>Creates a <see cref="JournalRestoreOutcomeKind.SkippedWindowGone"/> outcome.</summary>
    public static JournalRestoreOutcome SkippedWindowGone(JournalEntry entry) => new(entry, JournalRestoreOutcomeKind.SkippedWindowGone, null);

    /// <summary>Creates a <see cref="JournalRestoreOutcomeKind.SkippedHwndRecycled"/> outcome.</summary>
    public static JournalRestoreOutcome SkippedHwndRecycled(JournalEntry entry) => new(entry, JournalRestoreOutcomeKind.SkippedHwndRecycled, null);

    /// <summary>Creates a <see cref="JournalRestoreOutcomeKind.Failed"/> outcome.</summary>
    public static JournalRestoreOutcome Failed(JournalEntry entry, WIN32_ERROR? errorCode) => new(entry, JournalRestoreOutcomeKind.Failed, errorCode);
}
