using Bastion.Core;
using Bastion.Win32;
using Microsoft.Extensions.Time.Testing;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1 unit tests (docs/engineering/testing.md §3) for <see cref="WindowRegistry"/>'s own
/// bookkeeping — admission, idempotency, HWND-recycling eviction, and purge — with every Win32
/// dependency faked (<see cref="FakeWindowProcessIdReader"/>, <see cref="FakeWindowManageabilityInfoReader"/>,
/// <see cref="FakeWindowIdentityResolver"/>). No real HWND/process anywhere in this file.
/// </summary>
public sealed class WindowRegistryTests
{
    private static readonly HWND s_someWindow = new((IntPtr)1);
    private const uint SomePid = 100;

    private readonly FakeWindowProcessIdReader _pidReader = new();
    private readonly FakeWindowManageabilityInfoReader _infoReader = new();
    private readonly FakeWindowIdentityResolver _identityResolver = new();
    private readonly WindowIdMinter _idMinter = new();
    private readonly FakeTimeProvider _timeProvider = new();

    private WindowRegistry CreateRegistry() =>
        new(_pidReader, _infoReader, _identityResolver, WindowClassBlocklist.Default, _idMinter, _timeProvider);

    [Fact]
    public async Task NewManageableWindowIsAdmittedWithAMintedId()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();

        WindowId? windowId = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.NotNull(windowId);
        Assert.Equal(1, _identityResolver.CallCount);
    }

    [Fact]
    public async Task NotManageableWindowIsNotAdmittedAndNeverResolvesIdentity()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        _infoReader.SetInfo(s_someWindow, _infoReader.Default with { IsVisible = false });
        WindowRegistry registry = CreateRegistry();

        WindowId? windowId = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Null(windowId);
        Assert.Equal(0, _identityResolver.CallCount);
        Assert.Null(registry.TryGetEntry(s_someWindow));
    }

    [Fact]
    public async Task VanishedWindowIsNotAdmitted()
    {
        // No pid ever set for s_someWindow — FakeWindowProcessIdReader.TryReadProcessId returns
        // null, matching a window that no longer exists by the time this call runs.
        WindowRegistry registry = CreateRegistry();

        WindowId? windowId = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Null(windowId);
        Assert.Equal(0, _identityResolver.CallCount);
    }

    [Fact]
    public async Task AlreadyRegisteredWindowReturnsTheSameIdWithoutResolvingIdentityAgain()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();

        WindowId? first = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);
        WindowId? second = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(1, _identityResolver.CallCount);
    }

    [Fact]
    public async Task AlreadyRegisteredWindowIsNeverEvictedByALaterFailingFilterReRun()
    {
        // DESIGN.md §3.3: "any nonzero cloak value -> keep tracked, never tile, never forget."
        // Simulated here as the window later reading not-visible on a NAMECHANGE re-evaluation —
        // any filter failure has the same "never forget" consequence for an existing entry.
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        WindowId? admitted = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        _infoReader.SetInfo(s_someWindow, _infoReader.Default with { IsCloaked = true });
        WindowId? reEvaluated = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Equal(admitted, reEvaluated);
        Assert.NotNull(registry.TryGetEntry(s_someWindow));
    }

    [Fact]
    public async Task RecycledHwndMintsAFreshIdAndReplacesTheStaleEntry()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        WindowId? original = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        // The same HWND value is now a different live window (its EVENT_OBJECT_DESTROY was
        // missed) — the live PID no longer matches the recorded one.
        const uint recycledPid = 200;
        _pidReader.SetPid(s_someWindow, recycledPid);
        WindowId? afterRecycling = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.NotEqual(original, afterRecycling);
        Assert.Equal(recycledPid, registry.TryGetEntry(s_someWindow)?.Pid);
        Assert.Equal(2, _identityResolver.CallCount);
    }

    [Fact]
    public async Task PurgeRemovesTheEntryAndANewAdmissionMintsAFreshId()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        WindowId? original = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        registry.Purge(s_someWindow);
        Assert.Null(registry.TryGetEntry(s_someWindow));

        WindowId? readmitted = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.NotEqual(original, readmitted);
    }

    [Fact]
    public void TryGetEntryReturnsNullForAnUnknownWindow()
    {
        WindowRegistry registry = CreateRegistry();

        Assert.Null(registry.TryGetEntry(s_someWindow));
    }

    // --- TryGetHwnd (GitHub issue #5's integration gap: WindowId -> HWND resolution) -----------

    [Fact]
    public async Task TryGetHwndResolvesTheHwndForAnAdmittedWindow()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        WindowId? windowId = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        bool resolved = registry.TryGetHwnd(windowId!.Value, out HWND hwnd);

        Assert.True(resolved);
        Assert.Equal(s_someWindow, hwnd);
    }

    [Fact]
    public void TryGetHwndReturnsFalseForAWindowIdThatWasNeverAdmitted()
    {
        WindowRegistry registry = CreateRegistry();

        bool resolved = registry.TryGetHwnd(WindowId.FromOpaqueValue(999), out HWND hwnd);

        Assert.False(resolved);
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public async Task TryGetHwndReturnsFalseAfterPurge()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        WindowId? windowId = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        registry.Purge(s_someWindow);

        Assert.False(registry.TryGetHwnd(windowId!.Value, out _));
    }

    [Fact]
    public async Task TryGetHwndNoLongerResolvesTheOriginalWindowIdAfterHwndRecycling()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        WindowId? original = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        const uint recycledPid = 200;
        _pidReader.SetPid(s_someWindow, recycledPid);
        WindowId? afterRecycling = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        // The reverse index must be evicted alongside the stale forward entry (WindowRegistry's own
        // remarks on HWND-recycling correction) -- otherwise TryGetHwnd would keep resolving the
        // original, now-meaningless WindowId to this HWND, which now identifies a different window.
        Assert.False(registry.TryGetHwnd(original!.Value, out _));
        Assert.True(registry.TryGetHwnd(afterRecycling!.Value, out HWND hwnd));
        Assert.Equal(s_someWindow, hwnd);
    }

    [Fact]
    public async Task EntryRecordsTheInjectedTimeProviderAsFirstSeenTimestamp()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        WindowRegistry registry = CreateRegistry();
        DateTimeOffset expected = _timeProvider.GetUtcNow();

        await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Equal(expected, registry.TryGetEntry(s_someWindow)?.FirstSeenUtc);
    }

    [Fact]
    public async Task EntryRecordsTheResolvedIdentity()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        var identity = new WindowIdentity(WindowIdentityKind.Aumid, "Contoso.App_abc123!App");
        _identityResolver.Result = identity;
        WindowRegistry registry = CreateRegistry();

        await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Equal(identity, registry.TryGetEntry(s_someWindow)?.Identity);
    }

    [Fact]
    public async Task ConcurrentAdmissionOfTheSameWindowMintsExactlyOneId()
    {
        _pidReader.SetPid(s_someWindow, SomePid);
        var gate = new TaskCompletionSource();
        _identityResolver.Gate = gate.Task;
        WindowRegistry registry = CreateRegistry();

        // Both calls reach the (gated) identity-resolution await before either can mint —
        // TryFindExisting for a brand-new HWND is synchronous and always misses, so this is a
        // deterministic race window, not a timing-dependent guess.
        Task<WindowId?> first = registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);
        Task<WindowId?> second = registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        gate.SetResult();
        WindowId?[] results = await Task.WhenAll(first, second);

        Assert.NotNull(results[0]);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(2, _identityResolver.CallCount);
    }

    [Fact]
    public async Task StaleAdmissionIsAbortedRatherThanClobberingARecycledHwndsNewerEntry()
    {
        // Codex review finding on PR #40: a slow admission for a since-recycled HWND must not
        // commit a ghost entry for a window that no longer exists, even though nothing purged the
        // registry in between (its DESTROY may already have been handled with no entry yet to
        // purge, since this call had not committed anything at that point either).
        _pidReader.SetPid(s_someWindow, SomePid);
        var gate = new TaskCompletionSource();
        _identityResolver.Gate = gate.Task;
        WindowRegistry registry = CreateRegistry();

        Task<WindowId?> stale = registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        // The HWND is recycled to a different live window while the call above awaits identity
        // resolution.
        const uint recycledPid = 999;
        _pidReader.SetPid(s_someWindow, recycledPid);
        gate.SetResult();

        // CA2007: unlike this file's other awaits (each directly on a fresh call expression),
        // this one awaits a Task stored earlier and used concurrently with the code in between —
        // ConfigureAwait(true) keeps xUnit's synchronization-context-preserving default explicit
        // per this repo's test-code convention (never ConfigureAwait(false) in test code).
        WindowId? result = await stale.ConfigureAwait(true);

        Assert.Null(result);
        Assert.Null(registry.TryGetEntry(s_someWindow));
    }

    [Fact]
    public async Task ProvisionalUwpIdentityIsRetriedAndUpgradedOnLaterAdmission()
    {
        // Codex review finding on PR #40: DESIGN.md §3.3's UWP-attribution failure "retried on
        // later SHOW/NAMECHANGE/FOREGROUND" must actually happen, not freeze the fallback forever.
        _pidReader.SetPid(s_someWindow, SomePid);
        _infoReader.SetInfo(
            s_someWindow,
            _infoReader.Default with { ClassName = ApplicationFrameUwpAttributionProvider.ApplicationFrameWindowClassName });
        var provisional = new WindowIdentity(WindowIdentityKind.ExePath, @"C:\Windows\System32\ApplicationFrameHost.exe");
        _identityResolver.Result = provisional;
        WindowRegistry registry = CreateRegistry();
        WindowId? admitted = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);
        Assert.Equal(provisional, registry.TryGetEntry(s_someWindow)?.Identity);

        // The CoreWindow child becomes attributable by the time of a later re-evaluation.
        var upgraded = new WindowIdentity(WindowIdentityKind.Aumid, "Contoso.App_abc123!App");
        _identityResolver.Result = upgraded;
        WindowId? reEvaluated = await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Equal(admitted, reEvaluated);
        Assert.Equal(upgraded, registry.TryGetEntry(s_someWindow)?.Identity);
        Assert.Equal(2, _identityResolver.CallCount);
    }

    [Fact]
    public async Task ProvisionalIdentityOnAnOrdinaryWindowIsNeverRetried()
    {
        // Retrying is scoped to ApplicationFrameWindow specifically (see WindowRegistry's own
        // remarks) -- an ordinary desktop app's non-AUMID identity is not COM/UWP-timing-dependent
        // and will never change, so retrying it on every admission call would be pure waste.
        _pidReader.SetPid(s_someWindow, SomePid);
        _identityResolver.Result = WindowIdentity.Unknown;
        WindowRegistry registry = CreateRegistry();
        await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        await registry.TryAdmitAsync(s_someWindow, TestContext.Current.CancellationToken);

        Assert.Equal(1, _identityResolver.CallCount);
    }
}
