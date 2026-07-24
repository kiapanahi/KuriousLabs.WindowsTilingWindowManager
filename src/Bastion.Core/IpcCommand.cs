using System.Text.Json.Serialization;

namespace Bastion.Core;

/// <summary>
/// Polymorphic base for every command <c>bastionc</c>/<c>bastion-bar</c> send to <c>bastiond</c>
/// over the request/reply named pipe (DESIGN.md §3.9, GitHub issues #11/#12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape verified against docs/engineering/json-ipc-config.md §1</b> — <c>[JsonPolymorphic]</c> +
/// <c>[JsonDerivedType(Type, string)]</c> on an abstract record base, source-gen (metadata mode)
/// compatible, is the documented pattern for the request/reply command envelope described in
/// DESIGN.md §3.9. <c>ProtocolVersion</c> is the first constructor parameter of every command per
/// that doc's §4: "put an integer <c>ProtocolVersion</c> as the first field of every envelope type
/// ... rather than negotiating a separate handshake frame" — every derived record threads it through
/// its own primary constructor (e.g. <see cref="StatusCommand"/>) so it is always present
/// regardless of which concrete command a caller builds.
/// </para>
/// <para>
/// <b>Lives in <c>Bastion.Core</c>, not <c>Bastion.Win32</c>.</b> Both <c>bastionc</c>
/// (<c>Bastion.Cli</c>, which references <c>Bastion.Win32</c> directly and reaches this type only
/// transitively through <c>Bastion.Win32</c>'s own reference to <c>Bastion.Core</c>) and
/// <c>bastiond</c> (<c>Bastion.Daemon</c>, which references all three projects directly) need this
/// shape, and <see cref="WorkspaceKey"/>/<see cref="RuleKey"/> already establish
/// <c>Bastion.Core</c> as this repo's home for plain, serializable, cross-boundary data contracts —
/// no Win32 type, no I/O, no wall clock, satisfying the <c>pure-core</c> skill's purity checklist in
/// full. The actual named-pipe transport (framing, the accept loop, the
/// <c>JsonSerializerContext</c> that (de)serializes this hierarchy) lives in <c>Bastion.Win32</c>
/// instead, mirroring the same split this repo already uses for
/// <c>Bastion.Core.WindowRulesDocument</c> (the DTO) vs. <c>Bastion.Daemon.ConfigJsonContext</c>
/// (the serializer, which lives wherever the actual file I/O happens) — the serializer belongs at
/// the boundary that performs the actual (de)serialization calls, not necessarily beside the DTO.
/// </para>
/// </remarks>
/// <param name="ProtocolVersion">
/// The protocol version this command's shape was built against. Compared against
/// <see cref="CurrentProtocolVersion"/> by the receiving side before any further processing —
/// see <see cref="IpcCommandProcessor"/> and <see cref="ProtocolVersionMismatchReply"/>.
/// </param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
[JsonDerivedType(typeof(StatusCommand), "status")]
public abstract record IpcCommand(int ProtocolVersion)
{
    /// <summary>
    /// The current IPC envelope protocol version every command/reply this build produces carries.
    /// Bump whenever a breaking envelope-shape change ships (json-ipc-config.md §4: "treat the
    /// command/reply DTOs as a versioned public contract, same posture as a published REST API,
    /// from the first release").
    /// </summary>
    public const int CurrentProtocolVersion = 1;
}
