namespace Bastion.Win32;

/// <summary>
/// Minimal logging for the write-ahead HWND journal (GitHub issue #8) — the same
/// not-yet-composition-rooted stand-in shape as <see cref="HookDiagnostics"/>, scoped to the
/// journal/restore concern instead of hook callbacks.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §3.9, docs/engineering/daemon-architecture.md): wire this to the daemon's
/// structured (<c>[LoggerMessage]</c>-source-generated) logging pipeline once the composition root
/// exists (GitHub issue #10) — until then this is a minimal, allocation-conscious fallback so
/// <see cref="JournalRestoreOnShutdownService"/> has somewhere real to report a hard failure,
/// matching every other not-yet-wired Bastion.Win32 component's current logging posture.
/// </remarks>
internal static class JournalDiagnostics
{
    /// <summary>
    /// Logs that a clean-shutdown restore pass could not even read/parse the journal (e.g. a
    /// corrupt file) — the daemon still shuts down regardless (DESIGN.md §1's must-not-strand-a-
    /// window principle does not extend to blocking process exit on a diagnostic-logging concern).
    /// </summary>
    public static void LogRestoreOnShutdownFailed(Exception exception) =>
        Console.Error.WriteLine($"[Bastion.Win32] clean-shutdown journal restore failed: {exception}");

    /// <summary>Logs a one-line summary of a completed restore pass (restored/skipped/failed counts).</summary>
    public static void LogRestoreSummary(int restored, int skipped, int failed) =>
        Console.WriteLine($"[Bastion.Win32] journal restore: {restored} restored, {skipped} skipped, {failed} failed.");
}
