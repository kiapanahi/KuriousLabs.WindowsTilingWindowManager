using Bastion.Core;

namespace Bastion.Core.Tests;

/// <summary>
/// Deterministic <see cref="ILayoutEngine"/> test double: places every window at exactly
/// <paramref name="workArea"/> (ignoring gaps/constraints entirely), so Reconciler tests can assert
/// on an exact, hand-computed target rect without depending on any real engine's split arithmetic.
/// <c>DwindleLayoutEngine</c> (the real, shipped engine) is used only by
/// <c>ReconcilerDepthCapTests</c>, where its actual <c>SplitTree</c>-backed recursion is the thing
/// under test.
/// </summary>
internal sealed class FakeLayoutEngine : ILayoutEngine
{
    public IReadOnlyList<WindowPlacement> Solve(
        IReadOnlyList<WindowId> windows,
        Rect workArea,
        LayoutConstraints constraints,
        LayoutGaps gaps) =>
        windows.Select(id => new WindowPlacement(id, workArea)).ToList();
}
