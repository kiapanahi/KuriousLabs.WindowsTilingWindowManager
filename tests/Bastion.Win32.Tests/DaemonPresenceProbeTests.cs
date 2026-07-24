using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="DaemonPresenceProbe"/> — GitHub issue #11's "daemon-presence check uses
/// <c>Mutex.TryOpenExisting</c>, never a throwing <c>OpenExisting</c>" acceptance criterion.
/// </summary>
public sealed class DaemonPresenceProbeTests
{
    [Fact]
    public void IsDaemonRunningReturnsFalseWhenNothingHoldsTheMutex()
    {
        // Best-effort: correct in CI (no real bastiond runs there) and on an ordinary dev machine
        // with bastiond not currently running -- the same environmental assumption
        // SingleInstanceGuardTests already makes for the mutex this mirrors.
        Assert.False(DaemonPresenceProbe.IsDaemonRunning());
    }

    [Fact]
    public void IsDaemonRunningReturnsTrueWhileSomethingHoldsTheMutexAndFalseAfterItIsReleased()
    {
        using (new Mutex(initiallyOwned: true, name: DaemonPresenceProbe.MutexName, createdNew: out bool createdNew))
        {
            Assert.True(createdNew, "A real bastiond (or a leftover test mutex) already holds this name -- cannot simulate presence.");
            Assert.True(DaemonPresenceProbe.IsDaemonRunning());
        }

        Assert.False(DaemonPresenceProbe.IsDaemonRunning());
    }

    [Fact]
    public void MutexNameIsScopedToTheCurrentUserSessionNotAFixedGlobalString()
    {
        // Mirrors Bastion.Daemon.Tests.SingleInstanceGuardTests' identical assertion against
        // SingleInstanceGuard.MutexName -- the two must compute the exact same string (this type's
        // own remarks explain why no automated cross-assembly check is possible), so a regression
        // in either one's literal shape should show up as a failure in both test suites.
        Assert.StartsWith(@"Local\Bastion.Daemon.", DaemonPresenceProbe.MutexName, StringComparison.Ordinal);
        Assert.NotEqual(@"Local\Bastion.Daemon.", DaemonPresenceProbe.MutexName);
    }
}
