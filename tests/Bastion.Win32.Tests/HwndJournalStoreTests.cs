using System.Collections.Immutable;
using Bastion.Core;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Real, file-system-backed tests for <see cref="HwndJournalStore"/> (GitHub issue #8): missing-file
/// semantics, the source-gen JSON round trip through <see cref="JournalJsonContext"/>, the
/// temp-file-plus-move write path leaving no stray temp files behind, and full overwrite (never a
/// merge) on a second write. Each test gets its own isolated temp directory, deleted afterward —
/// never touches the real <c>%LOCALAPPDATA%\Bastion</c> path <see cref="HwndJournalStore.DefaultJournalFilePath"/> names.
/// </summary>
public sealed class HwndJournalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bastion-journal-tests-{Guid.NewGuid():N}");
    private readonly string _journalPath;

    public HwndJournalStoreTests()
    {
        _journalPath = Path.Combine(_directory, "hwnd-journal.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsyncReturnsEmptyWhenNoJournalFileExists()
    {
        var store = new HwndJournalStore(_journalPath);

        JournalDocument document = await store.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JournalDocument.Empty, document);
    }

    [Fact]
    public async Task WriteThenReadRoundTripsEveryFieldIncludingNestedTypes()
    {
        var store = new HwndJournalStore(_journalPath);
        var entry = new JournalEntry(
            HwndValue: 0x00007FF6_12345678,
            ProcessId: 4242,
            Workspace: WorkspaceKey.Default,
            PreManagementPlacement: new JournalWindowPlacement(
                JournalShowCommand.Maximized,
                MinPositionX: -1,
                MinPositionY: -1,
                MaxPositionX: 10,
                MaxPositionY: 20,
                NormalPosition: new Rect(100, 200, 900, 700)),
            Identity: new WindowIdentity(WindowIdentityKind.Aumid, "Contoso.App_9zz4h110yvjzy!App"),
            CornerPreference: JournalCornerPreference.Unset,
            JournaledAtUtc: new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero));
        var original = new JournalDocument { Dirty = true, Entries = [entry] };

        await store.WriteAsync(original, TestContext.Current.CancellationToken);
        JournalDocument roundTripped = await store.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public async Task WriteAsyncLeavesOnlyTheFinalFileBehindNoStrayTempFiles()
    {
        var store = new HwndJournalStore(_journalPath);

        await store.WriteAsync(JournalDocument.Empty, TestContext.Current.CancellationToken);

        string[] filesInDirectory = Directory.GetFiles(_directory);
        Assert.Equal([_journalPath], filesInDirectory);
    }

    [Fact]
    public async Task SecondWriteFullyReplacesTheFirstRatherThanMerging()
    {
        var store = new HwndJournalStore(_journalPath);
        JournalEntry firstEntry = CreateEntry(1);
        JournalEntry secondEntry = CreateEntry(2);

        await store.WriteAsync(new JournalDocument { Dirty = true, Entries = [firstEntry] }, TestContext.Current.CancellationToken);
        await store.WriteAsync(new JournalDocument { Dirty = false, Entries = [secondEntry] }, TestContext.Current.CancellationToken);

        JournalDocument final = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal([secondEntry], final.Entries);
        Assert.False(final.Dirty);
    }

    [Fact]
    public async Task WrittenJsonUsesCamelCasePropertyNames()
    {
        // Confirms JournalJsonContext's [JsonSourceGenerationOptions(PropertyNamingPolicy =
        // JsonKnownNamingPolicy.CamelCase)] actually reaches the file on disk, not just the
        // in-memory round trip above (which would still pass even if both sides silently agreed on
        // some other naming policy).
        var store = new HwndJournalStore(_journalPath);
        await store.WriteAsync(new JournalDocument { Dirty = true, Entries = ImmutableArray<JournalEntry>.Empty }, TestContext.Current.CancellationToken);

        string json = await File.ReadAllTextAsync(_journalPath, TestContext.Current.CancellationToken);

        Assert.Contains("\"dirty\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"entries\": []", json, StringComparison.Ordinal);
    }

    private static JournalEntry CreateEntry(long hwndValue) => new(
        hwndValue,
        ProcessId: 1,
        WorkspaceKey.Default,
        new JournalWindowPlacement(JournalShowCommand.Normal, 0, 0, 0, 0, new Rect(0, 0, 800, 600)),
        WindowIdentity.Unknown,
        JournalCornerPreference.Unset,
        DateTimeOffset.UnixEpoch);
}
