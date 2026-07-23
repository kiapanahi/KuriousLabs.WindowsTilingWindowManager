using System.Threading.Channels;

namespace Bastion.Win32;

/// <summary>
/// Constructs the WinEvent ingest channel with the exact configuration
/// docs/engineering/concurrency-performance.md §1 mandates. Factored out of
/// <see cref="WinEventPumpService"/> so the channel's overflow/drop contract is independently
/// unit-testable (see <c>WinEventChannelFactoryTests</c>) without starting the pump thread or
/// touching Win32 at all.
/// </summary>
internal static class WinEventChannelFactory
{
    /// <summary>
    /// Fixed ingest capacity per DESIGN.md §3.1 / concurrency-performance.md §1 — not currently
    /// config-tunable; the Reconciler's authoritative-read recovery is what makes a fixed bound
    /// safe rather than something that needs runtime adjustment.
    /// </summary>
    private const int Capacity = 4096;

    /// <summary>
    /// Creates the bounded WinEvent ingest channel: capacity <c>4096</c>, exactly one writer (the
    /// WinEvent pump thread) and one reader (the future Coalescer), via the explicit
    /// <see cref="BoundedChannelOptions"/> constructor — never the
    /// <see cref="Channel.CreateBounded{T}(int)"/> convenience overload, which silently defaults
    /// <see cref="BoundedChannelFullMode.Wait"/> and would block the OS-callback-driven writer.
    /// </summary>
    /// <param name="reconcileNowSignal">
    /// Invoked synchronously, on the writer's thread, exactly when an item is dropped because the
    /// channel is full — the literal mechanism behind DESIGN.md §3.1/§3.4's "queue overflow sets a
    /// reconcile-now flag" design. The callback body here is trivial (a single interface call),
    /// satisfying concurrency-performance.md §1's "must be trivial and allocation-free" rule for
    /// code that runs inline on the OS-callback-adjacent pump thread.
    /// </param>
    public static Channel<WinEvent> CreateIngestChannel(IReconcileNowSignal reconcileNowSignal)
    {
        ArgumentNullException.ThrowIfNull(reconcileNowSignal);

        // FullMode: DropWrite, not DropOldest — both are correctness-equivalent here (dropped
        // deltas are always recovered by the Reconciler's 5 s heartbeat / authoritative re-sync,
        // concurrency-performance.md §1), but DropWrite sacrifices only the *incoming* item, so a
        // shed arrival never mutates the shape of what the Coalescer is about to drain and exactly
        // one itemDropped invocation happens per shed event — simplest to reason about and test.
        var options = new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleWriter = true,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        };

        return Channel.CreateBounded<WinEvent>(options, itemDropped: _ => reconcileNowSignal.RequestReconcileNow());
    }
}
