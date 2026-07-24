using System.Collections.Immutable;
using Bastion.Core;
using Bastion.Layout;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>
/// Exercises the depth-cap acceptance criterion (docs/engineering/daemon-architecture.md §6, the
/// <c>pure-core</c> skill) end to end through <see cref="Reconciler"/>, using the real, shipped
/// <see cref="DwindleLayoutEngine"/> — <see cref="ReconcilerTests"/>' own <c>FakeLayoutEngine</c>
/// has no recursion at all, so it cannot exercise this.
/// </summary>
/// <remarks>
/// <see cref="Reconciler.ConvergeOnceAsync"/>'s own diffing is flat, not tree-recursive (see that
/// type's remarks) — every loop it runs is over a flat <see cref="ImmutableArray{T}"/>. The one
/// place actual recursion occurs in the pipeline it invokes is inside
/// <see cref="ILayoutEngine.Solve"/> for <see cref="DwindleLayoutEngine"/>, which internally builds
/// and solves a <c>SplitTree</c> via <c>SplitTreeLayout.Solve</c> — already bounded by
/// <c>SplitTree.MaxDepth</c> (1024, <see langword="internal"/> to <c>Bastion.Layout</c> and not
/// referenced by literal value here), a hard, config-independent, throwing cap from GitHub issue
/// #37. <see cref="DwindleLayoutEngine.Solve"/> additionally pre-empts ever reaching that throw
/// path itself: once the window count alone would exceed <c>MaxDepth + 1</c>, it degrades to a
/// flat stacked layout instead of attempting the chain-shaped tree build that would otherwise
/// approach the cap (see its own remarks) — the test below exercises exactly that degrade-not-crash
/// path, reachable end to end from the Reconciler, rather than re-testing <c>SplitTree</c>'s own
/// throw behavior a second time (already covered directly by <c>SplitTreeTests</c>/
/// <c>DwindleLayoutEngineTests</c> in <c>Bastion.Layout.Tests</c>).
/// </remarks>
public sealed class ReconcilerDepthCapTests
{
    [Fact]
    public async Task ConvergingAWorkspaceWithMoreWindowsThanTheSplitTreeDepthCapDoesNotThrow()
    {
        var windowSystem = new FakeWindowSystem();
        using var reconciler = new Reconciler(windowSystem, new DwindleLayoutEngine(), new FakeTimeProvider());
        var workArea = new Rect(0, 0, 1920, 1080);
        reconciler.SetWorkspace(WorkspaceKey.Default, workArea);

        // SplitTree.MaxDepth is 1024; DwindleLayoutEngine.Solve degrades to a flat stack once the
        // window count alone exceeds MaxDepth + 1 -- comfortably past that threshold here.
        const int WindowCount = 1024 + 6;
        for (var i = 0; i < WindowCount; i++)
        {
            var id = WindowId.FromOpaqueValue((ulong)(i + 1));

            // An observed rect that will never match any solved placement (zero-sized, at the
            // origin), so every window genuinely needs a Move instruction -- proving the plan is
            // real output, not an artifact of everything already matching by coincidence.
            windowSystem.Windows.Add(new ObservedWindow(id, default, default, IsCloaked: false, IsIconic: false, IsZoomed: false));
        }

        ImmutableArray<PlacementInstruction> plan = await reconciler.ConvergeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WindowCount, plan.Length);
        Assert.All(plan, instruction => Assert.Equal(PlacementAction.Move, instruction.Action));
    }
}
