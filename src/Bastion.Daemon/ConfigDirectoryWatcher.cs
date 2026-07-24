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
/// <see cref="FileSystemWatcher"/> raises its events on its own internal thread-pool-driven
/// callback mechanism, not a caller-owned message pump — unlike the WinEvent/keyboard-hook pumps
/// (<c>docs/engineering/daemon-architecture.md</c> §2), nothing here needs a dedicated
/// <see cref="Thread"/> or <c>GetMessage</c> loop.
/// </para>
/// </remarks>
internal sealed class ConfigDirectoryWatcher : IConfigDirectoryWatcher
{
    private readonly FileSystemWatcher _watcher;

    public ConfigDirectoryWatcher(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);

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

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnRenamed(object sender, RenamedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// <see cref="FileSystemWatcher"/>'s internal notification buffer overflowed (a storm of
    /// changes exceeded <see cref="FileSystemWatcher.InternalBufferSize"/>) or the watch itself
    /// failed. Treated the same as an ordinary change: WinEvents-are-hints-not-truth applies here
    /// too (DESIGN.md §1) — re-reading both files unconditionally is cheap and self-correcting,
    /// cheaper than trying to diagnose exactly what was missed.
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

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
