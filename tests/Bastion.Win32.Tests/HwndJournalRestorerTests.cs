using System.Collections.Immutable;
using Bastion.Core;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// GitHub issue #8's <see cref="HwndJournalRestorer"/>: HWND/PID revalidation defensiveness
/// (DESIGN.md §9's HWND-recycling honesty note) and the dirty-flag/journal-entry-retention rules
/// on partial failure.
/// </summary>
public sealed class HwndJournalRestorerTests
{
    private static readonly HWND s_hwnd = new(0x1000);

    [Fact]
    public async Task EmptyJournalProducesNoOutcomesAndNeverWritesTheStore()
    {
        var store = new FakeHwndJournalStore();
        var restorer = new HwndJournalRestorer(store, new FakeJournalPlacementSystem(), new FakeWindowProcessIdReader());

        ImmutableArray<JournalRestoreOutcome> outcomes = await restorer.RestoreAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(outcomes);
        Assert.Empty(store.WrittenDocuments);
    }

    /// <summary>The window no longer exists at all -- a routine, non-exceptional "it was already closed" case.</summary>
    [Fact]
    public async Task WindowNoLongerExistingIsSkippedAndRemovedFromTheJournal()
    {
        var store = new FakeHwndJournalStore();
        JournalEntry entry = CreateEntry(pid: 42);
        await Seed(store, entry);
        var pidReader = new FakeWindowProcessIdReader(); // no SetPid -- simulates a vanished window
        var restorer = new HwndJournalRestorer(store, new FakeJournalPlacementSystem(), pidReader);

        JournalRestoreOutcome outcome = Assert.Single(await restorer.RestoreAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(JournalRestoreOutcomeKind.SkippedWindowGone, outcome.Kind);
        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Empty(written.Entries);
        Assert.False(written.Dirty);
    }

    /// <summary>
    /// DESIGN.md §9's HWND-recycling honesty note made concrete: the HWND value now resolves to a
    /// live window, but one owned by a different process -- the journaled window is gone and this
    /// numeric value has been recycled to something unrelated. Must never call SetWindowPlacement
    /// on it.
    /// </summary>
    [Fact]
    public async Task RecycledHwndOwnedByADifferentProcessIsSkippedNeverPlaced()
    {
        var store = new FakeHwndJournalStore();
        JournalEntry entry = CreateEntry(pid: 42);
        await Seed(store, entry);
        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(s_hwnd, 999); // a different, unrelated process now owns this HWND value
        var placementSystem = new FakeJournalPlacementSystem();
        var restorer = new HwndJournalRestorer(store, placementSystem, pidReader);

        JournalRestoreOutcome outcome = Assert.Single(await restorer.RestoreAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(JournalRestoreOutcomeKind.SkippedHwndRecycled, outcome.Kind);
        Assert.Empty(placementSystem.AppliedPlacements);
        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Empty(written.Entries);
        Assert.False(written.Dirty);
    }

    [Fact]
    public async Task MatchingPidIsRestoredViaSetWindowPlacementAndRemovedFromTheJournal()
    {
        var store = new FakeHwndJournalStore();
        JournalEntry entry = CreateEntry(pid: 42);
        await Seed(store, entry);
        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(s_hwnd, 42); // same process that owned it at journal-write time
        var placementSystem = new FakeJournalPlacementSystem();
        var restorer = new HwndJournalRestorer(store, placementSystem, pidReader);

        JournalRestoreOutcome outcome = Assert.Single(await restorer.RestoreAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(JournalRestoreOutcomeKind.Restored, outcome.Kind);
        (HWND hwnd, JournalWindowPlacement placement) = Assert.Single(placementSystem.AppliedPlacements);
        Assert.Equal(s_hwnd, hwnd);
        Assert.Equal(entry.PreManagementPlacement, placement);
        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Empty(written.Entries);
        Assert.False(written.Dirty);
    }

    /// <summary>A failed SetWindowPlacement (e.g. UIPI-blocked elevated window) is retained for a future retry, keeping the journal dirty.</summary>
    [Fact]
    public async Task FailedSetWindowPlacementIsRetainedInTheJournalAndKeepsItDirty()
    {
        var store = new FakeHwndJournalStore();
        JournalEntry entry = CreateEntry(pid: 42);
        await Seed(store, entry);
        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(s_hwnd, 42);
        var placementSystem = new FakeJournalPlacementSystem();
        placementSystem.SetApplyResult(s_hwnd, PlacementCallResult.Fail(WIN32_ERROR.ERROR_ACCESS_DENIED));
        var restorer = new HwndJournalRestorer(store, placementSystem, pidReader);

        JournalRestoreOutcome outcome = Assert.Single(await restorer.RestoreAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(JournalRestoreOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(WIN32_ERROR.ERROR_ACCESS_DENIED, outcome.ErrorCode);
        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Equal([entry], written.Entries);
        Assert.True(written.Dirty);
    }

    /// <summary>A mixed pass: one entry restores cleanly, one fails -- only the failed one survives, and the flag reflects that.</summary>
    [Fact]
    public async Task MixedPassKeepsOnlyTheFailedEntriesAndDirtyReflectsWhetherAnyRemain()
    {
        var store = new FakeHwndJournalStore();
        var okHwnd = new HWND(0x2000);
        var failHwnd = new HWND(0x3000);
        JournalEntry okEntry = CreateEntry(hwndValue: (long)(IntPtr)okHwnd, pid: 10);
        JournalEntry failEntry = CreateEntry(hwndValue: (long)(IntPtr)failHwnd, pid: 20);
        await store.WriteAsync(new JournalDocument { Dirty = true, Entries = [okEntry, failEntry] }, TestContext.Current.CancellationToken);

        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(okHwnd, 10);
        pidReader.SetPid(failHwnd, 20);
        var placementSystem = new FakeJournalPlacementSystem();
        placementSystem.SetApplyResult(failHwnd, PlacementCallResult.Fail(WIN32_ERROR.ERROR_ACCESS_DENIED));
        var restorer = new HwndJournalRestorer(store, placementSystem, pidReader);

        ImmutableArray<JournalRestoreOutcome> outcomes = await restorer.RestoreAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, outcomes.Length);
        Assert.Equal(JournalRestoreOutcomeKind.Restored, outcomes[0].Kind);
        Assert.Equal(JournalRestoreOutcomeKind.Failed, outcomes[1].Kind);
        JournalDocument written = store.WrittenDocuments[^1];
        Assert.Equal([failEntry], written.Entries);
        Assert.True(written.Dirty);
    }

    private static async Task Seed(FakeHwndJournalStore store, JournalEntry entry) =>
        await store.WriteAsync(new JournalDocument { Dirty = true, Entries = [entry] }, TestContext.Current.CancellationToken).ConfigureAwait(true);

    private static JournalEntry CreateEntry(uint pid, long? hwndValue = null) => new(
        hwndValue ?? (long)(IntPtr)s_hwnd,
        pid,
        WorkspaceKey.Default,
        new JournalWindowPlacement(JournalShowCommand.Normal, 0, 0, 0, 0, new Rect(0, 0, 800, 600)),
        WindowIdentity.Unknown,
        JournalCornerPreference.Unset,
        DateTimeOffset.UnixEpoch);
}
