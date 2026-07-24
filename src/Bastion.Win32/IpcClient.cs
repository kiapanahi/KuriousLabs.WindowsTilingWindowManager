using System.IO.Pipes;
using System.Text.Json;
using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// The thin-client half of the request/reply command pipe (DESIGN.md §3.9, GitHub issue #11) —
/// connects, sends one <see cref="IpcCommand"/>, reads back one <see cref="IpcReply"/>, and
/// disconnects. <c>bastionc</c>'s <c>Program.cs</c> is the intended caller; it contains no
/// tiling/business logic of its own, matching that issue's own framing.
/// </summary>
/// <remarks>
/// Callers should check <see cref="DaemonPresenceProbe.IsDaemonRunning"/> first
/// (<c>Mutex.TryOpenExisting</c>, never a throwing <c>OpenExisting</c> — GitHub issue #11's own
/// acceptance criteria) rather than relying on a connect failure here to distinguish "daemon not
/// running" from "daemon running but momentarily not accepting." <see cref="SendCommandAsync"/>
/// itself makes no such distinction — a <see cref="TimeoutException"/> from
/// <see cref="NamedPipeClientStream.ConnectAsync(TimeSpan,CancellationToken)"/> is exactly as
/// likely from a dead daemon as from one that is simply slow to accept, so the presence probe is
/// the caller's first line of triage, not this method.
/// </remarks>
internal static class IpcClient
{
    /// <summary>Connects, sends <paramref name="command"/>, and returns the daemon's reply.</summary>
    /// <exception cref="TimeoutException">The daemon did not accept the connection within <paramref name="connectTimeout"/>.</exception>
    /// <exception cref="IOException">The pipe broke while sending the command or reading the reply.</exception>
    public static async Task<IpcReply> SendCommandAsync(IpcCommand command, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            IpcPipeNames.Command,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);

            byte[] requestBody = JsonSerializer.SerializeToUtf8Bytes(command, IpcJsonContext.Default.IpcCommand);
            await IpcFraming.WriteFrameAsync(pipe, requestBody, cancellationToken).ConfigureAwait(false);

            byte[] responseBody = await IpcFraming.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(responseBody, IpcJsonContext.Default.IpcReply)
                ?? throw new JsonException("IPC reply deserialized to null.");
        }
        finally
        {
            // A plain try/finally + explicit DisposeAsync, not an `await using` declaration: the
            // compiler-synthesized implicit-dispose await an `await using` local emits has no
            // syntactic hook to attach .ConfigureAwait(false) to, which CA2007/MA0004 flag in
            // non-test code (this repo's analyzer set does not special-case library methods the
            // way it does xUnit [Fact] test methods).
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
