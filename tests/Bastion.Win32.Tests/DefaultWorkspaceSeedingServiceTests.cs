using Bastion.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="DefaultWorkspaceSeedingService"/> — the Codex review finding fix (GitHub
/// issue #10) proving <see cref="Reconciler.SetWorkspace"/> actually gets called with the primary
/// monitor's work area before reconciliation begins.
/// </summary>
public sealed class DefaultWorkspaceSeedingServiceTests
{
    [Fact]
    public async Task StartAsyncSeedsTheDefaultWorkspaceWithThePrimaryWorkArea()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        var placementSystem = new FakePlacementSystem { PrimaryWorkArea = new Rect(0, 0, 1920, 1040) };
        var service = new DefaultWorkspaceSeedingService(reconciler, placementSystem);

        Assert.False(reconciler.DesiredState.Workspaces.ContainsKey(WorkspaceKey.Default));

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(reconciler.DesiredState.Workspaces.ContainsKey(WorkspaceKey.Default));
        Assert.Equal(placementSystem.PrimaryWorkArea, reconciler.DesiredState.Workspaces[WorkspaceKey.Default].WorkArea);

        // Reconciler's own desired-window-set sync auto-admitting a newly-observed window once
        // WorkspaceKey.Default exists is already thoroughly covered by Bastion.Core.Tests'
        // ReconcilerTests (e.g. NewlyObservedWindowIsAutoAdmittedIntoTheDefaultWorkspace) against
        // its own richer FakeWindowSystem -- this test's job stops at proving
        // DefaultWorkspaceSeedingService itself calls SetWorkspace with the right work area; this
        // project's own FakeWindowSystem is deliberately minimal (see its own remarks) and not the
        // right place to re-prove Reconciler's admission logic a second time.
    }

    [Fact]
    public async Task StopAsyncCompletesWithoutDoingAnything()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), new FakeTimeProvider());
        var service = new DefaultWorkspaceSeedingService(reconciler, new FakePlacementSystem());

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(reconciler.DesiredState.Workspaces.ContainsKey(WorkspaceKey.Default));
    }
}
