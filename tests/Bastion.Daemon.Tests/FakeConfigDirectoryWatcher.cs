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

    /// <summary>
    /// Snapshots the current <see cref="Changed"/> delegate for a later, possibly-stale invocation —
    /// simulating the real <see cref="FileSystemWatcher"/>'s in-flight-callback race
    /// <c>WindowRulesHotReloadService</c>'s remarks describe: a real event's multicast delegate
    /// field is read once when dispatch begins, so a subscriber removed via <c>-=</c> afterward
    /// does not retroactively affect an invocation already under way on that already-fetched
    /// delegate instance. <see cref="RaiseChanged"/> alone cannot reproduce this — invoking
    /// <see cref="Changed"/> *after* a test has unsubscribed a handler will simply skip it, because
    /// the field is re-read at that later invocation, not snapshotted earlier. Capturing the
    /// delegate here, then invoking it after <c>StopAsync</c>/<c>Dispose</c> has already
    /// unsubscribed and stopped, reproduces the actual ordering a real
    /// <see cref="FileSystemWatcher"/> callback dispatched to a thread-pool thread can hit.
    /// </summary>
    public EventHandler? CaptureCurrentChangedHandler() => Changed;

    public void Dispose() => IsDisposed = true;
}
