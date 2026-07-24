using System.Text.Json.Serialization;
using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// The dedicated <see cref="JsonSerializerContext"/> for the IPC command/reply envelope
/// (GitHub issues #11/#12) — its own logical model group, distinct from
/// <c>Bastion.Daemon.ConfigJsonContext</c> (config/rules-file DTOs) and
/// <c>Bastion.Win32.JournalJsonContext</c> (the write-ahead journal), per
/// <c>docs/engineering/json-ipc-config.md</c> §1: "Define one <c>JsonSerializerContext</c> per
/// logical model group, not one giant context."
/// </summary>
/// <remarks>
/// <para>
/// <b>Lives here, not in <c>Bastion.Core</c> alongside <see cref="IpcCommand"/>/<see cref="IpcReply"/>.</b>
/// This mirrors <c>ConfigJsonContext</c>'s own precedent exactly: <c>WindowRulesDocument</c> (the
/// DTO) lives in <c>Bastion.Core</c>, but <c>ConfigJsonContext</c> (the serializer) lives in
/// <c>Bastion.Daemon</c>, because that is where the actual <c>JsonSerializer.Deserialize</c>/
/// <c>Serialize</c> calls against config files happen. The identical reasoning applies here: every
/// actual (de)serialize call against an IPC frame's bytes happens inside this assembly
/// (<see cref="IpcCommandServerPump"/>, <see cref="IpcBroadcastServerPump"/>,
/// <see cref="IpcClient"/>) — <c>Bastion.Core</c> itself never touches JSON, bytes, or I/O of any
/// kind, by the <c>pure-core</c> skill's own hard constraint. A second, more mechanical reason:
/// this type's documented shape (json-ipc-config.md §1's own code sample) is <c>internal</c>, and
/// <c>Bastion.Core</c>'s <c>AssemblyInfo.cs</c> grants <c>InternalsVisibleTo</c> only to
/// <c>Bastion.Core.Tests</c> — an <c>internal</c> type there would be invisible to this assembly
/// entirely, whereas <c>Bastion.Win32</c> already grants <c>InternalsVisibleTo</c> to both
/// <c>bastionc</c> and <c>bastiond</c> (its two production consumers), so keeping this
/// <c>internal</c> here costs nothing and matches the doc sample's own accessibility exactly.
/// </para>
/// <para>
/// Call <see cref="Default"/>'s generated <c>IpcCommand</c>/<c>IpcReply</c>
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> properties directly at
/// every (de)serialization call site — the same statically-provably-reflection-free reasoning
/// <c>JournalJsonContext</c>'s own remarks give for why the <c>options</c>-taking
/// <see cref="System.Text.Json.JsonSerializer"/> overloads are avoided under this assembly's
/// <c>IsAotCompatible=true</c> build.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcCommand))]
[JsonSerializable(typeof(IpcReply))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
