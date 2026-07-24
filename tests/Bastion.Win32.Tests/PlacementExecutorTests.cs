using System.Collections.Immutable;
using System.Runtime;
using Bastion.Core;
using Bastion.Win32;
using Microsoft.Extensions.Time.Testing;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1/2 tests (docs/engineering/testing.md §3/§5) for <see cref="PlacementExecutor"/> against a
/// <see cref="FakePlacementSystem"/> — zero real HWNDs anywhere in this file. Covers the
/// acceptance-criteria-mandated Defer-batch-failure-falls-back-to-per-window-SetWindowPos path
/// (<see cref="DeferBatchFailureAbandonsTheWholeBatchAndFallsBackToPerWindowSetWindowPosForEveryWindow"/>),
/// hang-quarantine/backoff, state normalization and its coordinate-space branch, clamp detection and
/// the single-request-per-pass <c>RequestReconcileNow</c> signal, <c>GCSettings.LatencyMode</c>
/// scoping, plan-order preservation, and the pass-wide async-settle wait for
/// <c>WPF_ASYNCWINDOWPLACEMENT</c>/<c>SWP_ASYNCWINDOWPOS</c> placements (both Codex review findings
/// on this PR).
/// </summary>
public sealed class PlacementExecutorTests
{
    private static readonly Rect s_target = new(0, 0, 800, 600);

    // --- Construction & validation (Copilot review finding on this PR) --------------------------

    [Fact]
    public void ConstructorRejectsANullPlacementSystem()
    {
        Assert.Throws<ArgumentNullException>(() => new PlacementExecutor(null!, new FakeReconcileNowSignal(), new FakeTimeProvider()));
    }

    [Fact]
    public void ConstructorRejectsANullReconcileNowSignal()
    {
        Assert.Throws<ArgumentNullException>(() => new PlacementExecutor(new FakePlacementSystem(), null!, new FakeTimeProvider()));
    }

    [Fact]
    public void ConstructorRejectsANullTimeProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new PlacementExecutor(new FakePlacementSystem(), new FakeReconcileNowSignal(), null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsANonPositiveHangProbeTimeout(double milliseconds)
    {
        var options = new PlacementExecutorOptions { HangProbeTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExecutor(new FakePlacementSystem(), options: options));
    }

    [Fact]
    public void ConstructorRejectsANonPositiveInitialQuarantineBackoff()
    {
        var options = new PlacementExecutorOptions { InitialQuarantineBackoff = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExecutor(new FakePlacementSystem(), options: options));
    }

    [Fact]
    public void ConstructorRejectsAMaxQuarantineBackoffSmallerThanTheInitialOne()
    {
        var options = new PlacementExecutorOptions
        {
            InitialQuarantineBackoff = TimeSpan.FromSeconds(10),
            MaxQuarantineBackoff = TimeSpan.FromSeconds(1),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExecutor(new FakePlacementSystem(), options: options));
    }

    [Fact]
    public void ConstructorRejectsAQuarantineBackoffMultiplierBelowOne()
    {
        var options = new PlacementExecutorOptions { QuarantineBackoffMultiplier = 0.5 };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExecutor(new FakePlacementSystem(), options: options));
    }

    [Fact]
    public void ConstructorRejectsANegativeSizeTolerance()
    {
        var options = new PlacementExecutorOptions { SizeToleranceDevicePixels = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExecutor(new FakePlacementSystem(), options: options));
    }

    [Fact]
    public void ConstructorRejectsANegativeAsyncVerifyDelay()
    {
        var options = new PlacementExecutorOptions { AsyncVerifyDelay = TimeSpan.FromMilliseconds(-1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateExecutor(new FakePlacementSystem(), options: options));
    }

    // --- Untile / vanished-window handling ------------------------------------------------------

    [Fact]
    public async Task EmptyPlanProducesAnEmptyResultWithoutTouchingTheSystem()
    {
        var system = new FakePlacementSystem();
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementOutcome> outcomes = await executor.ApplyAsync(
            ImmutableArray<PlacementInstruction>.Empty, TestContext.Current.CancellationToken);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task UntileInstructionProducesAnUntiledOutcomeWithNoWin32Calls()
    {
        var system = new FakePlacementSystem();
        var reconcileSignal = new FakeReconcileNowSignal();
        PlacementExecutor executor = CreateExecutor(system, reconcileSignal);
        var windowId = WindowId.FromOpaqueValue(1);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Untile(windowId)];
        PlacementOutcome outcome = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Untiled, outcome.Kind);
        Assert.Empty(system.DeferredHwnds);
        Assert.Empty(system.FallbackAppliedHwnds);
        Assert.Equal(0, reconcileSignal.RequestCount);
    }

    [Fact]
    public async Task UnresolvableWindowIdProducesAFailedOutcomeWithNoErrorCode()
    {
        // No SetHwnd call at all -- TryResolveHwnd returns false, simulating a window that vanished
        // between the Reconciler's plan and this pass.
        var system = new FakePlacementSystem();
        PlacementExecutor executor = CreateExecutor(system);
        var windowId = WindowId.FromOpaqueValue(1);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Failed, outcome.Kind);
        Assert.Null(outcome.ErrorCode);
    }

    // --- Plan-order preservation (Codex review finding on this PR) ------------------------------

    [Fact]
    public async Task OutcomesArePreservedInPlanOrderForAMixedPlan()
    {
        // A plan mixing a batch-bound Move with an immediately-resolved Untile must not come back
        // reordered by which execution phase happens to finish first.
        var system = new FakePlacementSystem();
        HWND hwndA = new(1);
        var windowIdA = WindowId.FromOpaqueValue(1);
        var windowIdB = WindowId.FromOpaqueValue(2);
        system.SetHwnd(windowIdA, hwndA);
        system.SetGeometry(hwndA, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan =
            [PlacementInstruction.Move(windowIdA, s_target), PlacementInstruction.Untile(windowIdB)];

        ImmutableArray<PlacementOutcome> outcomes = await executor.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(2, outcomes.Length);
        Assert.Equal(windowIdA, outcomes[0].WindowId);
        Assert.Equal(PlacementOutcomeKind.Moved, outcomes[0].Kind);
        Assert.Equal(windowIdB, outcomes[1].WindowId);
        Assert.Equal(PlacementOutcomeKind.Untiled, outcomes[1].Kind);
    }

    // --- Hang probe / quarantine backoff (DESIGN.md §3.6a, §9) ----------------------------------

    [Fact]
    public async Task HungWindowIsQuarantinedAndNeverReachesTheDeferBatchOrFallback()
    {
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetHung(hwnd);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, outcome.Kind);
        Assert.Empty(system.DeferredHwnds);
        Assert.Empty(system.FallbackAppliedHwnds);
    }

    [Fact]
    public async Task QuarantineBackoffSkipsReProbingUntilItElapsesThenReProbes()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetHung(hwnd);
        var options = new PlacementExecutorOptions { InitialQuarantineBackoff = TimeSpan.FromSeconds(2) };
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time, options: options);
        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken)).Kind);

        // Still within the 2 s backoff. Flip the fake to "responsive" to prove the executor does
        // NOT re-probe here: if it did, this assertion would see a non-quarantined outcome instead.
        system.SetResponsive(hwnd);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken)).Kind);

        // Backoff now elapsed -- re-probes, sees responsive, proceeds. This window is "ever seen
        // hung" (sticky), so it now takes the per-window (async-flagged) fallback path, not the
        // Defer batch -- needs the pass-wide settle wait too.
        time.Advance(TimeSpan.FromSeconds(2));
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementOutcome afterBackoff = Assert.Single(await ApplyAndSettleAsync(executor, plan, time));
        Assert.Equal(PlacementOutcomeKind.Moved, afterBackoff.Kind);
    }

    [Fact]
    public async Task EverHungWindowAlwaysUsesThePerWindowFallbackEvenAfterRecovering()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetHung(hwnd);
        var options = new PlacementExecutorOptions { InitialQuarantineBackoff = TimeSpan.FromMilliseconds(1) };
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time, options: options);
        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken)).Kind);

        system.SetResponsive(hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        time.Advance(TimeSpan.FromSeconds(1));

        PlacementOutcome recovered = Assert.Single(await ApplyAndSettleAsync(executor, plan, time));

        // DESIGN.md §3.6d: "the standing mode for any window ever seen hung" -- per-window
        // SetWindowPos, never the Defer batch again, even though it is responsive again now.
        Assert.Equal(PlacementOutcomeKind.Moved, recovered.Kind);
        Assert.Contains(hwnd, system.FallbackAppliedHwnds);
        Assert.DoesNotContain(hwnd, system.DeferredHwnds);
    }

    [Fact]
    public async Task PurgeDropsQuarantineBookkeepingSoTheWindowIsReProbedImmediately()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetHung(hwnd);
        var options = new PlacementExecutorOptions
        {
            InitialQuarantineBackoff = TimeSpan.FromMinutes(10),
            MaxQuarantineBackoff = TimeSpan.FromMinutes(10),
        };
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time, options: options);
        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken)).Kind);

        // Purge resets the sticky ever-hung flag too, so this window re-enters the (synchronous)
        // Defer batch on its next successful placement -- no settle wait needed here.
        executor.Purge(windowId);
        system.SetResponsive(hwnd);
        system.SetGeometry(hwnd, s_target, s_target);

        // Without Purge, this would still be well within the 10-minute backoff and skipped again.
        PlacementOutcome afterPurge = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));
        Assert.Equal(PlacementOutcomeKind.Moved, afterPurge.Kind);
    }

    // --- Batch apply / Defer-batch-failure fallback (DESIGN.md §3.6d) ---------------------------

    [Fact]
    public async Task NormalWindowGoesThroughTheDeferBatchAndIsVerifiedAfterMove()
    {
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Contains(hwnd, system.DeferredHwnds);
        Assert.Equal(1, system.EndDeferCallCount);
        Assert.Equal(s_target, outcome.VerifiedBounds);
        Assert.Null(outcome.ClampedTo);
    }

    /// <summary>The acceptance criteria's explicitly-required test.</summary>
    [Fact]
    public async Task DeferBatchFailureAbandonsTheWholeBatchAndFallsBackToPerWindowSetWindowPosForEveryWindow()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);

        var windowIdA = WindowId.FromOpaqueValue(1);
        var windowIdB = WindowId.FromOpaqueValue(2);
        var windowIdC = WindowId.FromOpaqueValue(3);
        HWND hwndA = new(1);
        HWND hwndB = new(2);
        HWND hwndC = new(3);
        system.SetHwnd(windowIdA, hwndA);
        system.SetHwnd(windowIdB, hwndB);
        system.SetHwnd(windowIdC, hwndC);

        var targetA = new Rect(0, 0, 100, 100);
        var targetB = new Rect(100, 0, 200, 100);
        var targetC = new Rect(200, 0, 300, 100);
        system.SetGeometry(hwndA, targetA, targetA);
        system.SetGeometry(hwndB, targetB, targetB);
        system.SetGeometry(hwndC, targetC, targetC);

        // The middle window fails DeferWindowPos -- per DESIGN.md §3.6d / DeferWindowPos's own
        // documented contract, this must abandon the ENTIRE batch (never call EndDeferWindowPos)
        // and fall back to per-window SetWindowPos for ALL THREE windows, not just the one that
        // failed.
        system.SetDeferFails(hwndB);

        ImmutableArray<PlacementInstruction> plan =
        [
            PlacementInstruction.Move(windowIdA, targetA),
            PlacementInstruction.Move(windowIdB, targetB),
            PlacementInstruction.Move(windowIdC, targetC),
        ];

        ImmutableArray<PlacementOutcome> outcomes = await ApplyAndSettleAsync(executor, plan, time);

        Assert.Equal(0, system.EndDeferCallCount);
        Assert.Equal(3, system.FallbackAppliedHwnds.Count);
        Assert.Contains(hwndA, system.FallbackAppliedHwnds);
        Assert.Contains(hwndB, system.FallbackAppliedHwnds);
        Assert.Contains(hwndC, system.FallbackAppliedHwnds);
        Assert.All(outcomes, o => Assert.Equal(PlacementOutcomeKind.Moved, o.Kind));
    }

    [Fact]
    public async Task EndDeferWindowPosFailureAlsoFallsBackToPerWindowForEveryWindow()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem { EndDeferFails = true };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await ApplyAndSettleAsync(executor, plan, time));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Contains(hwnd, system.FallbackAppliedHwnds);
    }

    [Fact]
    public async Task GCLatencyModeIsRestoredAfterTheDeferBatchEvenOnFailure()
    {
        // GCSettings.LatencyMode is process-wide, and this repo does not disable xUnit
        // parallelization -- setting it to a specific value here (even restored in a finally)
        // could still race with other concurrently running tests reading or writing it in between
        // (Copilot review finding on this PR). Capturing (never writing) the ambient value and
        // asserting it is unchanged afterward is an equally strong regression test for "restored in
        // a finally," without this test ever mutating shared global state itself.
        GCLatencyMode ambient = GCSettings.LatencyMode;
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem { EndDeferFails = true };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        await ApplyAndSettleAsync(executor, plan, time);

        Assert.Equal(ambient, GCSettings.LatencyMode);
    }

    // --- Async-settle wait for WPF_ASYNCWINDOWPLACEMENT/SWP_ASYNCWINDOWPOS (Codex finding) -------

    [Fact]
    public async Task StateNormalizationDoesNotVerifyUntilThePassWideSettleWaitElapses()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);
        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];

        Task<ImmutableArray<PlacementOutcome>> applyTask = executor.ApplyAsync(plan, TestContext.Current.CancellationToken);

        // A real (short) delay to let the task actually reach its awaited Task.Delay without
        // advancing the fake clock -- it must NOT have completed yet. This is the direct regression
        // test for the Codex finding: an immediate geometry read after WPF_ASYNCWINDOWPLACEMENT can
        // observe stale, pre-move bounds and misreport a clamp.
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.False(applyTask.IsCompleted);

        time.Advance(PlacementExecutorOptions.Default.AsyncVerifyDelay);
        PlacementOutcome outcome = Assert.Single(await applyTask.ConfigureAwait(true));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
    }

    // --- State normalization / coordinate space (DESIGN.md §3.6b) -------------------------------

    [Fact]
    public async Task IconicWindowIsRestoredDirectlyViaSetWindowPlacementNotTheDeferBatch()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await ApplyAndSettleAsync(executor, plan, time));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Single(system.AppliedPlacements);
        Assert.DoesNotContain(hwnd, system.DeferredHwnds);
        Assert.DoesNotContain(hwnd, system.FallbackAppliedHwnds);
        Assert.Equal(0, system.EndDeferCallCount);
    }

    [Fact]
    public async Task NonToolWindowStateNormalizationConvertsToWorkspaceCoordinates()
    {
        // A taskbar docked at the top of the primary monitor eats 40px off its own work area.
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem { PrimaryWorkArea = new Rect(0, 40, 1920, 1080) };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        var target = new Rect(0, 40, 800, 640); // screen coordinates, zero invisible border
        system.SetGeometry(hwnd, target, target);
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, target)];
        await ApplyAndSettleAsync(executor, plan, time);

        (HWND Hwnd, Rect Target) applied = Assert.Single(system.AppliedPlacements);
        Assert.Equal(new Rect(0, 0, 800, 600), applied.Target);
    }

    [Fact]
    public async Task ToolWindowStateNormalizationUsesScreenCoordinatesUnconverted()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem { PrimaryWorkArea = new Rect(0, 40, 1920, 1080) };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: true));
        var target = new Rect(0, 40, 800, 640);
        system.SetGeometry(hwnd, target, target);
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, target)];
        await ApplyAndSettleAsync(executor, plan, time);

        (HWND Hwnd, Rect Target) applied = Assert.Single(system.AppliedPlacements);
        Assert.Equal(target, applied.Target);
    }

    [Fact]
    public async Task FailingSetWindowPlacementProducesAFailedOutcomeWithThePreservedErrorCode()
    {
        // The apply call itself fails, so there is nothing to verify and no settle wait is needed --
        // the failure is written immediately.
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        system.SetPlacementResult(hwnd, PlacementCallResult.Fail(WIN32_ERROR.ERROR_ACCESS_DENIED));
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(WIN32_ERROR.ERROR_ACCESS_DENIED, outcome.ErrorCode);
    }

    // --- Verify-after-move / clamp detection (DESIGN.md §3.6e) ----------------------------------

    [Fact]
    public async Task ClampedResultIsSurfacedAndTriggersExactlyOneReconcileNowRequestPerPass()
    {
        var system = new FakePlacementSystem();
        var reconcileSignal = new FakeReconcileNowSignal();
        HWND hwndA = new(1);
        HWND hwndB = new(2);
        var windowIdA = WindowId.FromOpaqueValue(1);
        var windowIdB = WindowId.FromOpaqueValue(2);
        system.SetHwnd(windowIdA, hwndA);
        system.SetHwnd(windowIdB, hwndB);
        var targetA = new Rect(0, 0, 400, 300);
        var targetB = new Rect(400, 0, 800, 300);

        // Both windows refuse to shrink to the requested width. Neither has a WindowPlacementState
        // configured (defaults to "no state normalization needed"), so both go through the
        // synchronous Defer batch -- no settle wait needed.
        system.SetGeometry(hwndA, new Rect(0, 0, 500, 300), new Rect(0, 0, 500, 300));
        system.SetGeometry(hwndB, new Rect(400, 0, 850, 300), new Rect(400, 0, 850, 300));
        PlacementExecutor executor = CreateExecutor(system, reconcileSignal);

        ImmutableArray<PlacementInstruction> plan =
            [PlacementInstruction.Move(windowIdA, targetA), PlacementInstruction.Move(windowIdB, targetB)];
        ImmutableArray<PlacementOutcome> outcomes = await executor.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.All(outcomes, o => Assert.NotNull(o.ClampedTo));
        Assert.Equal(1, reconcileSignal.RequestCount);
    }

    [Fact]
    public async Task NonClampedResultDoesNotTriggerAReconcileNowRequest()
    {
        var system = new FakePlacementSystem();
        var reconcileSignal = new FakeReconcileNowSignal();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system, reconcileSignal);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        await executor.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(0, reconcileSignal.RequestCount);
    }

    [Fact]
    public async Task WindowVanishingBetweenTheMoveAndTheVerifyReadStillCountsAsMoved()
    {
        // No SetGeometry call -- TryReadGeometry returns false on the verify-after-move read, a
        // routine race (the window was destroyed between the move and this read). No
        // WindowPlacementState configured, so this goes through the synchronous Defer batch.
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(await executor.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Null(outcome.VerifiedBounds);
        Assert.Null(outcome.ClampedTo);
    }

    private static PlacementExecutor CreateExecutor(
        FakePlacementSystem system,
        FakeReconcileNowSignal? reconcileSignal = null,
        FakeTimeProvider? timeProvider = null,
        PlacementExecutorOptions? options = null) =>
        new(system, reconcileSignal ?? new FakeReconcileNowSignal(), timeProvider ?? new FakeTimeProvider(), options);

    /// <summary>
    /// Starts <see cref="PlacementExecutor.ApplyAsync"/>, advances <paramref name="time"/> by the
    /// default async-settle delay to unblock its pass-wide <c>Task.Delay</c> wait (see
    /// <see cref="PlacementExecutor"/>'s own remarks), then awaits completion. <c>ApplyAsync</c>
    /// runs synchronously up to that awaited delay before returning a <see cref="Task"/> at all
    /// (ordinary C# async-method semantics), so by the time this helper calls
    /// <see cref="FakeTimeProvider.Advance"/> the delay has already been registered against the
    /// fake clock's current time.
    /// </summary>
    private static async Task<ImmutableArray<PlacementOutcome>> ApplyAndSettleAsync(
        PlacementExecutor executor, ImmutableArray<PlacementInstruction> plan, FakeTimeProvider time)
    {
        Task<ImmutableArray<PlacementOutcome>> task = executor.ApplyAsync(plan, TestContext.Current.CancellationToken);
        time.Advance(PlacementExecutorOptions.Default.AsyncVerifyDelay);
        return await task.ConfigureAwait(true);
    }
}
