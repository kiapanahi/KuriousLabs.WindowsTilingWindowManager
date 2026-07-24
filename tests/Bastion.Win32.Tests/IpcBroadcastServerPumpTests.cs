using System.IO.Pipes;
using System.Text.Json;
using Bastion.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Real named-pipe broadcast fan-out through <see cref="IpcBroadcastServerPump"/> — GitHub issues
/// #11/#12's shared acceptance criterion "one broadcast fan-out to two subscribers."
/// </summary>
public sealed class IpcBroadcastServerPumpTests
{
    [Fact]
    public async Task PublishAsyncFansOutTheIdenticalReplyToTwoConnectedSubscribers()
    {
        using var pump = new IpcBroadcastServerPump(NullLogger<IpcBroadcastServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        // Plain variables + try/finally, not `await using`: the compiler-synthesized
        // implicit-dispose await an `await using` declaration emits has no syntactic hook to
        // attach .ConfigureAwait(true) to, which this repo's analyzer set still flags (xUnit1030
        // requires ConfigureAwait(true), never (false), inside a test method).
        NamedPipeClientStream subscriberOne = await ConnectSubscriberAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        NamedPipeClientStream subscriberTwo = await ConnectSubscriberAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            // Give the accept loop a moment to have registered both connections (each connection
            // completing ConnectAsync only guarantees the OS-level handshake, not that this pump's
            // own loop iteration has reached `_subscribers[connected] = 0` yet).
            await WaitUntilAsync(() => pump.SubscriberCount >= 2, TestContext.Current.CancellationToken).ConfigureAwait(true);

            var reply = new StatusReply(IpcCommand.CurrentProtocolVersion, "1.2.3-test");
            await pump.PublishAsync(reply, TestContext.Current.CancellationToken).ConfigureAwait(true);

            byte[] bodyOne = await IpcFraming.ReadFrameAsync(subscriberOne, TestContext.Current.CancellationToken).ConfigureAwait(true);
            byte[] bodyTwo = await IpcFraming.ReadFrameAsync(subscriberTwo, TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(bodyOne, bodyTwo);
            StatusReply received = Assert.IsType<StatusReply>(
                JsonSerializer.Deserialize(bodyOne, IpcJsonContext.Default.IpcReply));
            Assert.Equal("1.2.3-test", received.DaemonVersion);
        }
        finally
        {
            await subscriberOne.DisposeAsync().ConfigureAwait(true);
            await subscriberTwo.DisposeAsync().ConfigureAwait(true);
            await pump.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ADisconnectedSubscriberIsPrunedWithoutStoppingFanOutToTheRemainingOne()
    {
        using var pump = new IpcBroadcastServerPump(NullLogger<IpcBroadcastServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        NamedPipeClientStream dying = await ConnectSubscriberAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        NamedPipeClientStream survivor = await ConnectSubscriberAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            await WaitUntilAsync(() => pump.SubscriberCount >= 2, TestContext.Current.CancellationToken).ConfigureAwait(true);

            await dying.DisposeAsync().ConfigureAwait(true); // the "went away between broadcasts" case

            var reply = new StatusReply(IpcCommand.CurrentProtocolVersion, "1.2.3-test");
            await pump.PublishAsync(reply, TestContext.Current.CancellationToken).ConfigureAwait(true);

            byte[] body = await IpcFraming.ReadFrameAsync(survivor, TestContext.Current.CancellationToken).ConfigureAwait(true);
            StatusReply received = Assert.IsType<StatusReply>(JsonSerializer.Deserialize(body, IpcJsonContext.Default.IpcReply));
            Assert.Equal("1.2.3-test", received.DaemonVersion);
        }
        finally
        {
            await survivor.DisposeAsync().ConfigureAwait(true);
            await pump.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    // Ordinary async helpers, not test methods themselves -- xUnit1030's ConfigureAwait(true)
    // requirement is scoped to [Fact]/[Theory] methods, so these follow the general
    // library-code convention (ConfigureAwait(false)) instead, same as IpcClient.SendCommandAsync.
    private static async Task<NamedPipeClientStream> ConnectSubscriberAsync(CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".", IpcPipeNames.Broadcast, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }

        Assert.True(condition(), "Timed out waiting for the expected subscriber count.");
    }
}
