namespace Bastion.Daemon;

/// <summary>
/// The directory-change-notification seam <see cref="WindowRulesHotReloadService"/> depends on,
/// matching <c>docs/engineering/testing.md</c> §5's Tier-2 fake-adapter shape: the real
/// filesystem-notification mechanism (<see cref="ConfigDirectoryWatcher"/>, backed by
/// <see cref="FileSystemWatcher"/>) is a live-OS dependency a unit test should never depend on
/// directly, so <c>Bastion.Daemon.Tests</c> drives <see cref="WindowRulesHotReloadService"/>'s
/// debounce/atomic-swap/keep-old-on-failure logic against an in-memory fake that raises
/// <see cref="Changed"/> synchronously and deterministically instead.
/// </summary>
internal interface IConfigDirectoryWatcher : IDisposable
{
    /// <summary>
    /// Raised at least once for every relevant filesystem change under the watched directory —
    /// creation, content change, deletion, or rename (an editor's atomic rename-replace fires this,
    /// which is exactly why the *directory*, not a single file handle, is watched — DESIGN.md §3.9).
    /// The standard <see cref="EventHandler"/> shape (MA0046) rather than a bare <see cref="Action"/>
    /// — <see cref="EventArgs.Empty"/> is always passed, since every subscriber's response is
    /// "re-read and re-merge both known files from scratch," so which specific file/change fired is
    /// not load-bearing information.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Begins raising <see cref="Changed"/>. Idempotent.</summary>
    void Start();

    /// <summary>Stops raising <see cref="Changed"/>. Idempotent.</summary>
    void Stop();
}
