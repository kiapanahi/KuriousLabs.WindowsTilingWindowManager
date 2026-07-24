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
        var writer = new HwndJournalWriter(new FakeHwndJournalStore());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => writer.RecordThenActAsync(null!, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecordThenActAsyncRejectsANullAction()
    {
        var writer = new HwndJournalWriter(new FakeHwndJournalStore());
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
        var writer = new HwndJournalWriter(store);
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
        var writer = new HwndJournalWriter(store);

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
        var writer = new HwndJournalWriter(store);
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
        var writer = new HwndJournalWriter(store);
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
        var writer = new HwndJournalWriter(store);
        JournalEntry appended = CreateEntry(hwndValue: 333, pid: 444);

        await writer.RecordThenActAsync(appended, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Equal([existing, appended], written.Entries);
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
