using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Construction-only coverage for <see cref="WinEventPumpService"/>, plus one narrow pump-thread
/// lifecycle assertion (<see cref="StartAsyncWithAnAlreadyCanceledTokenThrowsAndLeavesNoPumpThreadAlive"/>):
/// that a canceled <c>StartAsync</c> cleans up the pump thread it already started rather than
/// orphaning it. The rest of the lifecycle — real hook registration outcomes, <c>StopAsync</c>'s
/// <c>PostThreadMessage</c>/<c>Thread.Join</c> handshake under ordinary shutdown — is intentionally
/// not exercised here: that is Tier 3 (<c>Bastion.TestWindows</c>) territory per GitHub issue #13,
/// not this unit-test project.
/// </summary>
public sealed class WinEventPumpServiceTests
{
    [Fact]
    public void ConstructorWiresIngestReaderToAFreshEmptyChannel()
    {
        var reconcileSignal = new FakeReconcileNowSignal();
        using var pump = new WinEventPumpService(reconcileSignal);

        Assert.NotNull(pump.IngestReader);
        Assert.False(pump.IngestReader.TryRead(out _));
    }

    [Fact]
    public async Task StartAsyncWithAnAlreadyCanceledTokenThrowsAndLeavesNoPumpThreadAlive()
    {
        var reconcileSignal = new FakeReconcileNowSignal();
        using var pump = new WinEventPumpService(reconcileSignal);
        var alreadyCanceled = new CancellationToken(canceled: true);

        // ManualResetEventSlim.Wait(CancellationToken) is documented to throw OperationCanceledException
        // itself (not the Task-cancellation-flavored TaskCanceledException derived type) when the
        // token is already canceled — https://learn.microsoft.com/dotnet/api/system.threading.manualreseteventslim.wait.
        await Assert.ThrowsAsync<OperationCanceledException>(() => pump.StartAsync(alreadyCanceled));

        // StartAsync's catch block awaits StopAsync — which Thread.Joins the already-started pump
        // thread — before rethrowing, so by the time the exception above has propagated, the join
        // has already either completed or itself thrown a TimeoutException. No "shortly after"
        // polling delay is needed: this assertion is deterministic, not a race.
        Assert.False(pump.IsPumpThreadAlive);
    }
}
