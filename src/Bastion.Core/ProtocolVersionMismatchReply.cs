namespace Bastion.Core;

/// <summary>
/// Returned instead of attempting to process any command whose <see cref="IpcCommand.ProtocolVersion"/>
/// does not match this <c>bastiond</c> build's <see cref="IpcCommand.CurrentProtocolVersion"/> —
/// docs/engineering/json-ipc-config.md §4: "On mismatch, the receiving side replies with a typed
/// <c>ProtocolVersionMismatch</c> reply (never silently coerces/truncates)."
/// </summary>
/// <remarks>
/// <c>bastionc</c> surfaces this as a clear "daemon is a different version, restart it" message
/// (GitHub issue #11's own acceptance criteria) rather than letting a shape mismatch surface as a
/// raw <see cref="System.Text.Json.JsonException"/> deep in deserialization.
/// </remarks>
/// <param name="ProtocolVersion">
/// This <c>bastiond</c>'s own <see cref="IpcCommand.CurrentProtocolVersion"/> — the version this
/// reply itself is shaped as, so it always deserializes cleanly regardless of what the sender
/// understood.
/// </param>
/// <param name="ReceivedProtocolVersion">
/// The protocol version actually present on the incoming command that triggered this reply, so the
/// caller's error message can state both numbers explicitly.
/// </param>
public sealed record ProtocolVersionMismatchReply(int ProtocolVersion, int ReceivedProtocolVersion) : IpcReply(ProtocolVersion);
