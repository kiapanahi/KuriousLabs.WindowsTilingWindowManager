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
    // this finally block at shutdown.
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
                    await pipe.DisposeAsync().ConfigureAwait(false);
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
            foreach (NamedPipeServerStream subscriber in _subscribers.Keys)
            {
                await subscriber.DisposeAsync().ConfigureAwait(false);
            }

            _subscribers.Clear();
        }

        LogStopped();
    }

    /// <inheritdoc/>
    public async Task PublishAsync(IpcReply reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reply);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(reply, IpcJsonContext.Default.IpcReply);
        foreach (NamedPipeServerStream subscriber in _subscribers.Keys)
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
