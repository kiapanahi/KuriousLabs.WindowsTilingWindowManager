using System.Buffers.Binary;
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

    /// <summary>
    /// Regression test for a real bug: <c>PublishAsync</c> used to fan out sequentially
    /// (<see langword="foreach"/> over <c>_subscribers.Keys</c>, one <c>WriteFrameAsync</c> call
    /// at a time), so a second subscriber's write would not even start until the first's had
    /// fully resolved.
    /// </summary>
    /// <remarks>
    /// A design proving this via one subscriber that never reads at all plus one that reads
    /// immediately turned out to be flaky in practice: which of the two
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>-backed
    /// <c>_subscribers.Keys</c> enumerates first is not under this test's control and varies run
    /// to run (confirmed empirically), so whichever one happens to enumerate first gets serviced
    /// promptly regardless of whether the fan-out is sequential or concurrent -- the bug was only
    /// sometimes caught. Two <em>equally</em> slow subscribers remove that dependency entirely:
    /// each paces its own read of an oversized frame in small chunks (<see cref="ReadSlowlyAsync"/>),
    /// so it is the <em>total</em> elapsed time for both to finish -- not which one finishes
    /// first -- that distinguishes concurrent fan-out (close to one subscriber's own pacing) from
    /// sequential fan-out (roughly double, since the second cannot start until the first drains).
    /// </remarks>
    [Fact]
    public async Task PublishAsyncDeliversToTwoSlowSubscribersConcurrentlyRatherThanOneAfterAnother()
    {
        using var pump = new IpcBroadcastServerPump(NullLogger<IpcBroadcastServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        NamedPipeClientStream subscriberOne = await ConnectSubscriberAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        NamedPipeClientStream subscriberTwo = await ConnectSubscriberAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            await WaitUntilAsync(() => pump.SubscriberCount >= 2, TestContext.Current.CancellationToken).ConfigureAwait(true);

            // Comfortably larger than CreatePipe's 4096-byte outBufferSize, so each write can
            // only complete as fast as that subscriber's own paced reader drains it.
            var reply = new ErrorReply(IpcCommand.CurrentProtocolVersion, new string('x', 8192));

            // Stopwatch starts before PublishAsync: it synchronously kicks off (via Task.WhenAll)
            // every subscriber's write attempt up to its first real suspension point before the
            // call even returns, so this still captures the fan-out's true start.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Task publishTask = pump.PublishAsync(reply, TestContext.Current.CancellationToken);

#pragma warning disable CA2025 // both awaited via Task.WhenAll below, before either stream is disposed in `finally`
            Task<byte[]> readOne = ReadSlowlyAsync(subscriberOne, TestContext.Current.CancellationToken);
            Task<byte[]> readTwo = ReadSlowlyAsync(subscriberTwo, TestContext.Current.CancellationToken);
#pragma warning restore CA2025

            byte[][] bodies = await Task.WhenAll(readOne, readTwo)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
            stopwatch.Stop();

            Assert.Equal(bodies[0], bodies[1]);
            ErrorReply received = Assert.IsType<ErrorReply>(JsonSerializer.Deserialize(bodies[0], IpcJsonContext.Default.IpcReply));
            Assert.Equal(reply.Message, received.Message);

            // One subscriber's own paced read takes ChunkCount * s_chunkDelay ~= 8 * 150ms =
            // 1200ms. Concurrent fan-out keeps the total near that; sequential roughly doubles it
            // (~2400ms), since the second subscriber's write can't start until the first drains.
            Assert.True(
                stopwatch.ElapsedMilliseconds < 2000,
                $"Expected concurrent delivery well under 2000ms; took {stopwatch.ElapsedMilliseconds}ms -- PublishAsync may be fanning out sequentially again.");

            await publishTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await subscriberOne.DisposeAsync().ConfigureAwait(true);
            await subscriberTwo.DisposeAsync().ConfigureAwait(true);
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

    // Reads one length-prefixed IpcFraming frame like IpcFraming.ReadFrameAsync itself, except the
    // body is drained in small, deliberately paced chunks -- this is what makes
    // PublishAsyncDeliversToTwoSlowSubscribersConcurrentlyRatherThanOneAfterAnother's timing
    // signal (concurrent vs. sequential fan-out) independent of which subscriber a
    // ConcurrentDictionary enumerator happens to visit first: both subscribers are equally slow,
    // so it is the *total* elapsed time for both to finish that distinguishes the two
    // implementations, not which one finishes first.
    private const int ChunkSize = 1024;
    private static readonly TimeSpan s_chunkDelay = TimeSpan.FromMilliseconds(150);

    private static async Task<byte[]> ReadSlowlyAsync(NamedPipeClientStream client, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await client.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);

        byte[] body = new byte[length];
        for (int offset = 0; offset < length; offset += ChunkSize)
        {
            int count = Math.Min(ChunkSize, length - offset);
            await client.ReadExactlyAsync(body.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            await Task.Delay(s_chunkDelay, cancellationToken).ConfigureAwait(false);
        }

        return body;
    }
}
