using System.Collections.Immutable;
using Bastion.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>
/// Tier 1/2 tests (docs/engineering/testing.md §3/§5) for <see cref="Reconciler"/> — the
/// DESIGN.md §3.4 convergence actor — against a <see cref="FakeWindowSystem"/> and
/// <see cref="FakeLayoutEngine"/>, with zero interop types anywhere in this file. Runs on Linux CI
/// exactly like <c>Bastion.Layout.Tests</c> (pure-core skill; no <c>Bastion.Win32</c> reference).
/// </summary>
public sealed class ReconcilerTests
{
    private static readonly Rect s_workArea = new(0, 0, 1920, 1080);

    // --- Construction & validation ------------------------------------------------------------

    [Fact]
    public void ConstructorRejectsANullWindowSystem()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentNullException>(() => new Reconciler(null!, new FakeLayoutEngine(), time));
    }

    [Fact]
    public void ConstructorRejectsANullLayoutEngine()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentNullException>(() => new Reconciler(new FakeWindowSystem(), null!, time));
    }

    [Fact]
    public void ConstructorRejectsANullTimeProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ConstructorRejectsANonPositiveHeartbeatInterval(double seconds)
    {
        var time = new FakeTimeProvider();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromSeconds(seconds) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), time, options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsANonPositiveReassertBudgetPerWindow(int budget)
    {
        var time = new FakeTimeProvider();
        var options = new ReconcilerOptions { ReassertBudgetPerWindow = budget };
        Assert.Throws<ArgumentOutOfRangeException>(() => new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), time, options));
    }

    [Fact]
    public void ConstructorRejectsANonPositiveReassertBudgetWindow()
    {
        var time = new FakeTimeProvider();
        var options = new ReconcilerOptions { ReassertBudgetWindow = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(() => new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), time, options));
    }

    [Fact]
    public void ConstructorRejectsANegativePositionTolerance()
    {
        var time = new FakeTimeProvider();
        var options = new ReconcilerOptions { PositionToleranceDevicePixels = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), time, options));
    }

    // --- Never-default snapshots (docs/engineering/daemon-architecture.md §5) ----------------

    [Fact]
    public void ObservedStateAndPlacementPlanAreNeverDefaultEvenBeforeAnyConvergencePass()
    {
        using var reconciler = new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), new FakeTimeProvider());

        Assert.False(reconciler.ObservedState.IsDefault);
        Assert.False(reconciler.LastPlacementPlan.IsDefault);
        Assert.Empty(reconciler.ObservedState);
        Assert.Empty(reconciler.LastPlacementPlan);
    }

    [Fact]
    public async Task ObservedStateIsRebuiltWhollyEveryConvergencePassAndNeverDefault()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, s_workArea, s_workArea, IsCloaked: false, IsIconic: false, IsZoomed: false));

        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(reconciler.ObservedState.IsDefault);
        Assert.Single(reconciler.ObservedState);
        Assert.Equal(windowId, reconciler.ObservedState[0].WindowId);
    }

    // --- Desired-window-set sync --------------------------------------------------------------

    [Fact]
    public async Task NewlyObservedWindowIsAutoAdmittedIntoTheDefaultWorkspace()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, default, default, IsCloaked: false, IsIconic: false, IsZoomed: false));

        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(reconciler.DesiredState.ContainsWindow(windowId));
    }

    [Fact]
    public async Task WithoutADefaultWorkspaceRegisteredNewWindowsAreObservedButNotDesired()
    {
        // No SetWorkspace call at all -- v0.1 has no monitor-assignment policy (GitHub issue #16)
        // to invent a work area from nothing, so a window has nowhere to go until a caller
        // registers WorkspaceKey.Default.
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, default, default, IsCloaked: false, IsIconic: false, IsZoomed: false));

        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(reconciler.ObservedState);
        Assert.False(reconciler.DesiredState.ContainsWindow(windowId));
    }

    [Fact]
    public async Task CloakedWindowIsTrackedInObservedStateButNeverDesired()
    {
        // DESIGN.md §3.3/§4: "any nonzero cloak value -> keep tracked, never tile, never forget."
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, default, default, IsCloaked: true, IsIconic: false, IsZoomed: false));

        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(reconciler.ObservedState); // kept tracked
        Assert.False(reconciler.DesiredState.ContainsWindow(windowId)); // never tiled
    }

    [Fact]
    public async Task AWindowThatBecomesCloakedAfterAdmissionIsRemovedFromDesiredStateButStillObserved()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, s_workArea, s_workArea, IsCloaked: false, IsIconic: false, IsZoomed: false));
        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(reconciler.DesiredState.ContainsWindow(windowId));

        windowSystem.Windows[0] = windowSystem.Windows[0] with { IsCloaked = true };
        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(reconciler.ObservedState);
        Assert.False(reconciler.DesiredState.ContainsWindow(windowId));
    }

    [Fact]
    public async Task VanishedWindowIsRemovedFromBothObservedAndDesiredState()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, s_workArea, s_workArea, IsCloaked: false, IsIconic: false, IsZoomed: false));
        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(reconciler.DesiredState.ContainsWindow(windowId));

        windowSystem.Windows.Clear();
        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(reconciler.ObservedState);
        Assert.False(reconciler.DesiredState.ContainsWindow(windowId));
    }

    [Fact]
    public async Task SetWorkspaceCalledAgainForTheSameKeyPreservesTheExistingWindowList()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        windowSystem.Windows.Add(new ObservedWindow(windowId, s_workArea, s_workArea, IsCloaked: false, IsIconic: false, IsZoomed: false));
        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(reconciler.DesiredState.ContainsWindow(windowId));

        // Re-registering the same workspace key (e.g. a monitor resize) must not lose the window
        // list ConvergeOnceAsync's own auto-admission already populated.
        Rect resized = s_workArea with { Right = 2560 };
        reconciler.SetWorkspace(WorkspaceKey.Default, resized);

        Assert.Equal(resized, reconciler.DesiredState.Workspaces[WorkspaceKey.Default].WorkArea);
        Assert.True(reconciler.DesiredState.ContainsWindow(windowId));
    }

    // --- Reassert budget (DESIGN.md §3.4) -----------------------------------------------------

    [Fact]
    public async Task ExhaustingTheReassertBudgetUntilesTheWindowInsteadOfContinuingToMoveIt()
    {
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { ReassertBudgetPerWindow = 2, ReassertBudgetWindow = TimeSpan.FromSeconds(2) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);

        // Never actually converges: FakeLayoutEngine always targets s_workArea, but the observed
        // rect here never matches it -- simulating an app that keeps fighting the tiler (DESIGN.md
        // §3.4's "post-DPI self-resizes and app-initiated geometry drift").
        var neverConvergingRect = new Rect(0, 0, 10, 10);
        windowSystem.Windows.Add(new ObservedWindow(windowId, neverConvergingRect, neverConvergingRect, IsCloaked: false, IsIconic: false, IsZoomed: false));

        ImmutableArray<PlacementInstruction> first = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        ImmutableArray<PlacementInstruction> second = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        ImmutableArray<PlacementInstruction> third = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PlacementAction.Move, Assert.Single(first).Action);
        Assert.Equal(PlacementAction.Move, Assert.Single(second).Action);
        Assert.Equal(PlacementAction.Untile, Assert.Single(third).Action);
        Assert.False(reconciler.DesiredState.ContainsWindow(windowId));
        Assert.Contains(windowId, reconciler.DesiredState.UntiledWindows);

        // Untiled -> never re-admitted on a later pass just because it is still observed and
        // eligible (not cloaked) -- otherwise the untile decision would be undone on the very next
        // tick.
        await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.False(reconciler.DesiredState.ContainsWindow(windowId));
    }

    [Fact]
    public async Task ReassertBudgetResetsAfterItsRollingWindowElapses()
    {
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { ReassertBudgetPerWindow = 1, ReassertBudgetWindow = TimeSpan.FromSeconds(2) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        var neverConvergingRect = new Rect(0, 0, 10, 10);
        windowSystem.Windows.Add(new ObservedWindow(windowId, neverConvergingRect, neverConvergingRect, IsCloaked: false, IsIconic: false, IsZoomed: false));

        ImmutableArray<PlacementInstruction> first = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlacementAction.Move, Assert.Single(first).Action);

        // Budget (1 per 2 s) is now exhausted for this tick's window -- advancing past the window
        // resets it rather than requiring a brand-new window identity.
        time.Advance(options.ReassertBudgetWindow + TimeSpan.FromMilliseconds(1));

        ImmutableArray<PlacementInstruction> second = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlacementAction.Move, Assert.Single(second).Action);
    }

    [Fact]
    public async Task ReassertBudgetIsATrueTrailingWindowNotAFixedWindowThatDoublesAtItsBoundary()
    {
        // Codex review finding on this PR: a fixed window anchored at the first attempt (reset
        // entirely once stale, rather than aging out one entry at a time) can let roughly double
        // the configured budget through right at its own reset boundary. With budget=2 per 2s:
        // attempts at t=0ms and t=1990ms exhaust it; a fixed-window implementation would then
        // reset fully at t=2010ms (since 2010-0 >= 2000) and wrongly allow BOTH a 3rd attempt at
        // 2010ms AND a 4th at 2020ms, even though [1990ms, 2020ms] spans only 30ms and already
        // contains two recent attempts. A true trailing window allows the 3rd (only 1990ms is
        // still "recent" once 0ms ages out) but must reject the 4th (both 1990ms and 2010ms are
        // then "recent").
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { ReassertBudgetPerWindow = 2, ReassertBudgetWindow = TimeSpan.FromSeconds(2) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);
        var neverConvergingRect = new Rect(0, 0, 10, 10);
        windowSystem.Windows.Add(new ObservedWindow(windowId, neverConvergingRect, neverConvergingRect, IsCloaked: false, IsIconic: false, IsZoomed: false));

        ImmutableArray<PlacementInstruction> atZeroMs = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlacementAction.Move, Assert.Single(atZeroMs).Action);

        time.Advance(TimeSpan.FromMilliseconds(1990));
        ImmutableArray<PlacementInstruction> at1990Ms = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlacementAction.Move, Assert.Single(at1990Ms).Action);

        time.Advance(TimeSpan.FromMilliseconds(20)); // now at 2010ms
        ImmutableArray<PlacementInstruction> at2010Ms = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlacementAction.Move, Assert.Single(at2010Ms).Action); // correct: only 1990ms is still "recent"

        time.Advance(TimeSpan.FromMilliseconds(10)); // now at 2020ms
        ImmutableArray<PlacementInstruction> at2020Ms = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlacementAction.Untile, Assert.Single(at2020Ms).Action); // both 1990ms and 2010ms are "recent" -- reject
    }

    [Fact]
    public async Task WithinToleranceObservedRectDoesNotConsumeAnyReassertBudget()
    {
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { ReassertBudgetPerWindow = 1, PositionToleranceDevicePixels = 2 };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);

        // Within the configured 2px tolerance of s_workArea on every edge.
        Rect almostConverged = s_workArea with { Right = s_workArea.Right - 1 };
        windowSystem.Windows.Add(new ObservedWindow(windowId, almostConverged, almostConverged, IsCloaked: false, IsIconic: false, IsZoomed: false));

        ImmutableArray<PlacementInstruction> first = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        ImmutableArray<PlacementInstruction> second = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Empty(second); // would be Untile on a second call if the first had wrongly consumed budget
    }

    // --- Convergence triggers (DESIGN.md §3.4) ------------------------------------------------

    [Fact]
    public async Task HeartbeatTickDrivesAConvergencePassViaFakeTimeProvider()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromSeconds(5) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        try
        {
            Assert.Equal(0, windowSystem.ReadAllCallCount);

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                time.Advance(options.HeartbeatInterval);
                if (windowSystem.ReadAllCallCount > 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1, "Expected the heartbeat to drive at least one convergence pass.");
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task RequestReconcileNowTriggersAConvergencePassWithoutWaitingForTheHeartbeat()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();

        // A heartbeat interval far longer than this test could ever run for isolates the
        // wake-signal path from the heartbeat path -- if the assertion below passes, it is because
        // RequestReconcileNow woke the loop, not because the heartbeat happened to also fire.
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        try
        {
            Assert.Equal(0, windowSystem.ReadAllCallCount);

            reconciler.RequestReconcileNow();

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1, "Expected RequestReconcileNow to drive a convergence pass without a heartbeat tick.");
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task MultipleRequestReconcileNowCallsBeforeTheLoopWakesCoalesceIntoOnePass()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        try
        {
            // Mirrors DESIGN.md §3.4's coalesced-intent/distrust-escalation triggers both folding
            // into the same "wake the loop" signal (docs/engineering/concurrency-performance.md
            // §1's DropWrite rationale: a pending wake is enough, extra ones are redundant).
            reconciler.RequestReconcileNow();
            reconciler.RequestReconcileNow();
            reconciler.RequestReconcileNow();

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.Equal(1, windowSystem.ReadAllCallCount);
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }
}
