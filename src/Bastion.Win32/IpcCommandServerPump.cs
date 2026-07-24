using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text.Json;
using Bastion.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Win32;

/// <summary>
/// The request/reply command pipe's accept loop (DESIGN.md §3.9, GitHub issue #12) — the
/// "listen on N, spin up N+1" pattern docs/engineering/json-ipc-config.md §4 specifies, serving
/// concurrent <c>bastionc</c> invocations against a single named pipe name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosting shape: <see cref="BackgroundService"/>, not a raw <see cref="IHostedService"/> +
/// dedicated <see cref="Thread"/>.</b> Named pipes are plain, portable BCL I/O
/// (<see cref="System.IO.Pipes"/>) — this loop is an ordinary <see langword="await"/>-based
/// channel-shaped drain with no <c>GetMessage</c> pump or STA-affine COM call of its own, the exact
/// "pure managed drain loop" carve-out docs/engineering/concurrency-performance.md §2 describes,
/// matching <see cref="ReconcilerLoopService"/>/<see cref="PlacementExecutionPump"/>/
/// <see cref="ReconcilerIntentPump"/>'s identical reasoning. It still runs as its own dedicated
/// pump per docs/engineering/concurrency-performance.md's standing rule ("never inline in the
/// Reconciler") — registered as its own <see cref="IHostedService"/>, never invoked from another
/// pump's loop body.
/// </para>
/// <para>
/// <b>Concurrent connection servicing, deliberately.</b> "The command pipe must serve concurrent
/// <c>bastionc</c> invocations" (json-ipc-config.md §4) and the acceptance criterion that a new
/// server instance starts <em>before</em> the just-accepted connection is serviced both require
/// the accept loop to never block on servicing one connection before accepting the next. Each
/// accepted connection is therefore serviced on its own <see cref="Task"/>
/// (<see cref="ServiceConnectionAsync"/>), tracked via a fault-only <see cref="Task.ContinueWith(Action{Task,object?},object?,CancellationToken,TaskContinuationOptions,TaskScheduler)"/>
/// continuation rather than a bare discarded <c>_ = ...</c> — docs/engineering/daemon-architecture.md
/// §6: "every genuinely fire-and-forget <see cref="Task"/> in the daemon ... must have an explicit
/// owner/tracker that observes its result, logs failure." In practice
/// <see cref="ServiceConnectionAsync"/> never actually throws (its own catch-all swallows and logs
/// everything short of a corrupted-process-state exception) — this continuation is defense-in-depth
/// against a bug in that catch-all itself, not the primary error path.
/// </para>
/// <para>
/// <b>A single connection's failure never propagates to the accept loop, by construction.</b>
/// <see cref="ServiceConnectionAsync"/>'s own catch-all (logged, not rethrown) is a deliberate,
/// reasoned <em>deviation</em> from <see cref="ReconcilerLoopService"/>/<see cref="PlacementExecutionPump"/>'s
/// "let a genuine bug propagate and stop the host" default: those two guard the core reconciliation
/// pipeline, where an unanticipated exception signals the pipeline itself is broken. This loop
/// instead services requests arriving from other, unprivileged, same-user processes over an IPC
/// boundary — untrusted input in the ordinary security sense, even though <c>PipeOptions.CurrentUserOnly</c>
/// scopes who can connect at all. One malformed or unlucky <c>bastionc</c> invocation must never be
/// able to take <c>bastiond</c> down with it (DESIGN.md §1's "must never strand a window"), so a
/// per-connection failure is logged and the loop moves on — mirroring
/// <c>JournalRestoreOnShutdownService</c>'s own <c>catch (Exception ex) when (ex is not OperationCanceledException)</c>
/// precedent for the identical "a diagnostic/servicing concern must not be allowed to take the host
/// down" reasoning.
/// </para>
/// <para>
/// <b>Cancellation vs. disconnect, at both the accept step and the per-connection step.</b> Per
/// json-ipc-config.md §4: an <see cref="OperationCanceledException"/> from a canceled
/// <paramref name="stoppingToken"/> is expected, cooperative shutdown; an <see cref="IOException"/>
/// ("the pipe is broken") or <see cref="InvalidOperationException"/> (pipe already disconnected)
/// means a client went away and that specific stream instance is disposed while the loop continues
/// accepting the next connection. <see cref="System.IO.EndOfStreamException"/> — thrown by
/// <see cref="Stream.ReadExactlyAsync(byte[],CancellationToken)"/> when a client closes its end
/// before sending a full frame — derives from <see cref="IOException"/>
/// (confirmed: https://learn.microsoft.com/dotnet/api/system.io.endofstreamexception), so the same
/// catch clause already covers it without listing it separately.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Registered via AddHostedService<IpcCommandServerPump>() in Bastion.Daemon's " +
        "composition root (GitHub issues #11/#12). Same documented CA1812 false-positive shape as " +
        "every other pump in this assembly (WinEventPumpService, Coalescer, ReconcilerLoopService).")]
internal sealed partial class IpcCommandServerPump(IpcCommandProcessor processor, ILogger<IpcCommandServerPump> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(IpcPipeNames.Command);

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
                    // A broken/disconnected pending server-stream instance itself, distinct from a
                    // connection that was accepted and then serviced -- json-ipc-config.md §4's
                    // cancellation-vs-disconnect distinction applies at this layer too. Replace the
                    // instance and keep accepting; never let this kill the pump.
                    LogAcceptFailed(ex);
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = CreatePipe();
                    continue;
                }

                // "Listen on N, spin up N+1": the next server instance starts accepting before the
                // just-accepted connection (`connected`) is serviced at all.
                NamedPipeServerStream connected = pipe;
                pipe = CreatePipe();

                // CA2025 flags any unawaited Task holding an IDisposable argument, but cannot see
                // that `connected` is only ever disposed from inside ServiceConnectionAsync's own
                // finally block, strictly after that same method's reads/writes on it complete --
                // exactly the "suppress if you know the task finishes using the instance before
                // it's disposed" case CA2025's own docs describe. This type's remarks explain why
                // the Task is deliberately not awaited at this call site (concurrent connection
                // servicing).
#pragma warning disable CA2025
                Task serviceTask = ServiceConnectionAsync(connected, stoppingToken);
#pragma warning restore CA2025
                _ = serviceTask.ContinueWith(
                    task => LogUnobservedConnectionFault(task.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        LogStopped();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Deliberate catch-all: see this type's remarks for why one connection's " +
            "unanticipated failure must never propagate to the accept loop (or the daemon) -- " +
            "this is an IPC boundary serviced by other, unprivileged, same-user processes, not " +
            "the core reconciliation pipeline ReconcilerLoopService/PlacementExecutionPump guard.")]
    private async Task ServiceConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        try
        {
            byte[] requestBody = await IpcFraming.ReadFrameAsync(pipe, stoppingToken).ConfigureAwait(false);
            IpcReply reply = ProcessRequest(requestBody);
            byte[] responseBody = JsonSerializer.SerializeToUtf8Bytes(reply, IpcJsonContext.Default.IpcReply);
            await IpcFraming.WriteFrameAsync(pipe, responseBody, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected cooperative shutdown while servicing this connection -- not an error.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The client went away mid-exchange (process killed, pipe reset) -- routine per
            // json-ipc-config.md §4, logged at a level below Error rather than swallowed silently.
            LogClientDisconnected(ex);
        }
        catch (Exception ex)
        {
            // See this type's remarks: a single connection's unanticipated failure must never take
            // the accept loop (or the daemon) down with it.
            LogConnectionFailed(ex);
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Peeks at the raw request's <c>protocolVersion</c> field before committing to a full
    /// polymorphic deserialize -- see this type's remarks and <see cref="IpcCommandProcessor"/>'s
    /// own remarks for why this two-phase parse exists.
    /// </summary>
    private IpcReply ProcessRequest(byte[] requestBody)
    {
        int receivedProtocolVersion;
        try
        {
            using var envelope = JsonDocument.Parse(requestBody);

            // JsonDocument.Parse succeeds for any syntactically valid JSON text, including a
            // non-object root (a bare `[]`, `null`, `42`, `"a string"`) -- JsonElement.TryGetProperty
            // is documented to throw InvalidOperationException, not JsonException, whenever
            // RootElement.ValueKind isn't JsonValueKind.Object
            // (https://learn.microsoft.com/dotnet/api/system.text.json.jsonelement.trygetproperty#system-text-json-jsonelement-trygetproperty(system-string-system-text-json-jsonelement@)).
            // Left unchecked, that exception would escape this method's own catch (JsonException)
            // below, propagate out of ProcessRequest entirely, and land in ServiceConnectionAsync's
            // `catch (Exception ex) when (ex is IOException or InvalidOperationException)` clause --
            // meant for "the client disconnected mid-exchange" -- silently closing the connection
            // instead of returning the documented ErrorReply a malformed request must get.
            if (envelope.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ErrorReply(IpcCommand.CurrentProtocolVersion, $"Malformed IPC request: expected a JSON object at the root, got {envelope.RootElement.ValueKind}.");
            }

            if (!envelope.RootElement.TryGetProperty("protocolVersion", out JsonElement versionElement) ||
                !versionElement.TryGetInt32(out receivedProtocolVersion))
            {
                return new ErrorReply(IpcCommand.CurrentProtocolVersion, "Malformed IPC request: missing or non-integer protocolVersion.");
            }
        }
        catch (JsonException ex)
        {
            return new ErrorReply(IpcCommand.CurrentProtocolVersion, $"Malformed IPC request: not valid JSON ({ex.Message}).");
        }

        if (receivedProtocolVersion != IpcCommand.CurrentProtocolVersion)
        {
            // A future client's command shape may not even deserialize against this build's
            // [JsonDerivedType] set -- reply from the raw version number alone, without ever
            // attempting the full IpcCommand deserialize below.
            return new ProtocolVersionMismatchReply(IpcCommand.CurrentProtocolVersion, receivedProtocolVersion);
        }

        IpcCommand command;
        try
        {
            command = JsonSerializer.Deserialize(requestBody, IpcJsonContext.Default.IpcCommand)
                ?? throw new JsonException("IPC command deserialized to null.");
        }
        catch (JsonException ex)
        {
            return new ErrorReply(IpcCommand.CurrentProtocolVersion, $"Malformed IPC command: {ex.Message}");
        }

        return processor.Process(command);
    }

    // An explicit, nonzero in/out buffer size is load-bearing, not a tuning knob -- see the
    // identical note on IpcBroadcastServerPump.CreatePipe(). The parameterless-buffer-size
    // NamedPipeServerStream constructors all chain to the 8-parameter ctor with a literal 0 for
    // both inBufferSize and outBufferSize (confirmed against the dotnet/runtime source), and
    // CreateNamedPipeW's own documented remarks state a write exceeding the remaining buffer
    // quota "will block until the data is read from the pipe"
    // (https://learn.microsoft.com/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew#remarks).
    // This pump's request/reply flow happens to keep a read posted on both sides before the
    // matching write in the common case (ServiceConnectionAsync reads the request before writing
    // the reply; the client's own SendCommandAsync posts its reply-read immediately after its
    // request-write completes), but that ordering is a scheduling coincidence, not a guarantee --
    // under thread-pool contention there is no structural reason the client's request write can't
    // race ahead of ServiceConnectionAsync's read. Sizing the buffer explicitly removes the race
    // entirely rather than relying on favorable timing, matching the fix proven necessary for the
    // broadcast pipe's PublishAsync (json-ipc-config.md tests exercising both pipes).
    private static NamedPipeServerStream CreatePipe() => new(
        IpcPipeNames.Command,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: 4096,
        outBufferSize: 4096);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC command server pump started on pipe '{PipeName}'.")]
    private partial void LogStarted(string pipeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC command server pump stopped.")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Accepting an IPC command connection failed; replacing the pipe instance and continuing.")]
    private partial void LogAcceptFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "An IPC command client disconnected while its request was being serviced.")]
    private partial void LogClientDisconnected(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Servicing an IPC command connection failed unexpectedly.")]
    private partial void LogConnectionFailed(Exception exception);

    // Defense-in-depth only -- ServiceConnectionAsync's own catch-all above means this should be
    // unreachable in practice; see this type's remarks on the fault-only ContinueWith it guards.
    [LoggerMessage(Level = LogLevel.Error, Message = "An IPC connection-servicing task faulted without its exception being handled internally.")]
    private partial void LogUnobservedConnectionFault(Exception? exception);
}
