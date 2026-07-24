using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Bastion.Win32;

/// <summary>
/// Real, file-system-backed <see cref="IHwndJournalStore"/> for
/// <c>%LOCALAPPDATA%\Bastion\hwnd-journal.json</c> (DESIGN.md §3.7, GitHub issue #8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Write path: temp file + <c>File.Move(overwrite: true)</c>, same directory.</b> A direct
/// in-place write risks leaving a truncated, unparseable journal on disk if the writing process is
/// killed mid-write — exactly the crash this journal exists to survive. Writing to a sibling temp
/// file first and only then moving it over the real path means the real path always names either
/// the previous complete document or the new complete one, never a partial one. Honesty note: this
/// is a defense against a mid-write <em>process</em> crash (the scenario DESIGN.md's crash-recovery
/// story targets — <c>bastiond</c> dying), not a guarantee against OS-level power loss, which would
/// need an explicit <c>fsync</c>-equivalent flush-to-disk this type does not perform; the .NET
/// <see cref="File.Move(string, string, bool)"/> docs themselves stop short of promising atomicity
/// (they describe same-volume behavior only implicitly, via contrast with the documented
/// copy-then-delete behavior for a <em>cross</em>-volume move) — the temp file is created via
/// <see cref="Path.ChangeExtension(string, string)"/>-style sibling naming specifically so the move
/// always stays on the journal's own volume.
/// </para>
/// <para>
/// <b>Not thread-safe; expects sequential invocation</b> — matching <c>PlacementExecutor</c>'s own
/// documented posture. Bastion's write-ahead call sites are driven one at a time (the write-then-act
/// ordering <see cref="HwndJournalWriter"/> enforces is itself inherently sequential); a future
/// caller needing concurrent access must serialize its own calls.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and by Bastion.Cli's " +
        "Program.cs (a different assembly) for `bastionc restore-windows`; intended to also be " +
        "registered once Bastion.Daemon's composition root is wired (GitHub issue #10). Same " +
        "documented CA1812 false-positive shape as PlacementSystemAdapter/WindowSystemAdapter.")]
internal sealed class HwndJournalStore(string journalFilePath) : IHwndJournalStore
{
    /// <summary>
    /// The production journal path DESIGN.md §3.7 names verbatim:
    /// <c>%LOCALAPPDATA%\Bastion\hwnd-journal.json</c>.
    /// </summary>
    public static string DefaultJournalFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bastion", "hwnd-journal.json");

    /// <inheritdoc/>
    public async Task<JournalDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(journalFilePath))
        {
            // No journal file yet -- a fresh install, or a journal nothing has ever been written
            // to. The routine, expected "nothing outstanding" case, not an error.
            return JournalDocument.Empty;
        }

        byte[] bytes = await File.ReadAllBytesAsync(journalFilePath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, JournalJsonContext.Default.JournalDocument)
            ?? throw new JsonException($"Journal file '{journalFilePath}' deserialized to a null document.");
    }

    /// <inheritdoc/>
    public async Task WriteAsync(JournalDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? directory = Path.GetDirectoryName(journalFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Sibling of the real path (same directory => same volume => the Move below never falls
        // back to copy-then-delete) with a per-call-unique suffix so two overlapping writers (e.g.
        // a stray double-invocation) never clobber each other's temp file mid-write.
        string tempPath = $"{journalFilePath}.{Guid.NewGuid():N}.tmp";

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JournalJsonContext.Default.JournalDocument);
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, journalFilePath, overwrite: true);
    }
}
