namespace Bastion.Daemon;

/// <summary>
/// The real, <see cref="FileSystemWatcher"/>-backed <see cref="IConfigDirectoryWatcher"/> for
/// GitHub issue #9's hot-reload path (DESIGN.md §3.9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Watches <see cref="FileSystemWatcher.Changed"/>, <see cref="FileSystemWatcher.Created"/>,
/// <see cref="FileSystemWatcher.Deleted"/>, and <see cref="FileSystemWatcher.Renamed"/> — all
/// four.</b> DESIGN.md §3.9 calls out specifically that "editors do atomic rename-replace": a
/// typical save writes a sibling temp file, then renames it over the target, which the OS reports
/// as a directory-level <c>Renamed</c> (and, depending on the editor/filesystem, sometimes a
/// <c>Created</c>+<c>Deleted</c> pair instead) rather than a same-file <c>Changed</c>. Subscribing
/// to all four is what makes watching the *directory* actually more robust than a single-file watch
/// handle, not just nominally so.
/// </para>
/// <para>
/// <b>Filters events to <paramref name="userRulesFileName"/> in code, deliberately not via
/// <see cref="FileSystemWatcher.Filter"/>.</b> The same directory this type watches also receives
/// <see cref="WindowRulesSchemaPublisherService"/>'s own <c>rules.schema.json</c> writes on every
/// startup (caught in review: every daemon start was scheduling a spurious reload-and-success-
/// notification for a file that was never <c>rules.jsonc</c>), so events must be filtered to the
/// one file this subsystem actually cares about. <see cref="FileSystemWatcher.Filter"/>'s exact
/// matching semantics against a <see cref="FileSystemWatcher.Renamed"/> event's *old* name were not
/// something this pass could confirm precisely against learn.microsoft.com, so
/// <see cref="IsUserRulesFile"/> checks <see cref="FileSystemEventArgs.Name"/>/
/// <see cref="RenamedEventArgs.OldName"/> explicitly instead — both because it is the choice this
/// research pass could actually verify, and because it is directly unit-testable
/// (<c>ConfigDirectoryWatcherFilterTests</c>) without needing a real, timing-dependent
/// <see cref="FileSystemWatcher"/> in the loop.
/// </para>
/// <para>
/// <see cref="FileSystemWatcher"/> raises its events on its own internal thread-pool-driven
/// callback mechanism, not a caller-owned message pump — unlike the WinEvent/keyboard-hook pumps
/// (<c>docs/engineering/daemon-architecture.md</c> §2), nothing here needs a dedicated
/// <see cref="Thread"/> or <c>GetMessage</c> loop.
/// </para>
/// </remarks>
internal sealed class ConfigDirectoryWatcher : IConfigDirectoryWatcher
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _userRulesFileName;

    public ConfigDirectoryWatcher(string directoryPath, string userRulesFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        ArgumentException.ThrowIfNullOrEmpty(userRulesFileName);
        _userRulesFileName = userRulesFileName;

        // FileSystemWatcher's constructor throws if the directory doesn't exist yet -- true on a
        // fresh install, before the user has ever created an overlay file. Idempotent and cheap.
        Directory.CreateDirectory(directoryPath);

        _watcher = new FileSystemWatcher(directoryPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public void Start() => _watcher.EnableRaisingEvents = true;

    /// <inheritdoc/>
    public void Stop() => _watcher.EnableRaisingEvents = false;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsUserRulesFile(e.Name, _userRulesFileName))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Checks both the new and old name: a save-as-temp-then-rename-over-target sequence makes
        // rules.jsonc the *new* name, but a rename-away-then-replace sequence (or an editor that
        // renames the live file aside before writing a fresh one) could make it the *old* name --
        // either direction is a change this subsystem must react to.
        if (IsUserRulesFile(e.Name, _userRulesFileName) || IsUserRulesFile(e.OldName, _userRulesFileName))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// <see cref="FileSystemWatcher"/>'s internal notification buffer overflowed (a storm of
    /// changes exceeded <see cref="FileSystemWatcher.InternalBufferSize"/>) or the watch itself
    /// failed. Treated the same as an ordinary change, unfiltered: WinEvents-are-hints-not-truth
    /// applies here too (DESIGN.md §1) — re-reading both files unconditionally is cheap and
    /// self-correcting, cheaper than trying to diagnose exactly what was missed.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// <see langword="true"/> if <paramref name="candidateFileName"/> names
    /// <paramref name="userRulesFileName"/> — ordinal, case-insensitive (Windows filesystems are
    /// case-preserving but not case-sensitive by default). A <see langword="null"/>
    /// <paramref name="candidateFileName"/> (documented as possible for a <see cref="RenamedEventArgs"/>
    /// "if the FileSystemWatcher does not get matching old and new name events from the operating
    /// system") never matches.
    /// </summary>
    internal static bool IsUserRulesFile(string? candidateFileName, string userRulesFileName) =>
        candidateFileName is not null && string.Equals(candidateFileName, userRulesFileName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Dispose()
    {
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
