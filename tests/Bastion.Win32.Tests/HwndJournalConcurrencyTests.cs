using System.Collections.Immutable;
using Bastion.Core;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Codex review finding on this PR: <see cref="HwndJournalWriter.RecordThenActAsync"/>'s whole
/// "journal, then hide" sequence and <see cref="HwndJournalRestorer.RestoreAllAsync"/>'s whole
/// "read, restore, write" pass must serialize against each other via the shared cross-process
/// journal lock — otherwise a concurrent restore could read and clear a just-written entry
/// <em>before</em> the writer's own hide action has actually run, stranding the window hidden with
/// no journal row left to recover it.
/// </summary>
public sealed class HwndJournalConcurrencyTests
{
    [Fact]
    public async Task RestoreAllAsyncBlocksUntilAConcurrentRecordThenActAsyncsWholeOperationCompletes()
    {
        var store = new FakeHwndJournalStore();
        using var sharedLock = new FakeHwndJournalLock();
        var writer = new HwndJournalWriter(store, sharedLock);
        var hwnd = new HWND(0x6000);
        var entry = new JournalEntry(
            (long)(IntPtr)hwnd,
            42,
            WorkspaceKey.Default,
            new JournalWindowPlacement(JournalShowCommand.Normal, 0, 0, 0, 0, new Rect(0, 0, 800, 600)),
            WindowIdentity.Unknown,
            JournalCornerPreference.Unset,
            DateTimeOffset.UnixEpoch);
        var hideGate = new TaskCompletionSource();

        // Start the writer's whole "journal, then hide" operation, but the hide action itself
        // blocks on hideGate -- simulating the daemon mid-way through hiding a window: the entry is
        // already durably written, but the actual hide has not happened yet.
        Task writerTask = writer.RecordThenActAsync(entry, _ => hideGate.Task, TestContext.Current.CancellationToken);

        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(hwnd, 42);
        var placementSystem = new FakeJournalPlacementSystem();
        var restorer = new HwndJournalRestorer(store, placementSystem, pidReader, sharedLock);

        // Fire off a concurrent restore -- it must not be able to proceed while the writer still
        // holds the lock (mid hide-action).
        Task<ImmutableArray<JournalRestoreOutcome>> restorerTask =
            restorer.RestoreAllAsync(TestContext.Current.CancellationToken);

        // A real (short) delay to let both tasks actually reach their awaited continuations, without
        // advancing any fake clock -- matching PlacementExecutorTests' own established pattern for
        // proving a task is genuinely still pending, not merely "hasn't been scheduled yet."
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(restorerTask.IsCompleted);
        Assert.Empty(placementSystem.AppliedPlacements);

        // Release the hide action -- the writer's whole operation completes and releases the lock,
        // which lets the blocked restorer finally proceed.
        hideGate.SetResult();
        await writerTask.ConfigureAwait(true);
        ImmutableArray<JournalRestoreOutcome> outcomes = await restorerTask.ConfigureAwait(true);

        // The window really was hidden by the time the restorer got its turn -- it correctly found
        // and restored the entry, rather than racing ahead of the hide.
        JournalRestoreOutcome outcome = Assert.Single(outcomes);
        Assert.Equal(JournalRestoreOutcomeKind.Restored, outcome.Kind);
        Assert.Single(placementSystem.AppliedPlacements);
    }
}
