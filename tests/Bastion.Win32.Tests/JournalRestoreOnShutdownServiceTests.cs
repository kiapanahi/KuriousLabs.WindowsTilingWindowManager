using Bastion.Core;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// GitHub issue #8's clean-daemon-shutdown hook. Not yet registered anywhere (issue #10 wires the
/// real composition root) — these tests exercise the <see cref="IHostedService"/> contract directly.
/// </summary>
public sealed class JournalRestoreOnShutdownServiceTests
{
    [Fact]
    public async Task StartAsyncCompletesImmediatelyWithoutTouchingTheJournal()
    {
        var store = new FakeHwndJournalStore();
        var restorer = new HwndJournalRestorer(store, new FakeJournalPlacementSystem(), new FakeWindowProcessIdReader());
        var service = new JournalRestoreOnShutdownService(restorer);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.WrittenDocuments);
    }

    [Fact]
    public async Task StopAsyncForceRestoresTheJournal()
    {
        var store = new FakeHwndJournalStore();
        var hwnd = new HWND(0x5000);
        var entry = new JournalEntry(
            (long)(IntPtr)hwnd,
            42,
            WorkspaceKey.Default,
            new JournalWindowPlacement(JournalShowCommand.Normal, 0, 0, 0, 0, new Rect(0, 0, 800, 600)),
            WindowIdentity.Unknown,
            JournalCornerPreference.Unset,
            DateTimeOffset.UnixEpoch);
        await store.WriteAsync(new JournalDocument { Dirty = true, Entries = [entry] }, TestContext.Current.CancellationToken);
        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(hwnd, 42);
        var placementSystem = new FakeJournalPlacementSystem();
        var restorer = new HwndJournalRestorer(store, placementSystem, pidReader);
        var service = new JournalRestoreOnShutdownService(restorer);

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(placementSystem.AppliedPlacements);
        JournalDocument finalDocument = store.WrittenDocuments[^1];
        Assert.Empty(finalDocument.Entries);
        Assert.False(finalDocument.Dirty);
    }

    /// <summary>A diagnostic-logging concern (an unreadable/corrupt journal) must never block host shutdown.</summary>
    [Fact]
    public async Task StopAsyncSwallowsAFailedRestoreRatherThanPropagating()
    {
        var restorer = new HwndJournalRestorer(new ThrowingReadStore(), new FakeJournalPlacementSystem(), new FakeWindowProcessIdReader());
        var service = new JournalRestoreOnShutdownService(restorer);

        // Must not throw -- a broken journal is not allowed to prevent bastiond from exiting.
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class ThrowingReadStore : IHwndJournalStore
    {
        public Task<JournalDocument> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new JsonException("Simulated corrupt journal.");

        public Task WriteAsync(JournalDocument document, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should never be reached in this test.");
    }
}
