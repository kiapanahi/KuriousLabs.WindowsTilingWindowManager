using System.Collections.Immutable;
using Bastion.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>
/// Exercises <see cref="Reconciler.PlacementPlanReader"/> — the GitHub issue #10 composition-root
/// hand-off seam a Win32-side placement-execution pump drains to feed the Placement Executor. See
/// <see cref="Reconciler"/>'s own remarks ("Placement-plan hand-off") for why this is a channel
/// rather than a change to <see cref="Reconciler.RunAsync"/>'s own signature.
/// </summary>
public sealed class ReconcilerPlacementPlanPublishingTests
{
    private static readonly Rect s_workArea = new(0, 0, 1920, 1080);

    [Fact]
    public async Task AConvergencePassThatProducesANonEmptyPlanPublishesItOnPlacementPlanReader()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);

        // FakeLayoutEngine always targets s_workArea; an observed rect that never matches it forces
        // a Move instruction on every pass -- the same "never converging" trick ReconcilerTests'
        // own reassert-budget tests already use.
        var neverConvergingRect = new Rect(0, 0, 10, 10);
        windowSystem.Windows.Add(new ObservedWindow(windowId, neverConvergingRect, neverConvergingRect, IsCloaked: false, IsIconic: false, IsZoomed: false));

        ImmutableArray<PlacementInstruction> produced = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.False(produced.IsEmpty);

        Assert.True(reconciler.PlacementPlanReader.TryRead(out ImmutableArray<PlacementInstruction> published));
        Assert.Equal(produced, published);
    }

    [Fact]
    public async Task AConvergencePassThatProducesAnEmptyPlanPublishesNothing()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());

        // No workspace registered and no windows observed -- ConvergeOnceAsync's own plan is
        // necessarily empty (nothing to solve), matching
        // ObservedStateAndPlacementPlanAreNeverDefaultEvenBeforeAnyConvergencePass's own baseline.
        ImmutableArray<PlacementInstruction> produced = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Empty(produced);

        Assert.False(reconciler.PlacementPlanReader.TryRead(out _));
    }

    [Fact]
    public async Task AConvergedWindowThatStopsProducingMovesPublishesNothingOnALaterPass()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var windowId = WindowId.FromOpaqueValue(1);

        // Observed rect already matches exactly what FakeLayoutEngine will solve for -- already
        // converged, so this pass's plan is empty and nothing should be published.
        windowSystem.Windows.Add(new ObservedWindow(windowId, s_workArea, s_workArea, IsCloaked: false, IsIconic: false, IsZoomed: false));

        ImmutableArray<PlacementInstruction> produced = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Empty(produced);
        Assert.False(reconciler.PlacementPlanReader.TryRead(out _));
    }

    [Fact]
    public async Task ANewerUnreadPlanSupersedesAnOlderOneRatherThanQueuingBehindIt()
    {
        // BoundedChannelFullMode.DropOldest (verified against
        // https://learn.microsoft.com/dotnet/api/system.threading.channels.boundedchannelfullmode):
        // "Removes and ignores the oldest item in the channel in order to make room for the item
        // being written." Reconciler's own remarks document this as deliberate: the newest
        // convergence result always supersedes an older, not-yet-applied one.
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time);
        reconciler.SetWorkspace(WorkspaceKey.Default, s_workArea);
        var firstWindowId = WindowId.FromOpaqueValue(1);
        var secondWindowId = WindowId.FromOpaqueValue(2);

        var neverConvergingRect = new Rect(0, 0, 10, 10);
        windowSystem.Windows.Add(new ObservedWindow(firstWindowId, neverConvergingRect, neverConvergingRect, IsCloaked: false, IsIconic: false, IsZoomed: false));

        // Two passes back-to-back, neither drained from PlacementPlanReader in between. A second
        // window is added only between passes, so the two plans are genuinely different in content
        // (one vs. two Move instructions) -- not merely two distinct ImmutableArray<T> instances
        // wrapping equal content, which xUnit's Assert.NotEqual would already treat as equal via its
        // own sequence-equality comparison for IEnumerable<T> (ImmutableArray<T>.Equals's own
        // reference-equality-of-the-backing-array semantics do not apply here).
        ImmutableArray<PlacementInstruction> first = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        windowSystem.Windows.Add(new ObservedWindow(secondWindowId, neverConvergingRect, neverConvergingRect, IsCloaked: false, IsIconic: false, IsZoomed: false));
        ImmutableArray<PlacementInstruction> second = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);
        Assert.Single(first);
        Assert.Equal(2, second.Length);

        Assert.True(reconciler.PlacementPlanReader.TryRead(out ImmutableArray<PlacementInstruction> published));
        Assert.Equal(second, published); // the newer, larger plan survived, not the first.
        Assert.False(reconciler.PlacementPlanReader.TryRead(out _)); // nothing queued behind it.
    }

    [Fact]
    public void DisposeCompletesThePlacementPlanReaderSoADrainingConsumerCanExitCleanly()
    {
        var reconciler = new Reconciler(new FakeWindowSystem(), new FakeLayoutEngine(), new FakeTimeProvider());

        reconciler.Dispose();

        Assert.True(reconciler.PlacementPlanReader.Completion.IsCompleted);
    }
}
