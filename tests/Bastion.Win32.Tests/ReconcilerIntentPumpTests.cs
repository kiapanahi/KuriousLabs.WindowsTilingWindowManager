using System.Threading.Channels;
using Bastion.Core;
using Bastion.Win32;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="ReconcilerIntentPump"/> — the bridge from the Coalescer's coalesced-intent
/// stream to the Reconciler, DESIGN.md §3.4's "(1) coalesced intents" convergence trigger.
/// </summary>
public sealed class ReconcilerIntentPumpTests
{
    [Fact]
    public async Task ACoalescedIntentTriggersAConvergencePassWithoutWaitingForTheHeartbeat()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, reconciler);
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, windowSystem.ReadAllCallCount);

            // The pump never inspects intent kind/payload (see its own remarks) -- any of the six
            // typed intents is equally a "wake the loop up" signal.
            Assert.True(intents.Writer.TryWrite(new WindowAppeared(Hwnd: 0x1000)));

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1, "Expected the coalesced intent to drive a Reconciler convergence pass.");
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DifferentIntentKindsAllDriveTheSameConvergenceTrigger()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var windowSystem = new FakeWindowSystem();
        var options = new ReconcilerOptions { HeartbeatInterval = TimeSpan.FromDays(1) };
        using var reconciler = new Reconciler(windowSystem, new FakeLayoutEngine(), time, options);
        var intents = Channel.CreateUnbounded<CoalescedIntent>();
        using var pump = new ReconcilerIntentPump(intents.Reader, reconciler);
        using var cts = new CancellationTokenSource();

        Task loopTask = reconciler.RunAsync(cts.Token);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(intents.Writer.TryWrite(new WindowVanished(Hwnd: 0x2000)));

            for (var attempt = 0; attempt < 200 && windowSystem.ReadAllCallCount == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            Assert.True(windowSystem.ReadAllCallCount >= 1);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
            await cts.CancelAsync().ConfigureAwait(true);
            await loopTask.ConfigureAwait(true);
        }
    }
}
