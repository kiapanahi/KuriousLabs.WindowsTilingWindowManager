using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Bastion.Core;
using Microsoft.Extensions.Hosting;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Bridges the Coalescer's coalesced-intent stream to the Reconciler — DESIGN.md §3.4's "(1)
/// coalesced intents" convergence trigger.
/// </summary>
/// <remarks>
/// <para>
/// Per DESIGN.md §1 ("reads are truth, events are scheduling hints"), every convergence pass
/// re-derives ground truth from <see cref="IWindowSystem.ReadAllAsync"/> regardless of which
/// trigger fired it — so for convergence purposes this bridge's only job is <em>timing</em>: wake
/// the Reconciler's convergence loop sooner than its next heartbeat tick via
/// <see cref="Reconciler.RequestReconcileNow"/>.
/// </para>
/// <para>
/// <b><see cref="WindowVanished"/> is the one exception, for registry hygiene, not convergence.</b>
/// DESIGN.md §3.3/§5: "entries are purged only on <c>EVENT_OBJECT_DESTROY</c> — never by
/// <c>IsWindow</c> polling," and <see cref="WindowVanished"/>'s own doc comment already names this
/// as its purpose. This class is the first (and, before this fix, the only) production consumer of
/// the Coalescer's intent stream, so purging is this bridge's job: without it, a destroyed window's
/// stale <see cref="WindowRegistry"/> entry survives indefinitely, and if the OS later recycles its
/// <c>HWND</c> to a genuinely different window in the <em>same</em> process,
/// <see cref="WindowRegistry.TryAdmitAsync"/>'s own PID-match "already registered" check (its own
/// remarks) wrongly hands the new window the old, stale <see cref="WindowId"/> — identity, layout
/// position, and reassert-budget state all carried over incorrectly (Codex review finding on this
/// PR). Every other intent kind still carries no payload-specific handling at all.
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
internal sealed class ReconcilerIntentPump(
    ChannelReader<CoalescedIntent> intentReader,
    WindowRegistry registry,
    Reconciler reconciler) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (CoalescedIntent intent in intentReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (intent is WindowVanished vanished)
            {
                // The one payload-specific action this bridge takes -- see this type's remarks.
                // HWND has no implicit nint conversion in this direction (only HWND -> nint,
                // used elsewhere e.g. WinEventPumpService's OnWinEvent) -- confirmed by the build.
                registry.Purge(new HWND(vanished.Hwnd));
            }

            reconciler.RequestReconcileNow();
        }
    }
}
