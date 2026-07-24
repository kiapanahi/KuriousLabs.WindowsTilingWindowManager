using System.Collections.Immutable;
using System.Threading.Channels;
using Bastion.Core;
using Bastion.Win32;
using Microsoft.Extensions.Time.Testing;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="ReconcilerIntentPump"/> — the bridge from the Coalescer's coalesced-intent
/// stream to the Reconciler, DESIGN.md §3.4's "(1) coalesced intents" convergence trigger.
/// </summary>
public sealed class ReconcilerIntentPumpTests
{
    private static WindowRegistry CreateRegistry(
        FakeWindowProcessIdReader pidReader, FakeWindowManageabilityInfoReader infoReader, FakeWindowIdentityResolver identityResolver, TimeProvider time) =>
        new(pidReader, infoReader, identityResolver, WindowClassBlocklist.Default, new WindowIdMinter(), time);

    private static PlacementExecutor CreatePlacementExecutor(IPlacementSystem system, TimeProvider time) =>
        new(system, new FakeReconcileNowSignal(), time);

    [Fact]
    public async Task ACoalescedIntentTriggersAConvergencePassWithoutWaitingForTheHeartbeat()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        WindowRegistry registry = CreateRegistry(new FakeWindowProcessIdReader(), new FakeWindowManageabilityInfoReader(), new FakeWindowIdentityResolver(), time);
        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, registry, reconciler, CreatePlacementExecutor(new FakePlacementSystem(), time));
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, windowSystem.ReadAllCallCount);

            // Convergence-wise the pump never inspects intent kind/payload (see its own remarks) --
            // any of the six typed intents is equally a "wake the loop up" signal.
            Assert.True(intents.Writer.TryWrite(new WindowAppeared(Hwnd: 0x1000)));

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1, "Expected the coalesced intent to drive a Reconciler convergence pass.");
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DifferentIntentKindsAllDriveTheSameConvergenceTrigger()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        WindowRegistry registry = CreateRegistry(new FakeWindowProcessIdReader(), new FakeWindowManageabilityInfoReader(), new FakeWindowIdentityResolver(), time);
        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, registry, reconciler, CreatePlacementExecutor(new FakePlacementSystem(), time));
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(intents.Writer.TryWrite(new WindowVanished(Hwnd: 0x2000)));

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task WindowVanishedIntentPurgesTheRegistrySoARecycledHwndInTheSameProcessGetsAFreshIdentity()
    {
        // Codex review finding on this PR: without this purge, a destroyed window's stale
        // WindowRegistry entry survives indefinitely. If the OS later recycles its HWND to a
        // genuinely different window in the SAME process, WindowRegistry.TryAdmitAsync's own
        // PID-match "already registered" check (its own remarks) would wrongly hand the new window
        // the old, stale WindowId -- exactly the DESIGN.md §3.3/§9 HWND-recycling risk "entries are
        // purged only on EVENT_OBJECT_DESTROY" exists to prevent.
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);

        var pidReader = new FakeWindowProcessIdReader();
        WindowRegistry registry = CreateRegistry(pidReader, new FakeWindowManageabilityInfoReader(), new FakeWindowIdentityResolver(), time);
        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, registry, reconciler, CreatePlacementExecutor(new FakePlacementSystem(), time));
        using var cts = new CancellationTokenSource();

        var hwnd = new HWND((nint)0x9000);
        const uint pid = 42;
        pidReader.SetPid(hwnd, pid);
        WindowId? original = await registry.TryAdmitAsync(hwnd, TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(intents.Writer.TryWrite(new WindowVanished(hwnd)));

            for (var attempt = 0; attempt < 200 && registry.TryGetEntry(hwnd) is not null; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.Null(registry.TryGetEntry(hwnd));

            // The same process (same pid) is later handed the exact same recycled HWND value for a
            // genuinely different window -- without the purge above, TryAdmitAsync would have
            // returned `original` again instead of minting a fresh WindowId.
            WindowId? afterRecycling = await registry.TryAdmitAsync(hwnd, TestContext.Current.CancellationToken);
            Assert.NotEqual(original, afterRecycling);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task AnIntentOtherThanWindowVanishedNeverPurgesAnyRegistryEntry()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);

        var pidReader = new FakeWindowProcessIdReader();
        WindowRegistry registry = CreateRegistry(pidReader, new FakeWindowManageabilityInfoReader(), new FakeWindowIdentityResolver(), time);
        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, registry, reconciler, CreatePlacementExecutor(new FakePlacementSystem(), time));
        using var cts = new CancellationTokenSource();

        var hwnd = new HWND((nint)0x9100);
        pidReader.SetPid(hwnd, 7);
        WindowId? admitted = await registry.TryAdmitAsync(hwnd, TestContext.Current.CancellationToken);
        Assert.NotNull(admitted);

        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(intents.Writer.TryWrite(new ForegroundChanged(hwnd)));

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.Equal(admitted, registry.TryGetEntry(hwnd)?.WindowId);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task WindowVanishedIntentAlsoPurgesThePlacementExecutorsQuarantineState()
    {
        // Codex review finding on this PR: without this, PlacementExecutor._quarantine grows
        // unboundedly across the churn of windows opening/closing, and -- observably, per this test
        // -- a window's sticky "ever been hung" backoff outlives the window itself. Mirrors
        // PlacementExecutorTests.PurgeDropsQuarantineBookkeepingSoTheWindowIsReProbedImmediately's
        // own hang-then-purge-then-recover shape, but drives the purge through the real pump instead
        // of a direct Purge() call.
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);

        var pidReader = new FakeWindowProcessIdReader();
        WindowRegistry registry = CreateRegistry(pidReader, new FakeWindowManageabilityInfoReader(), new FakeWindowIdentityResolver(), time);

        var placementSystem = new FakePlacementSystem();
        PlacementExecutor placementExecutor = CreatePlacementExecutor(placementSystem, time);

        var hwnd = new HWND((nint)0x9300);
        pidReader.SetPid(hwnd, 55);
        WindowId? admitted = await registry.TryAdmitAsync(hwnd, TestContext.Current.CancellationToken);
        Assert.NotNull(admitted);
        WindowId windowId = admitted.Value;
        placementSystem.SetHwnd(windowId, hwnd);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, new Rect(0, 0, 100, 100))];
        await AssertHangArmsAStillBackedOffQuarantineAsync(placementExecutor, placementSystem, hwnd, plan, TestContext.Current.CancellationToken);

        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, registry, reconciler, placementExecutor);
        using var cts = new CancellationTokenSource();
        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(intents.Writer.TryWrite(new WindowVanished(hwnd)));

            for (var attempt = 0; attempt < 200 && registry.TryGetEntry(hwnd) is not null; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.Null(registry.TryGetEntry(hwnd));
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }

        // Same WindowId, still nominally "backed off" per the unadvanced fake clock and still
        // responsive -- but the vanish-triggered purge should have discarded the whole
        // QuarantineState, so a fresh apply now goes all the way through to a real (synchronous,
        // batch-path) Moved outcome instead of being quarantined again.
        PlacementOutcome afterPurge = Assert.Single(await placementExecutor.ApplyAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(PlacementOutcomeKind.Moved, afterPurge.Kind);
    }

    /// <summary>
    /// Hangs <paramref name="hwnd"/> once (arming both the transient backoff and the sticky "ever
    /// been hung" flag), then marks it responsive again and confirms the transient backoff -- checked
    /// before any probe -- is still active at this same unadvanced instant, proving it genuinely
    /// outlives the underlying hang unless something purges it.
    /// </summary>
    private static async Task AssertHangArmsAStillBackedOffQuarantineAsync(
        PlacementExecutor placementExecutor,
        FakePlacementSystem placementSystem,
        HWND hwnd,
        ImmutableArray<PlacementInstruction> plan,
        CancellationToken cancellationToken)
    {
        placementSystem.SetHung(hwnd);
        PlacementOutcome hungOutcome = Assert.Single(await placementExecutor.ApplyAsync(plan, cancellationToken).ConfigureAwait(true));
        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, hungOutcome.Kind);

        placementSystem.SetResponsive(hwnd);
        PlacementOutcome stillBackedOff = Assert.Single(await placementExecutor.ApplyAsync(plan, cancellationToken).ConfigureAwait(true));
        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, stillBackedOff.Kind);
    }
}
