using System.IO.Pipes;
using Bastion.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Real named-pipe round trip through <see cref="IpcCommandServerPump"/> (the server half) and
/// <see cref="IpcClient"/> (the client half) — GitHub issues #11/#12's shared acceptance criterion
/// "at least one full request/reply round trip," plus the protocol-version-mismatch path and the
/// accept loop's cancellation-vs-disconnect distinction.
/// </summary>
/// <remarks>
/// Real named pipes, not a fake transport seam: both ends are plain, fast, in-process BCL I/O with
/// no real flakiness risk, and exercising the genuine <see cref="IpcFraming"/>/
/// <see cref="IpcJsonContext"/>/<see cref="NamedPipeServerStream"/>/<see cref="NamedPipeClientStream"/>
/// stack end-to-end is strictly more representative of production than a fake seam would be for a
/// transport this small. <see cref="Bastion.Win32.Tests"/> already runs on Windows-only CI (Tiers
/// 2+), so there is no Linux-CI constraint pushing toward a fake here the way there is for
/// <c>Bastion.Core</c>/<c>Bastion.Layout</c>.
/// </remarks>
public sealed class IpcCommandServerPumpTests
{
    [Fact]
    public async Task AStatusCommandRoundTripsToAStatusReplyCarryingTheConfiguredDaemonVersion()
    {
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");
        using var pump = new IpcCommandServerPump(processor, NullLogger<IpcCommandServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            IpcReply reply = await IpcClient.SendCommandAsync(
                new StatusCommand(IpcCommand.CurrentProtocolVersion),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            StatusReply status = Assert.IsType<StatusReply>(reply);
            Assert.Equal("1.2.3-test", status.DaemonVersion);
            Assert.Equal(IpcCommand.CurrentProtocolVersion, status.ProtocolVersion);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ASecondConcurrentCommandIsAlsoServedRatherThanBlockedBehindTheFirst()
    {
        // Proves the "listen on N, spin up N+1" contract: two client connections issued back to
        // back (without awaiting the first's reply before starting the second) both complete.
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");
        using var pump = new IpcCommandServerPump(processor, NullLogger<IpcCommandServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Task<IpcReply> first = IpcClient.SendCommandAsync(
                new StatusCommand(IpcCommand.CurrentProtocolVersion), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Task<IpcReply> second = IpcClient.SendCommandAsync(
                new StatusCommand(IpcCommand.CurrentProtocolVersion), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            IpcReply[] replies = await Task.WhenAll(first, second);

            Assert.All(replies, reply => Assert.IsType<StatusReply>(reply));
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AProtocolVersionMismatchGetsATypedReplyInsteadOfADeserializationException()
    {
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");
        using var pump = new IpcCommandServerPump(processor, NullLogger<IpcCommandServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            IpcReply reply = await IpcClient.SendCommandAsync(
                new StatusCommand(ProtocolVersion: 999),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            ProtocolVersionMismatchReply mismatch = Assert.IsType<ProtocolVersionMismatchReply>(reply);
            Assert.Equal(IpcCommand.CurrentProtocolVersion, mismatch.ProtocolVersion);
            Assert.Equal(999, mismatch.ReceivedProtocolVersion);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ABrokenClientConnectionDoesNotKillTheAcceptLoopAndTheNextClientStillGetsServed()
    {
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");
        using var pump = new IpcCommandServerPump(processor, NullLogger<IpcCommandServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            // Connect and disconnect without writing a full frame -- the "client died mid-exchange"
            // case json-ipc-config.md §4 requires the accept loop to shrug off rather than fault.
            // A plain try/finally, not `await using`: the compiler-synthesized implicit-dispose
            // await has no syntactic hook to attach .ConfigureAwait(true) to, which CA2007 flags
            // here the same way it flags IpcClient.SendCommandAsync's production `await using`
            // (xUnit1030 requires ConfigureAwait(true), never (false), inside a test method).
            var abandoned = new NamedPipeClientStream(
                ".", IpcPipeNames.Command, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await abandoned.ConnectAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            finally
            {
                await abandoned.DisposeAsync().ConfigureAwait(true);
            }

            // The pump must still be alive and accepting -- prove it with an ordinary round trip.
            IpcReply reply = await IpcClient.SendCommandAsync(
                new StatusCommand(IpcCommand.CurrentProtocolVersion),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.IsType<StatusReply>(reply);
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task StopAsyncCancelsTheAcceptLoopCooperativelyWithoutThrowing()
    {
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");
        using var pump = new IpcCommandServerPump(processor, NullLogger<IpcCommandServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken);

        // No exception should propagate from routine, cooperative shutdown -- the
        // OperationCanceledException from the in-flight WaitForConnectionAsync must be caught and
        // treated as expected shutdown, not surfaced to the caller.
        await pump.StopAsync(TestContext.Current.CancellationToken);
    }
}
