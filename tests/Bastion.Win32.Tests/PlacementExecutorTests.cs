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
/// the single-request-per-pass <c>RequestReconcileNow</c> signal, and <c>GCSettings.LatencyMode</c>
/// scoping.
/// </summary>
public sealed class PlacementExecutorTests
{
    private static readonly Rect s_target = new(0, 0, 800, 600);

    // --- Untile / vanished-window handling ------------------------------------------------------

    [Fact]
    public void EmptyPlanProducesAnEmptyResultWithoutTouchingTheSystem()
    {
        var system = new FakePlacementSystem();
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementOutcome> outcomes = executor.Apply(
            ImmutableArray<PlacementInstruction>.Empty, TestContext.Current.CancellationToken);

        Assert.Empty(outcomes);
    }

    [Fact]
    public void UntileInstructionProducesAnUntiledOutcomeWithNoWin32Calls()
    {
        var system = new FakePlacementSystem();
        var reconcileSignal = new FakeReconcileNowSignal();
        PlacementExecutor executor = CreateExecutor(system, reconcileSignal);
        var windowId = WindowId.FromOpaqueValue(1);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Untile(windowId)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Untiled, outcome.Kind);
        Assert.Empty(system.DeferredHwnds);
        Assert.Empty(system.FallbackAppliedHwnds);
        Assert.Equal(0, reconcileSignal.RequestCount);
    }

    [Fact]
    public void UnresolvableWindowIdProducesAFailedOutcomeWithNoErrorCode()
    {
        // No SetHwnd call at all -- TryResolveHwnd returns false, simulating a window that vanished
        // between the Reconciler's plan and this pass.
        var system = new FakePlacementSystem();
        PlacementExecutor executor = CreateExecutor(system);
        var windowId = WindowId.FromOpaqueValue(1);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Failed, outcome.Kind);
        Assert.Null(outcome.ErrorCode);
    }

    // --- Hang probe / quarantine backoff (DESIGN.md §3.6a, §9) ----------------------------------

    [Fact]
    public void HungWindowIsQuarantinedAndNeverReachesTheDeferBatchOrFallback()
    {
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetHung(hwnd);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, outcome.Kind);
        Assert.Empty(system.DeferredHwnds);
        Assert.Empty(system.FallbackAppliedHwnds);
    }

    [Fact]
    public void QuarantineBackoffSkipsReProbingUntilItElapsesThenReProbes()
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

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken)).Kind);

        // Still within the 2 s backoff. Flip the fake to "responsive" to prove the executor does
        // NOT re-probe here: if it did, this assertion would see a non-quarantined outcome instead.
        system.SetResponsive(hwnd);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken)).Kind);

        // Backoff now elapsed -- re-probes, sees responsive, proceeds normally.
        time.Advance(TimeSpan.FromSeconds(2));
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementOutcome afterBackoff = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));
        Assert.Equal(PlacementOutcomeKind.Moved, afterBackoff.Kind);
    }

    [Fact]
    public void EverHungWindowAlwaysUsesThePerWindowFallbackEvenAfterRecovering()
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

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken)).Kind);

        system.SetResponsive(hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        time.Advance(TimeSpan.FromSeconds(1));

        PlacementOutcome recovered = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        // DESIGN.md §3.6d: "the standing mode for any window ever seen hung" -- per-window
        // SetWindowPos, never the Defer batch again, even though it is responsive again now.
        Assert.Equal(PlacementOutcomeKind.Moved, recovered.Kind);
        Assert.Contains(hwnd, system.FallbackAppliedHwnds);
        Assert.DoesNotContain(hwnd, system.DeferredHwnds);
    }

    [Fact]
    public void PurgeDropsQuarantineBookkeepingSoTheWindowIsReProbedImmediately()
    {
        var time = new FakeTimeProvider();
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetHung(hwnd);
        var options = new PlacementExecutorOptions { InitialQuarantineBackoff = TimeSpan.FromMinutes(10) };
        PlacementExecutor executor = CreateExecutor(system, timeProvider: time, options: options);
        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];

        Assert.Equal(PlacementOutcomeKind.QuarantinedHung, Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken)).Kind);

        executor.Purge(windowId);
        system.SetResponsive(hwnd);
        system.SetGeometry(hwnd, s_target, s_target);

        // Without Purge, this would still be well within the 10-minute backoff and skipped again.
        PlacementOutcome afterPurge = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));
        Assert.Equal(PlacementOutcomeKind.Moved, afterPurge.Kind);
    }

    // --- Batch apply / Defer-batch-failure fallback (DESIGN.md §3.6d) ---------------------------

    [Fact]
    public void NormalWindowGoesThroughTheDeferBatchAndIsVerifiedAfterMove()
    {
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Contains(hwnd, system.DeferredHwnds);
        Assert.Equal(1, system.EndDeferCallCount);
        Assert.Equal(s_target, outcome.VerifiedBounds);
        Assert.Null(outcome.ClampedTo);
    }

    /// <summary>The acceptance criteria's explicitly-required test.</summary>
    [Fact]
    public void DeferBatchFailureAbandonsTheWholeBatchAndFallsBackToPerWindowSetWindowPosForEveryWindow()
    {
        var system = new FakePlacementSystem();
        PlacementExecutor executor = CreateExecutor(system);

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

        ImmutableArray<PlacementOutcome> outcomes = executor.Apply(plan, TestContext.Current.CancellationToken);

        Assert.Equal(0, system.EndDeferCallCount);
        Assert.Equal(3, system.FallbackAppliedHwnds.Count);
        Assert.Contains(hwndA, system.FallbackAppliedHwnds);
        Assert.Contains(hwndB, system.FallbackAppliedHwnds);
        Assert.Contains(hwndC, system.FallbackAppliedHwnds);
        Assert.All(outcomes, o => Assert.Equal(PlacementOutcomeKind.Moved, o.Kind));
    }

    [Fact]
    public void EndDeferWindowPosFailureAlsoFallsBackToPerWindowForEveryWindow()
    {
        var system = new FakePlacementSystem { EndDeferFails = true };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Contains(hwnd, system.FallbackAppliedHwnds);
    }

    [Fact]
    public void GCLatencyModeIsRestoredAfterTheDeferBatchEvenOnFailure()
    {
        GCLatencyMode original = GCSettings.LatencyMode;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            var system = new FakePlacementSystem { EndDeferFails = true };
            HWND hwnd = new(1);
            var windowId = WindowId.FromOpaqueValue(1);
            system.SetHwnd(windowId, hwnd);
            system.SetGeometry(hwnd, s_target, s_target);
            PlacementExecutor executor = CreateExecutor(system);

            ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
            executor.Apply(plan, TestContext.Current.CancellationToken);

            Assert.Equal(GCLatencyMode.Interactive, GCSettings.LatencyMode);
        }
        finally
        {
            GCSettings.LatencyMode = original;
        }
    }

    // --- State normalization / coordinate space (DESIGN.md §3.6b) -------------------------------

    [Fact]
    public void IconicWindowIsRestoredDirectlyViaSetWindowPlacementNotTheDeferBatch()
    {
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Moved, outcome.Kind);
        Assert.Single(system.AppliedPlacements);
        Assert.DoesNotContain(hwnd, system.DeferredHwnds);
        Assert.DoesNotContain(hwnd, system.FallbackAppliedHwnds);
        Assert.Equal(0, system.EndDeferCallCount);
    }

    [Fact]
    public void NonToolWindowStateNormalizationConvertsToWorkspaceCoordinates()
    {
        // A taskbar docked at the top of the primary monitor eats 40px off its own work area.
        var system = new FakePlacementSystem { PrimaryWorkArea = new Rect(0, 40, 1920, 1080) };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        var target = new Rect(0, 40, 800, 640); // screen coordinates, zero invisible border
        system.SetGeometry(hwnd, target, target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, target)];
        executor.Apply(plan, TestContext.Current.CancellationToken);

        (HWND Hwnd, Rect Target) applied = Assert.Single(system.AppliedPlacements);
        Assert.Equal(new Rect(0, 0, 800, 600), applied.Target);
    }

    [Fact]
    public void ToolWindowStateNormalizationUsesScreenCoordinatesUnconverted()
    {
        var system = new FakePlacementSystem { PrimaryWorkArea = new Rect(0, 40, 1920, 1080) };
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: true));
        var target = new Rect(0, 40, 800, 640);
        system.SetGeometry(hwnd, target, target);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, target)];
        executor.Apply(plan, TestContext.Current.CancellationToken);

        (HWND Hwnd, Rect Target) applied = Assert.Single(system.AppliedPlacements);
        Assert.Equal(target, applied.Target);
    }

    [Fact]
    public void FailingSetWindowPlacementProducesAFailedOutcomeWithThePreservedErrorCode()
    {
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetState(hwnd, new WindowPlacementState(IsIconic: true, IsZoomed: false, IsArranged: false, IsToolWindow: false));
        system.SetPlacementResult(hwnd, PlacementCallResult.Fail(WIN32_ERROR.ERROR_ACCESS_DENIED));
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

        Assert.Equal(PlacementOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(WIN32_ERROR.ERROR_ACCESS_DENIED, outcome.ErrorCode);
    }

    // --- Verify-after-move / clamp detection (DESIGN.md §3.6e) ----------------------------------

    [Fact]
    public void ClampedResultIsSurfacedAndTriggersExactlyOneReconcileNowRequestPerPass()
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

        // Both windows refuse to shrink to the requested width.
        system.SetGeometry(hwndA, new Rect(0, 0, 500, 300), new Rect(0, 0, 500, 300));
        system.SetGeometry(hwndB, new Rect(400, 0, 850, 300), new Rect(400, 0, 850, 300));
        PlacementExecutor executor = CreateExecutor(system, reconcileSignal);

        ImmutableArray<PlacementInstruction> plan =
            [PlacementInstruction.Move(windowIdA, targetA), PlacementInstruction.Move(windowIdB, targetB)];
        ImmutableArray<PlacementOutcome> outcomes = executor.Apply(plan, TestContext.Current.CancellationToken);

        Assert.All(outcomes, o => Assert.NotNull(o.ClampedTo));
        Assert.Equal(1, reconcileSignal.RequestCount);
    }

    [Fact]
    public void NonClampedResultDoesNotTriggerAReconcileNowRequest()
    {
        var system = new FakePlacementSystem();
        var reconcileSignal = new FakeReconcileNowSignal();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        system.SetGeometry(hwnd, s_target, s_target);
        PlacementExecutor executor = CreateExecutor(system, reconcileSignal);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        executor.Apply(plan, TestContext.Current.CancellationToken);

        Assert.Equal(0, reconcileSignal.RequestCount);
    }

    [Fact]
    public void WindowVanishingBetweenTheMoveAndTheVerifyReadStillCountsAsMoved()
    {
        // No SetGeometry call -- TryReadGeometry returns false on the verify-after-move read, a
        // routine race (the window was destroyed between the move and this read).
        var system = new FakePlacementSystem();
        HWND hwnd = new(1);
        var windowId = WindowId.FromOpaqueValue(1);
        system.SetHwnd(windowId, hwnd);
        PlacementExecutor executor = CreateExecutor(system);

        ImmutableArray<PlacementInstruction> plan = [PlacementInstruction.Move(windowId, s_target)];
        PlacementOutcome outcome = Assert.Single(executor.Apply(plan, TestContext.Current.CancellationToken));

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
}
