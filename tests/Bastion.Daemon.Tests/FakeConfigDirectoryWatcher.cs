namespace Bastion.Daemon.Tests;

/// <summary>
/// In-memory <see cref="IConfigDirectoryWatcher"/> fake — the seam that keeps
/// <see cref="WindowRulesHotReloadServiceTests"/> from depending on the real, live-OS
/// <see cref="FileSystemWatcher"/> mechanism (docs/engineering/testing.md §5's Tier-2 fake-adapter
/// shape). <see cref="RaiseChanged"/> is the test-only hook standing in for a real filesystem
/// notification.
/// </summary>
internal sealed class FakeConfigDirectoryWatcher : IConfigDirectoryWatcher
{
    public int StartCallCount { get; private set; }

    public int StopCallCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public event EventHandler? Changed;

    public void Start() => StartCallCount++;

    public void Stop() => StopCallCount++;

    /// <summary>Simulates a filesystem change notification firing.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => IsDisposed = true;
}
