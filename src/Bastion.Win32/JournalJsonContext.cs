using System.Text.Json.Serialization;

namespace Bastion.Win32;

/// <summary>
/// The dedicated <see cref="JsonSerializerContext"/> for the write-ahead HWND journal (GitHub issue
/// #8) — its own logical model group, distinct from the not-yet-built config/IPC contexts (GitHub
/// issues #9/#11/#12), per <c>docs/engineering/json-ipc-config.md</c> §1: "Define one
/// <c>JsonSerializerContext</c> per logical model group, not one giant context." The journal file
/// is plain local JSON, not IPC traffic, but goes through the identical source-gen discipline per
/// that doc and this issue's own acceptance criteria.
/// </summary>
/// <remarks>
/// <b>Call <see cref="Default"/>'s generated <c>JournalDocument</c> <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>
/// property directly at every (de)serialization call site</b> (<see cref="HwndJournalStore"/>) —
/// e.g. <c>JsonSerializer.Serialize(document, Default.JournalDocument)</c> — rather than building a
/// <see cref="System.Text.Json.JsonSerializerOptions"/> wrapper around <see cref="Default"/>.
/// Confirmed empirically this session: the <c>options</c>-taking overloads of
/// <see cref="System.Text.Json.JsonSerializer.Serialize{TValue}(TValue, System.Text.Json.JsonSerializerOptions?)"/>/
/// <c>Deserialize</c>/<c>SerializeToUtf8Bytes</c> all carry
/// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>/
/// <see cref="System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"/> unconditionally — the
/// analyzer cannot see that a specific <c>options</c> instance's <c>TypeInfoResolver</c> is
/// source-gen-only, so IL2026/IL3050 fire at the call site regardless of how carefully that
/// instance was constructed. The <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/>-based overloads
/// (<c>Default.JournalDocument</c>, generated per <see cref="JsonSerializableAttribute"/> root type
/// below) carry neither attribute — they are statically provably reflection-free, not just
/// runtime-safe by construction — which is why <c>Bastion.Win32.csproj</c>'s
/// <c>IsAotCompatible=true</c> build is clean only once every call site uses this shape. This also
/// means the <c>JsonSerializerIsReflectionEnabledByDefault=false</c> guarded-<c>TypeInfoResolver</c>
/// pattern shown in json-ipc-config.md §1 (for building one shared, general-purpose
/// <see cref="System.Text.Json.JsonSerializerOptions"/> that serves multiple root types through a
/// single options instance) is unnecessary here: this context has exactly one root type, so there
/// is nothing to combine a resolver chain for.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(JournalDocument))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
