using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text.Json;
using Bastion.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Win32;

/// <summary>
/// The broadcast state-subscription pipe's accept loop (DESIGN.md §3.9, GitHub issue #12) and
/// <see cref="IIpcBroadcastPublisher"/> implementation — fans an <see cref="IpcReply"/> out to
/// every currently-connected subscriber (<c>bastion-bar</c>, a future <c>bastionc --watch</c>
/// session).
/// </summary>
/// <remarks>
/// <para>
/// <b>One connected pipe instance per subscriber, not per request.</b> Unlike the command pipe
/// (one short-lived connection per <c>bastionc</c> invocation), a broadcast subscriber connects
/// once and stays connected — this pump's "servicing" of a newly-accepted connection is simply
/// registering the stream in <see cref="_subscribers"/>; there is no request to read at all.
/// The pipe is <see cref="PipeDirection.Out"/>-only (the server perspective) since subscribers
/// never send anything back over this pipe (DESIGN.md §3.9 describes it as one-directional
/// state broadcast); a subscriber that wants to issue a command still uses the separate command
/// pipe (<see cref="IpcCommandServerPump"/>).
/// </para>
/// <para>
/// <b>Disconnected subscribers are pruned lazily, on the next failed write, not proactively
/// detected.</b> Detecting a dead subscriber immediately would require either a dedicated
/// per-subscriber background read loop (this pipe direction doesn't even support reading) or an
/// <c>InOut</c> direction plus a throwaway pending read purely to observe an eventual
/// end-of-stream/broken-pipe signal — real complexity this repo's "never optimize/complicate a
/// path without a proven need" posture argues against for a broadcast fan-out whose only
/// consequence of a briefly-stale subscriber entry is one wasted write attempt on the next
/// publish. <see cref="PublishAsync"/> already prunes on exactly that failed write.
/// </para>
/// <para>
/// Hosting shape, accept-loop structure, and the cancellation-vs-disconnect distinction at the
/// accept step all mirror <see cref="IpcCommandServerPump"/> exactly — see that type's remarks
/// for the full reasoning (<see cref="BackgroundService"/> as a pure managed drain loop per
/// docs/engineering/concurrency-performance.md §2; "listen on N, spin up N+1" before servicing the
/// connection just accepted).
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Registered as both IHostedService and IIpcBroadcastPublisher via " +
        "Bastion.Daemon's composition root (GitHub issues #11/#12), mirroring " +
        "WinEventPumpService/Coalescer's own dual concrete + interface registration shape.")]
internal sealed partial class IpcBroadcastServerPump(ILogger<IpcBroadcastServerPump> logger) : BackgroundService, IIpcBroadcastPublisher
{
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _subscribers = new();

    /// <summary>
    /// Test-observable: how many broadcast subscribers are currently registered. Exposed only so
    /// tests can wait for the accept loop to have actually registered a just-connected subscriber
    /// (connecting only guarantees the OS-level handshake completed, not that this pump's own loop
    /// iteration has run yet) without reaching into private state via reflection -- mirrors
    /// <c>WinEventPumpService.IsPumpThreadAlive</c>'s identical rationale.
    /// </summary>
    internal int SubscriberCount => _subscribers.Count;

    /// <inheritdoc/>
    // CA2000 flags each CreatePipe() call below because, on the success path, ownership of the
    // created NamedPipeServerStream transfers into the `_subscribers` dictionary rather than
    // being disposed in the same scope -- a shape its intraprocedural dataflow analysis cannot
    // verify eventually disposes the object (that happens later, in this method's own `finally`
    // block, via the `foreach` over `_subscribers.Keys`). Every path is genuinely covered: the
    // accept-failure catch disposes the failed instance directly; a successfully-accepted
    // instance is disposed exactly once, either by PublishAsync's prune-on-failed-write or by
    // this finally block's `_subscribers.Keys` loop at shutdown; and the still-listening,
    // not-yet-connected instance current when the loop exits -- by either exit path -- is
    // disposed by this finally block's own unconditional first statement (see the remarks there).
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of every CreatePipe() result transfers into _subscribers " +
            "(disposed by this method's own finally block or PublishAsync's prune-on-failure) " +
            "or is disposed directly in the accept-failure catch -- see this method's own remarks.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(IpcPipeNames.Broadcast);

        NamedPipeServerStream pipe = CreatePipe();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    LogAcceptFailed(ex);
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = CreatePipe();
                    continue;
                }

                // "Listen on N, spin up N+1" -- start the next instance before registering
                // (this pipe's equivalent of "servicing") the one just accepted.
                NamedPipeServerStream connected = pipe;
                pipe = CreatePipe();
                _subscribers[connected] = 0;
                LogSubscriberConnected();
            }
        }
        finally
        {
            // Unconditional, and first: covers whichever `pipe` value is current regardless of
            // which of the loop's two exit paths was taken. The OperationCanceledException catch
            // above only handles one of them -- the loop's own condition
            // (`!stoppingToken.IsCancellationRequested`) can itself go false immediately after a
            // connection was accepted and registered (`pipe` already reassigned to the next
            // listening instance via "listen on N, spin up N+1"), exiting the loop at the top
            // without ever entering that catch at all. Left undisposed, that instance would never
            // appear in `_subscribers` (only `connected` is registered there) and would leak.
            // Mirrors IpcCommandServerPump.ExecuteAsync's identical unconditional
            // `await pipe.DisposeAsync()` in its own finally block.
            await pipe.DisposeAsync().ConfigureAwait(false);

            foreach (NamedPipeServerStream subscriber in _subscribers.Keys)
            {
                await subscriber.DisposeAsync().ConfigureAwait(false);
            }

            _subscribers.Clear();
        }

        LogStopped();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Fans out to every subscriber concurrently, not one at a time.</b> Each subscriber's
    /// write-or-prune runs as its own <see cref="Task"/> (<see cref="WriteOrPruneAsync"/>), and
    /// this method awaits all of them together via <see cref="Task.WhenAll(IEnumerable{Task})"/>.
    /// The per-subscriber pipe has a finite (4096-byte) write quota (<see cref="CreatePipe"/>'s
    /// own remarks), so a subscriber that stops reading -- a frozen/crashed <c>bastion-bar</c>, a
    /// suspended <c>bastionc --watch</c> session -- eventually blocks its own write until either
    /// it drains or <paramref name="cancellationToken"/> cancels (`CreateNamedPipeW`'s documented
    /// remarks, already cited on both pumps' <c>CreatePipe</c> methods). A strictly sequential
    /// <see langword="foreach"/> over <see cref="_subscribers"/> would let that one stuck
    /// subscriber's blocked write delay delivery to every subscriber enumerated after it; running
    /// the writes concurrently means the healthy subscribers' writes complete independently of the
    /// stuck one instead of queuing behind it -- the same "a slow/frozen client must not couple
    /// back into daemon latency" hazard docs/engineering/json-ipc-config.md §5 forbids for
    /// synchronous pipe I/O on a pump thread, applied here to async I/O serialized by a loop.
    /// This does not, by itself, stop the composite <see cref="Task"/> this method returns from
    /// waiting on the stuck subscriber too -- a fuller per-subscriber bounded-queue decoupling
    /// (so this method's own return never blocks on the slowest subscriber) would fix that, but is
    /// not yet justified for a v0.1 primitive with no production caller (see
    /// <see cref="IIpcBroadcastPublisher"/>'s own remarks).
    /// </remarks>
    public Task PublishAsync(IpcReply reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reply);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(reply, IpcJsonContext.Default.IpcReply);
        return Task.WhenAll(_subscribers.Keys.Select(subscriber => WriteOrPruneAsync(subscriber, payload, cancellationToken)));
    }

    /// <summary>Writes <paramref name="payload"/> to <paramref name="subscriber"/>, pruning it from <see cref="_subscribers"/> on a failed write.</summary>
    private async Task WriteOrPruneAsync(NamedPipeServerStream subscriber, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            await IpcFraming.WriteFrameAsync(subscriber, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The subscriber went away since it last connected -- see this type's remarks for
            // why detection is lazy, on this exact failed-write path, rather than proactive.
            LogSubscriberLost(ex);
            if (_subscribers.TryRemove(subscriber, out _))
            {
                await subscriber.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // An explicit, nonzero outBufferSize is load-bearing, not a tuning knob: the 5-parameter
    // NamedPipeServerStream constructor (no buffer-size parameters) reserves effectively no
    // output quota up front for a PipeDirection.Out-only pipe, and CreateNamedPipeW's own
    // documented remarks state a write that exceeds the remaining quota "will block until the
    // data is read from the pipe" (https://learn.microsoft.com/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew#remarks).
    // Empirically (this issue's own test suite), that meant PublishAsync's first WriteFrameAsync
    // call to a freshly-connected subscriber hung indefinitely whenever the subscriber had not
    // already posted a matching ReadAsync -- exactly the shape a real bastion-bar subscriber
    // reconnecting/idling between messages would hit, not just a test-ordering artifact. 4096
    // bytes comfortably buffers any real IpcReply (a StatusReply is well under 200 bytes) without
    // requiring a subscriber to have a read already pending, matching the buffer size Microsoft's
    // own named-pipe samples (e.g. the Multithreaded Pipe Server sample) use by convention. The
    // command pipe (IpcCommandServerPump.CreatePipe) carries the identical explicit buffer sizing
    // for the same reason, even though its own request/reply ordering was never observed to hang
    // in this suite -- see that method's own remarks for why that ordering is a favorable
    // scheduling coincidence, not a structural guarantee.
    private static NamedPipeServerStream CreatePipe() => new(
        IpcPipeNames.Broadcast,
        PipeDirection.Out,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: 0,
        outBufferSize: 4096);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC broadcast server pump started on pipe '{PipeName}'.")]
    private partial void LogStarted(string pipeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC broadcast server pump stopped.")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Debug, Message = "A broadcast subscriber connected.")]
    private partial void LogSubscriberConnected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accepting a broadcast subscriber connection failed; replacing the pipe instance and continuing.")]
    private partial void LogAcceptFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A broadcast subscriber was pruned after a failed publish write.")]
    private partial void LogSubscriberLost(Exception exception);
}
