using System.Text.Json.Serialization;

namespace Bastion.Core;

/// <summary>
/// Polymorphic base for every reply <c>bastiond</c> sends back over the request/reply named pipe,
/// or fans out over the broadcast pipe (DESIGN.md §3.9, GitHub issues #11/#12).
/// </summary>
/// <remarks>
/// Same shape rationale as <see cref="IpcCommand"/> (see its own remarks for the
/// <c>Bastion.Core</c>-vs-<c>Bastion.Win32</c> placement split) — a distinct
/// <c>TypeDiscriminatorPropertyName</c> (<c>"$reply"</c> vs. <c>IpcCommand</c>'s <c>"$cmd"</c>)
/// keeps a captured wire trace or manual debugging session unambiguous about which envelope kind
/// a given JSON blob is, even though the two are never actually parsed against the same
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> at any call site.
/// <see cref="ProtocolVersionMismatchReply"/> and <see cref="ErrorReply"/> are the two "something
/// went wrong" shapes every command dispatch can produce regardless of which concrete command was
/// sent; see <see cref="IpcCommandProcessor"/> for exactly when each is chosen.
/// </remarks>
/// <param name="ProtocolVersion">
/// The protocol version this reply's shape was built against — always
/// <see cref="IpcCommand.CurrentProtocolVersion"/> for any reply this build produces, per
/// docs/engineering/json-ipc-config.md §4's "put an integer ProtocolVersion as the first field of
/// every envelope type."
/// </param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$reply")]
[JsonDerivedType(typeof(StatusReply), "status")]
[JsonDerivedType(typeof(ProtocolVersionMismatchReply), "protocolVersionMismatch")]
[JsonDerivedType(typeof(ErrorReply), "error")]
public abstract record IpcReply(int ProtocolVersion);
