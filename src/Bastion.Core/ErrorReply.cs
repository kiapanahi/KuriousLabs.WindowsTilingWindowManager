namespace Bastion.Core;

/// <summary>
/// A generic, safe fallback reply for an incoming request <c>bastiond</c> could not process for a
/// reason other than a protocol-version mismatch (an unrecognized command shape despite a matching
/// protocol version, or a malformed/corrupt request body) — never a raw exception surfacing to the
/// caller across the pipe.
/// </summary>
/// <remarks>
/// Scoped deliberately narrowly: this is not a general error-code taxonomy, just the one safety-net
/// shape that lets <see cref="IpcCommandProcessor"/> (and the accept loop that calls it,
/// <c>Bastion.Win32.IpcCommandServerPump</c>) always produce a well-formed <see cref="IpcReply"/>
/// instead of letting a <see cref="System.Text.Json.JsonException"/> or an unrecognized
/// <see cref="IpcCommand"/> subtype escape as an unhandled failure. <see cref="Message"/> is
/// intended for a human reading <c>bastionc</c>'s output, not machine-parsed error codes.
/// </remarks>
/// <param name="ProtocolVersion">See <see cref="IpcReply.ProtocolVersion"/>.</param>
/// <param name="Message">A human-readable description of what went wrong.</param>
public sealed record ErrorReply(int ProtocolVersion, string Message) : IpcReply(ProtocolVersion);
