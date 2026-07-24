namespace Bastion.Win32;

/// <summary>
/// The file-I/O seam <see cref="HwndJournalWriter"/> and <see cref="HwndJournalRestorer"/> depend
/// on for the on-disk write-ahead journal (GitHub issue #8, DESIGN.md §3.7), matching
/// <c>docs/engineering/testing.md</c> §5's Tier-2 seam shape: <see cref="HwndJournalStore"/> is the
/// real, file-system-backed implementation; <c>Bastion.Win32.Tests</c>'s
/// <c>FakeHwndJournalStore</c> is the in-memory fake the write-before-hide ordering test drives.
/// </summary>
internal interface IHwndJournalStore
{
    /// <summary>
    /// Reads the current journal document. Returns <see cref="JournalDocument.Empty"/> if no
    /// journal file exists yet (a fresh install, or a journal that has never been written to) —
    /// this is the routine, expected "nothing outstanding" case, not an error.
    /// </summary>
    Task<JournalDocument> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably writes <paramref name="document"/>, replacing whatever was previously on disk. Must
    /// have fully completed (the returned <see cref="Task"/> awaited) before any caller relies on
    /// the write having happened — see <see cref="HwndJournalWriter"/>'s own remarks for why this is
    /// exactly the property the write-ahead ordering contract depends on.
    /// </summary>
    Task WriteAsync(JournalDocument document, CancellationToken cancellationToken = default);
}
