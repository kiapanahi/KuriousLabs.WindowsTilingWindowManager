using Bastion.Core;

namespace Bastion.Win32.Tests;

/// <summary>
/// Trivial <see cref="ILayoutEngine"/> test double for <see cref="ReconcileNowSignalTests"/>/
/// <see cref="ReconcilerIntentPumpTests"/> — a <see cref="Reconciler"/> constructor dependency
/// those tests never actually invoke (no workspace is registered, so
/// <see cref="Reconciler.ConvergeOnceAsync"/> never reaches a <see cref="ILayoutEngine.Solve"/>
/// call), but the constructor still requires a non-null instance.
/// </summary>
internal sealed class FakeLayoutEngine : ILayoutEngine
{
    public IReadOnlyList<WindowPlacement> Solve(
        IReadOnlyList<WindowId> windows,
        Rect workArea,
        LayoutConstraints constraints,
        LayoutGaps gaps) =>
        [];
}
