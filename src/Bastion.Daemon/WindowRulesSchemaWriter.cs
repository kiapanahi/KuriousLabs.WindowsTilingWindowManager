using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Bastion.Core;

namespace Bastion.Daemon;

/// <summary>
/// Builds and durably writes the published JSON Schema for <see cref="WindowRulesDocument"/> — the
/// shape of the file at <see cref="WindowRulesConfigPaths.UserRulesFilePath"/> — via
/// <see cref="JsonSchemaExporter"/> (GitHub issue #9; DESIGN.md §3.9;
/// <c>docs/engineering/json-ipc-config.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Startup-time generation, not a build-time step — this issue's explicit call.</b> The
/// acceptance criterion names a concrete destination, <c>%USERPROFILE%\.config\bastion\</c>: a
/// per-user, per-machine directory that does not exist and cannot be known at build/CI time — only
/// the daemon, running on the actual end-user's machine, can resolve
/// <see cref="Environment.SpecialFolder.UserProfile"/> and write there. <c>json-ipc-config.md</c>
/// §3's separate suggestion to "regenerate it as part of the build/release step... snapshotted with
/// Verify" is a complementary, *different* artifact — a repo-level regression guard against
/// unintentional schema-shape drift in code review — not a substitute for this runtime, per-user
/// copy; <c>Bastion.Daemon.Tests</c>'s Verify-snapshotted schema test calls <see cref="BuildSchema"/>
/// directly to serve exactly that build/review-time purpose, sharing this exact generation logic
/// rather than duplicating it.
/// </para>
/// <para>
/// <b>Uses <see cref="ConfigJsonContext"/>'s own generated <c>JsonTypeInfo&lt;WindowRulesDocument&gt;</c>
/// directly</b> (<c>ConfigJsonContext.Default.WindowRulesDocument.GetJsonSchemaAsNode(...)</c>),
/// never a throwaway reflection-based <see cref="JsonSerializerOptions"/> — per
/// <c>json-ipc-config.md</c> §3: "do not construct a throwaway reflection-based
/// <c>JsonSerializerOptions</c> just to export the schema."
/// </para>
/// <para>
/// <b>Best-effort: failure here must never fail startup or the hot-reload path.</b> The schema file
/// is a convenience for editor tooling (autocomplete/validation against the user's hand-edited
/// <c>rules.jsonc</c>), not part of the config-gating contract itself — <see cref="WriteAsync"/>'s
/// caller (<see cref="WindowRulesSchemaPublisherService"/>) catches and logs rather than propagates.
/// </para>
/// </remarks>
internal static class WindowRulesSchemaWriter
{
    private static readonly JsonSchemaExporterOptions s_exporterOptions = new() { TreatNullObliviousAsNonNullable = true };

    /// <summary>Builds the schema <see cref="JsonNode"/> for <see cref="WindowRulesDocument"/>. Pure with respect to the filesystem — no I/O.</summary>
    public static JsonNode BuildSchema() => ConfigJsonContext.Default.WindowRulesDocument.GetJsonSchemaAsNode(s_exporterOptions);

    /// <summary>
    /// Serializes <see cref="BuildSchema"/>'s result and durably writes it to
    /// <paramref name="schemaFilePath"/> via the same temp-file-plus-rename pattern
    /// <c>Bastion.Win32.HwndJournalStore.WriteAsync</c> already established (a direct in-place write
    /// risks a truncated, unparseable schema file if the process dies mid-write).
    /// </summary>
    public static async Task WriteAsync(string schemaFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaFilePath);

        string? directory = Path.GetDirectoryName(schemaFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = BuildSchema().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string tempPath = $"{schemaFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, schemaFilePath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}
