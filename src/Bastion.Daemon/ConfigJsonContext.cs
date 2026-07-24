using System.Text.Json;
using System.Text.Json.Serialization;
using Bastion.Core;

namespace Bastion.Daemon;

/// <summary>
/// The dedicated <see cref="JsonSerializerContext"/> for config/rules-file DTOs (GitHub issue #9) —
/// its own logical model group, distinct from <c>Bastion.Win32.JournalJsonContext</c> (the
/// write-ahead journal, issue #8) and the not-yet-built IPC envelope context (issue #11/#12), per
/// <c>docs/engineering/json-ipc-config.md</c> §1: "Define one <c>JsonSerializerContext</c> per
/// logical model group, not one giant context."
/// </summary>
/// <remarks>
/// <para>
/// <b>JSONC support lives entirely in this attribute, not in a separate <c>JsonDocumentOptions</c>
/// static.</b> <c>json-ipc-config.md</c> §2 documents a real gotcha: when a <c>Utf8JsonReader</c>/
/// <c>JsonDocument</c> is constructed manually and *then* handed to
/// <c>JsonSerializer.Deserialize(ref Utf8JsonReader, ...)</c>, the reader-level
/// <c>JsonDocumentOptions.CommentHandling</c>/<c>AllowTrailingCommas</c> win over whatever the
/// serializer-level <c>JsonSerializerOptions</c> says, because the reader has already tokenized by
/// the time the serializer options are consulted. <see cref="WindowRulesConfigLoader"/> never
/// manually constructs a <c>Utf8JsonReader</c>/<c>JsonDocument</c> — it calls the plain
/// bytes-plus-<c>JsonTypeInfo</c> deserialize overload, which builds its own reader internally
/// straight from <em>this context's own</em> <see cref="ReadCommentHandling"/>/
/// <see cref="AllowTrailingCommas"/> settings, so the reader/serializer split this gotcha warns
/// about cannot occur on this code path.
/// </para>
/// <para>
/// <b><see cref="UseStringEnumConverter"/> renders <see cref="WindowRuleAction"/> as its literal C#
/// member name</b> (<c>"Manage"</c>/<c>"Floating"</c>/<c>"Ignore"</c>), via the AOT-safe generic
/// <c>JsonStringEnumConverter&lt;TEnum&gt;</c> the blanket context option applies — never the
/// non-generic <c>JsonStringEnumConverter</c>, which is <c>[RequiresDynamicCode]</c> and
/// unsupported under Native AOT (confirmed via
/// learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation#serialize-enum-fields-as-strings).
/// </para>
/// <para>
/// Call <see cref="Default"/>'s generated <c>WindowRulesDocument</c> <c>JsonTypeInfo&lt;T&gt;</c>
/// property directly at every (de)serialization call site (<c>JsonSerializer.Deserialize(bytes,
/// Default.WindowRulesDocument)</c>) rather than building a <c>JsonSerializerOptions</c> wrapper —
/// same reasoning and the same statically-provably-reflection-free
/// <c>IsAotCompatible</c>-cleanliness argument as <c>Bastion.Win32.JournalJsonContext</c>'s own
/// remarks: the <c>options</c>-taking overloads carry
/// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>/
/// <see cref="System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"/> unconditionally, while
/// the <c>JsonTypeInfo&lt;T&gt;</c> overloads carry neither. This context has exactly one root type,
/// so there is nothing to build a <c>TypeInfoResolverChain</c> for either.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WindowRulesDocument))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
