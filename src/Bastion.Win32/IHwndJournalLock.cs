namespace Bastion.Win32;

/// <summary>
/// Cross-process mutual exclusion for the write-ahead journal's critical sections (GitHub issue
/// #8, Codex review finding on this PR): <see cref="HwndJournalWriter.RecordThenActAsync"/>'s whole
/// "journal, then hide" sequence and <see cref="HwndJournalRestorer.RestoreAllAsync"/>'s whole
/// "read, restore, write" pass must never interleave across <c>bastiond</c> and <c>bastionc</c> —
/// otherwise a concurrent <c>restore-windows</c> invocation could read and clear a just-written
/// entry <em>before</em> the daemon's own hide call for that window has actually run, leaving the
/// window hidden with no journal row to recover it if the daemon then crashes. Matches
/// <c>docs/engineering/testing.md</c> §5's Tier-2 seam shape: <see cref="HwndJournalLock"/> is the
/// real, named-<see cref="Semaphore"/>-backed implementation; <c>Bastion.Win32.Tests</c>'s
/// <c>FakeHwndJournalLock</c> is the in-process fake used to prove the ordering in tests.
/// </summary>
internal interface IHwndJournalLock
{
    /// <summary>
    /// Acquires the lock, awaiting if another process (or another caller in this one) currently
    /// holds it. Dispose the returned <see cref="IDisposable"/> to release it — wrap the whole
    /// critical section in a <see langword="using"/> block.
    /// </summary>
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
