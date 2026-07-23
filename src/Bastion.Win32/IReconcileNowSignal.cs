namespace Bastion.Win32;

/// <summary>
/// Seam for telling the (not-yet-implemented) Reconciler to run an immediate convergence pass
/// instead of waiting for its next periodic heartbeat. See DESIGN.md §3.1/§3.4: "Queue overflow is
/// not an error: it sets a reconcile-now flag and drops deltas — the Reconciler recovers from
/// authoritative reads."
/// </summary>
/// <remarks>
/// Two <c>itemDropped</c> callbacks call this today: the WinEvent ingest channel's
/// (docs/engineering/concurrency-performance.md §1, <see cref="WinEventChannelFactory"/>, GitHub
/// issue #1) and the Coalescer's own coalesced-intent channel's (<see cref="Coalescer"/>, GitHub
/// issue #2 — the identical overflow/data-loss shape justifies the identical recovery signal, per
/// DESIGN.md §3.4's "distrust escalation" trigger list, which names "queue overflow" generally, not
/// only the ingest channel's). No production implementation exists yet — the Reconciler (GitHub
/// issue #4) will supply one. This interface is the minimal constructor-injected seam both
/// <see cref="WinEventPumpService"/> and <see cref="Coalescer"/> depend on so neither needs to
/// change shape when that lands; tests supply a trivial fake (<c>FakeReconcileNowSignal</c>).
/// </remarks>
internal interface IReconcileNowSignal
{
    /// <summary>Requests an out-of-band reconciliation pass at the next opportunity.</summary>
    void RequestReconcileNow();
}
