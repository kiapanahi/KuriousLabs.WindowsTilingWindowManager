using Bastion.Core;
using Bastion.Win32;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="ReconcileNowSignal"/> — the real, production <see cref="IReconcileNowSignal"/>
/// this issue supplies, resolving the TODO left by <c>WinEventPumpService</c>/<c>Coalescer</c>'s own
/// doc comments ("the Reconciler ... will supply one").
/// </summary>
public sealed class ReconcileNowSignalTests
{
    [Fact]
    public async Task RequestReconcileNowForwardsToTheWrappedReconciler()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();

        // Far longer than this test could ever run for, so a pass only happens because the signal
        // fired, never because the heartbeat also happened to.
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        var signal = new ReconcileNowSignal(reconciler);
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        try
        {
            Assert.Equal(0, windowSystem.ReadAllCallCount);

            signal.RequestReconcileNow();

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1, "Expected ReconcileNowSignal to drive a Reconciler convergence pass.");
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }
}
