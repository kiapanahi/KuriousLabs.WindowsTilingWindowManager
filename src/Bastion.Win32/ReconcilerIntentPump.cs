using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Bastion.Core;
using Microsoft.Extensions.Hosting;

namespace Bastion.Win32;

/// <summary>
/// Bridges the Coalescer's coalesced-intent stream to the Reconciler — DESIGN.md §3.4's "(1)
/// coalesced intents" convergence trigger.
/// </summary>
/// <remarks>
/// <para>
/// Per DESIGN.md §1 ("reads are truth, events are scheduling hints"), every convergence pass
/// re-derives ground truth from <see cref="IWindowSystem.ReadAllAsync"/> regardless of which
/// trigger fired it — so this bridge's only job is <em>timing</em>: wake the Reconciler's
/// convergence loop sooner than its next heartbeat tick via <see cref="Reconciler.RequestReconcileNow"/>.
/// It never inspects, translates, or acts on an intent's own payload (kind, <c>Hwnd</c>) — doing so
/// would require resolving a raw <c>Hwnd</c> to a <see cref="WindowId"/> here and handing that back
/// to a Core-safe caller, which <see cref="WindowSystemAdapter.ReadAllAsync"/>'s own admission pass
/// already does, sequentially, for every currently-visible window on the very next convergence
/// pass this class triggers.
/// </para>
/// <para>
/// Hosting shape matches <see cref="Coalescer"/>'s own: a <see cref="BackgroundService"/>, not a
/// raw <see cref="IHostedService"/> + dedicated <see cref="Thread"/> — <see cref="ExecuteAsync"/>
/// is an ordinary <see langword="await foreach"/> channel-drain loop with no message pump or hook
/// registration of its own (docs/engineering/daemon-architecture.md §2).
/// </para>
/// <para>
/// Not yet wired into the composition root (<c>Bastion.Daemon</c>) — that is GitHub issue #10; this
/// type is constructed directly by tests today via <c>InternalsVisibleTo</c>, matching
/// <see cref="Coalescer"/>/<see cref="WinEventPumpService"/>'s own established pattern.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered with AddHostedService<ReconcilerIntentPump>() once Bastion.Daemon's " +
        "composition root is wired (GitHub issue #10) — not yet wired as of this change. Same " +
        "documented CA1812 false-positive shape as Coalescer/WinEventPumpService/BastiondService.")]
internal sealed class ReconcilerIntentPump(ChannelReader<CoalescedIntent> intentReader, Reconciler reconciler) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (CoalescedIntent _ in intentReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            reconciler.RequestReconcileNow();
        }
    }
}
