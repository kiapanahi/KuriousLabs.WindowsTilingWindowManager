namespace Bastion.Core;

/// <summary>
/// Pure <see cref="IpcCommand"/> -&gt; <see cref="IpcReply"/> mapping — the same "deterministic
/// function of its inputs, zero Win32/I/O" shape <see cref="ILayoutEngine"/> uses for
/// <c>Layout(tree, workArea, constraints, gaps) → [(WindowId, RECT visibleBounds)]</c>
/// (DESIGN.md §3.5), applied to command dispatch instead of layout solving.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not the transport.</b> This type never touches a pipe, a byte buffer, or JSON —
/// it operates on an already-deserialized <see cref="IpcCommand"/> and returns an
/// <see cref="IpcReply"/> for the caller (<c>Bastion.Win32.IpcCommandServerPump</c>) to serialize
/// and write. Keeping dispatch logic here means it is testable with zero pipes, zero hosting, and
/// zero Windows dependency at all — a plain <c>Bastion.Core.Tests</c> fact, Linux-CI-tested like
/// everything else in this project.
/// </para>
/// <para>
/// <b>Protocol-version check happens twice, deliberately, for two different reasons.</b> The
/// transport layer (<c>IpcCommandServerPump</c>) peeks at the raw JSON's <c>protocolVersion</c>
/// field <em>before</em> attempting a full polymorphic deserialize, specifically so a future
/// client speaking a protocol version whose command shapes this build does not even recognize
/// (an unrecognized <c>$cmd</c> discriminator) still gets a clean <see cref="ProtocolVersionMismatchReply"/>
/// instead of a <see cref="System.Text.Json.JsonException"/> — deserialization into a concrete
/// <see cref="IpcCommand"/> could throw before <see cref="IpcCommand.ProtocolVersion"/> is ever
/// readable in that specific case. The check <em>here</em>, against an already-successfully-deserialized
/// command, exists so this type is correct and complete in isolation — a caller that skips the
/// transport-layer pre-check (every unit test in <c>Bastion.Core.Tests</c>, by construction) still
/// gets the right behavior, and there is exactly one place version-mismatch-shaped output is
/// decided for any command whose shape <em>did</em> successfully deserialize.
/// </para>
/// </remarks>
/// <param name="daemonVersion">
/// The running <c>bastiond</c>'s version string, reported verbatim in every <see cref="StatusReply"/>.
/// </param>
public sealed class IpcCommandProcessor(string daemonVersion)
{
    /// <summary>Maps <paramref name="command"/> to its reply. See this type's remarks for the version-check placement.</summary>
    public IpcReply Process(IpcCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ProtocolVersion != IpcCommand.CurrentProtocolVersion)
        {
            return new ProtocolVersionMismatchReply(IpcCommand.CurrentProtocolVersion, command.ProtocolVersion);
        }

        return command switch
        {
            StatusCommand => new StatusReply(IpcCommand.CurrentProtocolVersion, daemonVersion),

            // Defensive, not currently reachable through JSON deserialization (IpcJsonContext's
            // [JsonDerivedType] set is closed to StatusCommand alone today) -- but IpcCommand is a
            // public, non-sealed abstract record, so a same-version, unrecognized-to-this-switch
            // subtype is a real possibility for a future command this build predates. Never let an
            // unhandled case fall through to a SwitchExpressionException.
            _ => new ErrorReply(IpcCommand.CurrentProtocolVersion, $"Unrecognized IPC command '{command.GetType().Name}'."),
        };
    }
}
