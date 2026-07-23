# JSON, IPC & Config: Wire Format, Schema & Named Pipes

Owns the concrete `System.Text.Json` source-gen layout for both config DTOs and the IPC
envelope types, JSONC parsing/hot-reload mechanics, published JSON Schema generation, and the
named-pipe transport (framing, security, multi-instance, cancellation) that connects
`bastiond` to `bastionc` and `bastion-bar` (DESIGN.md §3.9, §10). For Generic Host wiring,
`[OptionsValidator]`/`EnableConfigurationBindingGenerator` options-binding mechanics, and
hosted-service lifetime rules see `docs/engineering/daemon-architecture.md` §4 (that section
now defers the `JsonSerializerContext` layout to here — do not duplicate it there). For
bounded-channel/dedicated-thread rules the IPC pumps must follow see
`docs/engineering/concurrency-performance.md`. For `BannedApiAnalyzers`/`BannedSymbols.txt`
enforcement see `docs/engineering/quality-gates.md`. For how to test the protocol see
`docs/engineering/testing.md` (Tier-2 replay applies directly to IPC dispatch; `Verify`
snapshots apply directly to schema/JSON output).

---

## 1. `System.Text.Json` source generation under NativeAOT

`bastiond`, `bastionc`, and `bastion-bar` all publish AOT (DESIGN.md §10), so reflection-based
`JsonSerializer` calls are not just slow — they are unsupported (`JsonSerializer` reflection
requires `Type.GetType`/dynamic `Emit`-style code paths that AOT does not support).

- Define one `JsonSerializerContext` per logical model group, not one giant context: a
  `ConfigJsonContext` for `LayoutOptions`/rules-file DTOs, and an `IpcJsonContext` for the
  request/reply/broadcast envelope types. Register every root type with `[JsonSerializable]`;
  compose both into a single resolver via `JsonTypeInfoResolver.Combine`/
  `TypeInfoResolverChain` on the `JsonSerializerOptions` instance actually used at the pipe
  and config boundaries, so one options object serves both domains without a manual `switch`.
  - Reference: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation
- **Disable the reflection fallback explicitly** rather than relying on `PublishAot` to do it
  implicitly: set `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>`
  in `Directory.Build.props` (or per-project). With it unset, `PublishTrimmed`/`PublishAot`
  disables the reflection default anyway, but a stray CoreCLR-only debug run would silently
  fall back to reflection and mask a bug until the AOT publish. Explicit is safer than implicit
  here. Guard any resolver-selection code with the link-time-constant
  `JsonSerializer.IsReflectionEnabledByDefault` check so a `DefaultJsonTypeInfoResolver` is
  never rooted in the AOT build:
  ```csharp
  static JsonSerializerOptions CreateOptions() => new()
  {
      TypeInfoResolver = JsonSerializer.IsReflectionEnabledByDefault
          ? new DefaultJsonTypeInfoResolver()
          : JsonTypeInfoResolverChain.Combine(ConfigJsonContext.Default, IpcJsonContext.Default),
  };
  ```
  A reflection-based call under a fully AOT-disabled context throws `InvalidOperationException`
  with a descriptive message instead of failing unpredictably.
  - Reference: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation#disable-reflection-defaults
- Two generation modes exist: `JsonSourceGenerationMode.Metadata` (default) and `.Serialization`
  (fast-path, write-only — fast-path *deserialization* does not exist, per
  [dotnet/runtime#55043]). **Do not set `JsonSourceGenerationMode.Serialization` alone on any
  context that carries a polymorphic hierarchy** — `JsonDerivedTypeAttribute` requires the
  metadata generator; a `Serialization`-only context raises build diagnostic **SYSLIB1039**.
  Leave `[JsonSourceGenerationOptions]` at its default mode (`Metadata`, which also emits the
  fast-path members) unless a specific non-polymorphic context is proven hot enough to justify
  narrowing it.
  - Reference: https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib1039
- **Polymorphic IPC commands are supported.** `[JsonPolymorphic]` + `[JsonDerivedType(Type,
  string)]` on the command base type both work with source generation (metadata mode) — this
  is the correct shape for the request/reply command envelope described in DESIGN.md §3.9.
  ```csharp
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "$cmd")]
  [JsonDerivedType(typeof(FocusWindowCommand), "focusWindow")]
  [JsonDerivedType(typeof(SetLayoutCommand), "setLayout")]
  public abstract record IpcCommand(int ProtocolVersion);

  public sealed record FocusWindowCommand(int ProtocolVersion, int WindowIdValue) : IpcCommand(ProtocolVersion);
  public sealed record SetLayoutCommand(int ProtocolVersion, string EngineName) : IpcCommand(ProtocolVersion);

  [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
  [JsonSerializable(typeof(IpcCommand))]
  [JsonSerializable(typeof(IpcReply))]
  internal sealed partial class IpcJsonContext : JsonSerializerContext;
  ```
  - Reference: https://learn.microsoft.com/dotnet/api/system.text.json.serialization.jsonderivedtypeattribute
- `required` members on a command/config record are honored by both reflection and source-gen
  deserialization (`JsonRequiredAttribute`/the `required` keyword map to the same enforced-set
  check) and are fast-path-supported. `required` alone does not imply non-null — pair it with
  NRT annotations, not a runtime null check, for reference-typed members.
- Attributes **not** supported on the fast-path (still fine under metadata-mode
  deserialization, just skip them if a context is ever narrowed to `Serialization` mode):
  `JsonConstructorAttribute`, `JsonConverterAttribute`, `JsonExtensionDataAttribute`,
  `JsonNumberHandlingAttribute`.
  - Reference: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation-modes#serialization-optimization-fast-path-mode

---

## 2. JSONC config: parsing, layering, hot-reload

DESIGN.md §3.9: JSONC config layered as user overrides over a shipped curated rules file,
directory-watched with a 200 ms debounce, atomic immutable swap on success, parse errors keep
the old config and raise a bar notification.

- **Exact property names differ between the reader-level and serializer-level option types —
  do not conflate them.** `JsonReaderOptions`/`JsonDocumentOptions` expose
  `CommentHandling` (type `JsonCommentHandling`) and `AllowTrailingCommas`.
  `JsonSerializerOptions` exposes the differently-named `ReadCommentHandling` and
  `AllowTrailingCommas` (same name here). When a `Utf8JsonReader`/`JsonDocument` reads first
  and the parsed result is then handed to `JsonSerializer.Deserialize(ref Utf8JsonReader, ...)`,
  the **reader-level** options win for comment/trailing-comma handling — the serializer options
  passed to that overload only control conversion, not tokenization.
  ```csharp
  static readonly JsonDocumentOptions RulesFileOptions = new()
  {
      CommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true,
  };

  static readonly JsonSerializerOptions ConfigSerializerOptions = new()
  {
      ReadCommentHandling = JsonCommentHandling.Skip,
      AllowTrailingCommas = true,
      TypeInfoResolver = ConfigJsonContext.Default,
  };
  ```
  - Reference: https://learn.microsoft.com/dotnet/api/system.text.json.jsondocumentoptions
  - Reference: https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializeroptions.readcommenthandling
- **Layering strategy**: parse the shipped rules file and the user overlay into two separate
  DTO instances, then merge at the object-graph level (last-write-wins per named rule/section) —
  never text-merge the two JSONC documents before parsing. Text-merging JSONC is fragile
  (comment placement, trailing commas, duplicate keys) where object-graph merging after two
  clean parses is not.
- **Hot-reload**: watch the *directory* containing both files with `FileSystemWatcher` (editors
  do atomic rename-replace, which a file-level watch can miss mid-write); debounce at 200 ms
  per DESIGN.md §3.9. On a fire, parse into a brand-new immutable config object (a `record`
  wrapping `ImmutableArray<T>`/`init`-only properties throughout) and swap a single
  `volatile`/`Interlocked.Exchange`-published reference — readers never see a partially-applied
  config. On a parse failure, discard the new attempt, keep the existing published reference,
  and raise the bar notification; never null out or partially mutate the live config.
- **Do not layer both configs into one `IConfiguration` provider chain purely for this merge.**
  `Microsoft.Extensions.Configuration`'s provider-chain last-wins semantics work per-*key*, which
  matches simple flat overrides but does not give you an explicit hook to reject the whole
  overlay on a single bad rule and keep serving the last-known-good snapshot — do the two-parse
  + object-graph-merge + atomic-swap described above instead, and reserve `IConfiguration`/
  `IOptionsMonitor` for options that are allowed to take effect key-by-key without an
  all-or-nothing gate.

---

## 3. Published JSON Schema

- **`System.Text.Json.Schema.JsonSchemaExporter`** (introduced .NET 9, present unchanged in the
  .NET 10 API surface) generates a JSON Schema `JsonNode` from a `JsonSerializerOptions`/`Type`
  pair or from a `JsonTypeInfo` directly, via the `GetJsonSchemaAsNode` extension methods. This
  is the mechanism for DESIGN.md §3.9's published schema in
  `%USERPROFILE%\.config\bastion\`.
  ```csharp
  JsonNode schema = ConfigSerializerOptions.GetJsonSchemaAsNode(
      typeof(BastionConfig),
      new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
  await File.WriteAllTextAsync(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
  ```
  - Reference: https://learn.microsoft.com/dotnet/api/system.text.json.schema.jsonschemaexporter
- Works against a source-gen `JsonTypeInfo` as well as a reflection-based one, so it stays
  AOT-safe as long as the `JsonSerializerOptions` passed in already resolves through
  `ConfigJsonContext` — do not construct a throwaway reflection-based `JsonSerializerOptions`
  just to export the schema.
- `JsonSchemaExporterOptions.TransformSchemaNode` is the extension point for anything the
  default exporter gets structurally wrong for hand-authored JSONC (e.g. injecting
  `"$comment"` metadata, tightening a `GapPx`-style range constraint the `[Range]` attribute
  doesn't itself express as a schema keyword). Treat the exported schema as a generated
  artifact — regenerate it as part of the build/release step, never hand-edit it, and snapshot
  it with `Verify` (`docs/engineering/testing.md`) so an unintentional shape change surfaces in
  review.
- **Confirmed net-10.0 presence**: the `GetJsonSchemaAsNode` API reference page's version
  moniker range explicitly lists `net-10.0` (alongside `net-9.0`, `net-10.0-pp`, `net-11.0`,
  `net-11.0-pp`) for both the `JsonTypeInfo` and `(JsonSerializerOptions, Type)` overloads, so
  the exporter (and this extension method) is verified present, unchanged, in the .NET 10 API
  surface — not just a preview-only artifact. No .NET-10-specific behavior change surfaced
  during verification; re-check the `view=net-10.0` page only if relying on a specific
  `JsonSchemaExporterOptions` edge case not already covered above.

---

## 4. Named-pipe IPC

DESIGN.md §3.9/§10: message-mode named pipes (chosen over `AF_UNIX` for peer identity + ACLs),
one pipe for request/reply commands, one for broadcast state-subscription, ACL'd to the
interactive user.

- **`PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`** on every `NamedPipeServerStream`/
  `NamedPipeClientStream` bastiond/bastionc/bastion-bar create. `CurrentUserOnly` checks both
  the Windows user account **and** elevation level on Windows — plain SID match is not enough;
  an elevated `bastiond` (DESIGN.md §3.10 elevated-daemon mode) will *not* be reachable via a
  `CurrentUserOnly` pipe from a non-elevated `bastionc`/`bastion-bar`. Track that constraint
  explicitly if/when the elevated-daemon mode ships — it needs an explicit `PipeSecurity` ACL
  path instead (see next bullet), not `CurrentUserOnly`.
  - Reference: https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions
- **`CurrentUserOnly` and a custom `PipeSecurity` are mutually exclusive, not layered.**
  `NamedPipeServerStreamAcl.Create`'s documented remarks state that if `options` contains
  `CurrentUserOnly`, any supplied `PipeSecurity` is **ignored**, and the stream is created with
  a system-assigned `PipeSecurity` granting the current Windows user sole ownership and full
  control. For the default (non-elevated) single-user case, `CurrentUserOnly` alone is
  sufficient and simpler than hand-building a `PipeSecurity`/`PipeAccessRule` — reserve
  `NamedPipeServerStreamAcl.Create` with an explicit `PipeSecurity` for the elevated-daemon
  cross-privilege-level scenario, and drop `CurrentUserOnly` when you do.
  - Reference: https://learn.microsoft.com/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create
- **`System.IO.Pipes.AccessControl` is not a NuGet package you add on .NET 10.** The
  `NamedPipeServerStreamAcl`/`PipeSecurity`/`PipesAclExtensions` types still live in an assembly
  named `System.IO.Pipes.AccessControl.dll` in the API docs, but the corresponding NuGet package
  is one of the ones Microsoft stopped shipping/updating once .NET 6 landed, because "their
  implementation is now part of the .NET 6 platform" — i.e. the assembly ships in the shared
  framework already. Do not add a `PackageReference`/`PackageVersion` for it in
  `Directory.Packages.props`; just reference the types, and let CPM's audit step (
  `docs/engineering/quality-gates.md`) flag it if that ever changes upstream.
  - Reference: https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/6.0/older-framework-versions-dropped
- **Framing: length-prefixed, not `PipeTransmissionMode.Message`.** Use `PipeTransmissionMode.Byte`
  (the default) and frame every payload as a fixed-width (4-byte, little-endian, `BinaryPrimitives`)
  length prefix followed by the UTF-8 JSON body. Rationale: `PipeTransmissionMode.Message` is a
  Windows-specific read mode that both ends must agree on (`ReadMode` on the client must match),
  loses message boundaries silently if the read buffer is smaller than the message unless the
  caller checks `PipeStream.IsMessageComplete` in a loop, and composes poorly with pooled
  (`ArrayPool<byte>`)/`Utf8JsonReader`-based incremental parsing. A hand-rolled length prefix is
  a few lines, is trivially testable, and lets the reader rent exactly the right buffer size
  up front instead of looping on partial messages.
  ```csharp
  static async ValueTask WriteFrameAsync(PipeStream pipe, ReadOnlyMemory<byte> payload, CancellationToken ct)
  {
      byte[] header = ArrayPool<byte>.Shared.Rent(4);
      try
      {
          BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
          await pipe.WriteAsync(header.AsMemory(0, 4), ct).ConfigureAwait(false);
          await pipe.WriteAsync(payload, ct).ConfigureAwait(false);
      }
      finally
      {
          ArrayPool<byte>.Shared.Return(header);
      }
  }

  static async ValueTask<byte[]> ReadFrameAsync(PipeStream pipe, CancellationToken ct)
  {
      byte[] header = new byte[4];
      await pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
      int length = BinaryPrimitives.ReadInt32LittleEndian(header);
      byte[] body = new byte[length]; // consider ArrayPool + a max-length guard for untrusted input
      await pipe.ReadExactlyAsync(body, ct).ConfigureAwait(false);
      return body;
  }
  ```
  - Reference: https://learn.microsoft.com/dotnet/api/system.io.pipes.pipetransmissionmode
- **Request/reply command pipe vs broadcast state-subscription pipe both need a
  multi-instance accept loop**, just for different reasons: the command pipe must serve
  concurrent `bastionc` invocations, and the broadcast pipe needs one connected instance per
  subscriber (`bastion-bar`, possibly multiple `bastionc --watch` sessions) to fan the same
  state out. Pass `NamedPipeServerStream.MaxAllowedServerInstances` (`-1`, meaning
  OS-determined, up to 254) as `maxNumberOfServerInstances`, and after each
  `WaitForConnectionAsync` completes, immediately construct and start listening on a **new**
  `NamedPipeServerStream` instance before servicing the just-accepted connection — the classic
  "listen on N, spin up N+1" pattern. Run this accept loop on its own dedicated pump per
  `docs/engineering/concurrency-performance.md`, never inline in the Reconciler.
  - Reference: https://learn.microsoft.com/dotnet/api/system.io.pipes.namedpipeserverstream.maxallowedserverinstances
- **Cancellation vs disconnect are different failures — catch them differently.**
  `PipeStream.ReadAsync`/`WriteAsync` store an `OperationCanceledException` in the returned task
  when the passed `CancellationToken` is canceled — treat this as expected, cooperative shutdown
  (host stopping, subscriber closing its client deliberately), not an error to log at Error
  level. A broken/reset connection (client process died, pipe reset) surfaces as `IOException`
  ("the pipe is broken") from `WriteAsync`, or an `InvalidOperationException` from either method
  if the pipe is already in a disconnected state — treat these as the normal "a client went
  away" case: catch, dispose that server-stream instance, and loop back to accept the next
  connection. Do not let either exception escape the accept loop and kill the pump thread.
  - Reference: https://learn.microsoft.com/dotnet/api/system.io.pipes.pipestream.readasync
  - Reference: https://learn.microsoft.com/dotnet/api/system.io.pipes.pipestream.writeasync
- **Protocol version handshake**: put an integer `ProtocolVersion` as the first field of every
  envelope type (see the `IpcCommand`/`IpcReply` shapes in §1) rather than negotiating a
  separate handshake frame. On mismatch, the receiving side replies with a typed
  `ProtocolVersionMismatch` reply (never silently coerces/truncates) so `bastionc`/`bastion-bar`
  can surface "daemon is a different version, restart it" instead of a confusing deserialization
  failure further down the pipeline. Bump the constant whenever a breaking envelope-shape change
  ships; treat the command/reply DTOs as a versioned public contract, same posture as a
  published REST API, from the first release.

---

## 5. Forbidden

- **Reflection-based `JsonSerializer.Serialize`/`Deserialize` calls anywhere on an AOT-published
  path** (`bastiond`, `bastionc`, `bastion-bar`) — set
  `JsonSerializerIsReflectionEnabledByDefault=false` and pass an explicit
  `TypeInfoResolver`/`TypeInfoResolverChain` built from `ConfigJsonContext`/`IpcJsonContext`
  everywhere. See §1.
- **`BinaryFormatter`.** Already banned repo-wide via `BannedSymbols.txt`
  (`T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter`,
  `docs/engineering/quality-gates.md`) — do not re-litigate it here, just don't reach for it for
  IPC payloads either. It is unconditionally throwing (CWE-502) and has no relevance to a
  JSON-over-pipes design regardless.
- **`DataContractJsonSerializer`.** Reflection-based, not source-gen-capable, no AOT story —
  add `T:System.Runtime.Serialization.Json.DataContractJsonSerializer` to
  `BannedSymbols.txt` alongside the existing `BinaryFormatter` entry rather than leaving it
  merely conventionally avoided.
- **`Newtonsoft.Json`** anywhere in `bastiond`/`bastionc`/`bastion-bar`/`Bastion.Core`. No
  source-generation/AOT story equivalent to `System.Text.Json`'s, and it is an additional
  supply-chain dependency for a security-sensitive IPC boundary that already has a fully
  AOT-safe, zero-extra-dependency answer in-box. Add
  `N:Newtonsoft.Json` to `BannedSymbols.txt`.
- **Synchronous pipe reads/writes (`PipeStream.Read`/`Write`, or blocking on
  `ReadAsync(...).GetAwaiter().GetResult()`) on the Reconciler thread or any pump thread.**
  Pipe I/O belongs on its own dedicated accept-loop pump (§4); blocking the Reconciler on pipe
  I/O couples IPC client behavior (a slow/frozen `bastion-bar`) to tiling-decision latency,
  which DESIGN.md's single-threaded-actor model treats as a correctness hazard, not just a
  performance one.
- **`PipeOptions.CurrentUserOnly` combined with a hand-built `PipeSecurity`**, expecting the
  ACL to still apply — it is silently ignored by `NamedPipeServerStreamAcl.Create` whenever
  `CurrentUserOnly` is set (§4). Pick one mechanism per pipe instance.
- **`PipeTransmissionMode.Message` framing** for the command/broadcast pipes — length-prefixed
  `Byte`-mode framing is the chosen approach (§4); do not mix the two across the codebase.
- **Text-merging the shipped rules file and the user overlay JSONC documents before parsing**
  (string concatenation, naive line-splicing) — parse both independently, merge the resulting
  object graphs (§2).
- **Hand-editing the exported JSON Schema file.** It is a generated artifact of
  `JsonSchemaExporter` (§3); any manual correction belongs in a `TransformSchemaNode` delegate
  so it survives regeneration.
