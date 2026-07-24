using Bastion.Core;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// The acceptance-criteria-mandated write-before-hide ordering test for GitHub issue #8: "A test
/// verifies the write-before-hide ordering (e.g. via a fake adapter that would fail the test if
/// hide is observed before the journal write)." <see cref="HwndJournalWriter"/> has no real
/// production caller yet (Bastion-owned workspaces are GitHub issue #15) — these tests exercise the
/// ordering <em>contract</em> directly against <see cref="FakeHwndJournalStore"/>.
/// </summary>
public sealed class HwndJournalWriterTests
{
    [Fact]
    public async Task RecordThenActAsyncRejectsANullEntry()
    {
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(new FakeHwndJournalStore(), journalLock);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => writer.RecordThenActAsync(null!, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordThenActAsyncRejectsANullAction()
    {
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(new FakeHwndJournalStore(), journalLock);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => writer.RecordThenActAsync(CreateEntry(), null!, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The direct proof of write-before-act ordering: while the journal write is still pending
    /// (gated open), the action must not have run. If <see cref="HwndJournalWriter.RecordThenActAsync"/>
    /// ever fire-and-forgot the store write, or invoked the action before awaiting it, this
    /// assertion would fail — exactly the acceptance criteria's "a fake adapter that would fail the
    /// test if hide is observed before the journal write."
    /// </summary>
    [Fact]
    public async Task ActionIsNeverInvokedWhileTheJournalWriteIsStillPending()
    {
        var store = new FakeHwndJournalStore();
        var writeGate = new TaskCompletionSource();
        store.GateNextWrite(writeGate);
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, journalLock);
        bool actionInvoked = false;

        Task recordTask = writer.RecordThenActAsync(
            CreateEntry(),
            _ =>
            {
                actionInvoked = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        // The write is deliberately still pending -- the action must not have run yet, and nothing
        // must have been durably written.
        Assert.False(actionInvoked);
        Assert.Empty(store.WrittenDocuments);

        writeGate.SetResult();
        await recordTask.ConfigureAwait(true);

        Assert.True(actionInvoked);
        Assert.Single(store.WrittenDocuments);
    }

    /// <summary>Documents the exact ordering via a shared log, complementing the gated-pending proof above.</summary>
    [Fact]
    public async Task JournalWriteIsObservedBeforeTheActionInAnOrderedLog()
    {
        var store = new FakeHwndJournalStore();
        var order = new List<string>();
        store.OnWriteCompleted = _ => order.Add("journal-written");
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, journalLock);

        await writer.RecordThenActAsync(
            CreateEntry(),
            _ =>
            {
                order.Add("hide-called");
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["journal-written", "hide-called"], order);
    }

    [Fact]
    public async Task ActionIsNeverInvokedWhenTheJournalWriteFails()
    {
        var store = new ThrowingWriteStore();
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, journalLock);
        bool actionInvoked = false;

        await Assert.ThrowsAsync<IOException>(() => writer.RecordThenActAsync(
            CreateEntry(),
            _ =>
            {
                actionInvoked = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken));

        Assert.False(actionInvoked);
    }

    [Fact]
    public async Task RecordThenActAsyncMarksTheJournalDirtyAndAppendsTheEntry()
    {
        var store = new FakeHwndJournalStore();
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, journalLock);
        JournalEntry entry = CreateEntry();

        await writer.RecordThenActAsync(entry, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        JournalDocument written = Assert.Single(store.WrittenDocuments);
        Assert.True(written.Dirty);
        Assert.Equal([entry], written.Entries);
    }

    [Fact]
    public async Task RecordThenActAsyncAppendsToWhateverTheStoreAlreadyHad()
    {
        var store = new FakeHwndJournalStore();
        JournalEntry existing = CreateEntry(hwndValue: 111, pid: 222);
        await store.WriteAsync(new JournalDocument { Dirty = true, Entries = [existing] }, TestContext.Current.CancellationToken);
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, journalLock);
        JournalEntry appended = CreateEntry(hwndValue: 333, pid: 444);

        await writer.RecordThenActAsync(appended, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Equal([existing, appended], written.Entries);
    }

    /// <summary>
    /// Codex review finding on this PR: a window hidden, restored, and hidden again in the same
    /// session must not have its journal entry overwritten by the second hide's placement (which is
    /// itself Bastion-managed, not the window's true pre-management state) -- the entry already on
    /// disk from the first hide is the one crash recovery must apply.
    /// </summary>
    [Fact]
    public async Task RecordThenActAsyncPreservesTheFirstEntryWhenTheSameWindowIsHiddenAgain()
    {
        var store = new FakeHwndJournalStore();
        using var journalLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, journalLock);
        JournalEntry firstHideEntry = CreateEntry(hwndValue: 555, pid: 666);

        await writer.RecordThenActAsync(firstHideEntry, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        // Same window (same HwndValue + ProcessId), but a different captured placement -- as if it
        // had been restored into a Bastion tile and was now being hidden a second time.
        JournalEntry secondHideEntry = firstHideEntry with
        {
            PreManagementPlacement = new JournalWindowPlacement(JournalShowCommand.Maximized, 0, 0, 0, 0, new Rect(0, 0, 1920, 1080)),
        };
        bool secondActionInvoked = false;

        await writer.RecordThenActAsync(
            secondHideEntry,
            _ =>
            {
                secondActionInvoked = true;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        // The hide action still runs both times -- only the journal write is skipped the second time.
        Assert.True(secondActionInvoked);
        JournalDocument finalDocument = store.WrittenDocuments[^1];
        Assert.Equal([firstHideEntry], finalDocument.Entries);

        // The store's own WriteAsync was only actually invoked once (the second RecordThenActAsync
        // call found an existing entry and skipped writing entirely).
        Assert.Single(store.WrittenDocuments);
    }

    private static JournalEntry CreateEntry(long hwndValue = 1, uint pid = 100) => new(
        hwndValue,
        pid,
        WorkspaceKey.Default,
        new JournalWindowPlacement(JournalShowCommand.Normal, 0, 0, 0, 0, new Rect(0, 0, 800, 600)),
        WindowIdentity.Unknown,
        JournalCornerPreference.Unset,
        DateTimeOffset.UnixEpoch);

    /// <summary>An <see cref="IHwndJournalStore"/> whose <see cref="WriteAsync"/> always throws — proves a failed write never lets the action run.</summary>
    private sealed class ThrowingWriteStore : IHwndJournalStore
    {
        public Task<JournalDocument> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(JournalDocument.Empty);

        public Task WriteAsync(JournalDocument document, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated write failure.");
    }
}
