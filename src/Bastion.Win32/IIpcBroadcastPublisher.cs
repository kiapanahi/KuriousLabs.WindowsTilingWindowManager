using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// Fans an <see cref="IpcReply"/> out to every currently-connected subscriber on the broadcast
/// pipe (DESIGN.md §3.9). Implemented by <see cref="IpcBroadcastServerPump"/>; a seam so a future
/// in-process producer (the Workspace Manager, GitHub issue #15; a future Reconciler state-change
/// hook) can depend on this abstraction rather than the concrete pump type, and so tests can fake
/// it without standing up real named pipes.
/// </summary>
/// <remarks>
/// No production code calls <see cref="PublishAsync"/> yet — this issue's job is the transport
/// (the accept loop, the fan-out mechanism, and proving it with a real two-subscriber test), not
/// wiring a first real broadcast producer. Mirrors <c>HwndJournalWriter</c>'s own "real component,
/// no live production caller yet" precedent (its own remarks): the deliverable is the tested
/// component and its contract, not every future call site.
/// </remarks>
internal interface IIpcBroadcastPublisher
{
    /// <summary>Serializes <paramref name="reply"/> once and writes it to every connected subscriber, pruning any that have disconnected.</summary>
    Task PublishAsync(IpcReply reply, CancellationToken cancellationToken);
}
