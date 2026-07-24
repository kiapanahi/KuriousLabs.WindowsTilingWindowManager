using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// The real, production <see cref="IReconcileNowSignal"/> — GitHub issue #4 resolves the TODO both
/// <see cref="WinEventPumpService"/>'s and <see cref="Coalescer"/>'s own doc comments left ("the
/// Reconciler ... will supply one").
/// </summary>
/// <remarks>
/// A thin, same-assembly forward to <see cref="Reconciler.RequestReconcileNow"/> — not the
/// Reconciler itself — because <see cref="IReconcileNowSignal"/> is <see langword="internal"/> to
/// <c>Bastion.Win32</c> and <c>Bastion.Core</c> neither references this assembly nor could
/// implement one of its internal interfaces if it did (DESIGN.md §3/§10's purity boundary is
/// one-directional: <c>Bastion.Win32</c> depends on <c>Bastion.Core</c>, never the reverse). This
/// class is that necessary seam, not a workaround.
/// </remarks>
internal sealed class ReconcileNowSignal(Reconciler reconciler) : IReconcileNowSignal
{
    /// <inheritdoc/>
    public void RequestReconcileNow() => reconciler.RequestReconcileNow();
}
