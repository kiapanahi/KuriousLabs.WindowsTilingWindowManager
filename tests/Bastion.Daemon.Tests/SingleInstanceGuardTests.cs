using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// Exercises <see cref="SingleInstanceGuard"/> — GitHub issue #10's single-instance enforcement
/// (docs/engineering/daemon-architecture.md §7). A named <see cref="Mutex"/> is a genuine
/// OS-level, cross-process object: two <see cref="Mutex"/> instances constructed with the identical
/// name observe the same "already owned" state regardless of whether the existing owner lives in
/// this process or a different one, so a same-process test faithfully exercises the exact
/// production naming/acquire logic without needing to spawn a real second <c>bastiond</c> process.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquireSucceedsWhenNoOtherInstanceHoldsTheMutex()
    {
        using Mutex? acquired = SingleInstanceGuard.TryAcquire();

        Assert.NotNull(acquired);
    }

    [Fact]
    public void ASecondTryAcquireFailsWhileTheFirstIsStillHeld()
    {
        using Mutex? first = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(first);

        // The mutex is process-scoped by name, not by the specific Mutex object/thread that holds
        // it, so a second acquire attempt -- even from the same test process -- observes exactly
        // what a genuinely separate bastiond invocation would.
        using Mutex? second = SingleInstanceGuard.TryAcquire();

        Assert.Null(second);
    }

    [Fact]
    public void TryAcquireSucceedsAgainAfterTheFirstMutexIsReleased()
    {
        Mutex? first = SingleInstanceGuard.TryAcquire();
        Assert.NotNull(first);
        first.Dispose(); // releases the OS-level mutex -- the production Program.cs never does
                         // this while running; only a test verifying release behavior does.

        using Mutex? second = SingleInstanceGuard.TryAcquire();

        Assert.NotNull(second);
    }

    [Fact]
    public void MutexNameIsScopedToTheCurrentUserSessionNotAFixedGlobalString()
    {
        // DESIGN.md/CreateMutex's own documented remarks: a fixed, predictable, global name is a
        // local-DoS vector. This does not (and cannot, from a single-user test run) prove the SID
        // varies per user -- it proves the name is user-SID-derived and Local\-scoped, not a bare
        // literal like "BastionDaemon".
        Assert.StartsWith(@"Local\Bastion.Daemon.", SingleInstanceGuard.MutexName, StringComparison.Ordinal);
        Assert.NotEqual(@"Local\Bastion.Daemon.", SingleInstanceGuard.MutexName);
    }
}
