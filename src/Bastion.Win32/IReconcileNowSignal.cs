namespace Bastion.Win32;

/// <summary>
/// Seam for telling the (not-yet-implemented) Reconciler to run an immediate convergence pass
/// instead of waiting for its next periodic heartbeat. See DESIGN.md §3.1/§3.4: "Queue overflow is
/// not an error: it sets a reconcile-now flag and drops deltas — the Reconciler recovers from
/// authoritative reads."
/// </summary>
/// <remarks>
/// The WinEvent ingest channel's <c>itemDropped</c> callback
/// (docs/engineering/concurrency-performance.md §1, <see cref="WinEventChannelFactory"/>) is the
/// sole caller today. No production implementation exists yet — the Reconciler (GitHub issue #4)
/// will supply one. This interface is the minimal constructor-injected seam
/// <see cref="WinEventPumpService"/> depends on so it does not need to change shape when that
/// lands; tests supply a trivial fake (<c>FakeReconcileNowSignal</c>).
/// </remarks>
internal interface IReconcileNowSignal
{
    /// <summary>Requests an out-of-band reconciliation pass at the next opportunity.</summary>
    void RequestReconcileNow();
}
