using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    public async Task ANonObjectJsonRootGetsATypedErrorReplyRatherThanADisconnectedPipe(string nonObjectJsonBody)
    {
        // Regression test: JsonDocument.Parse succeeds for any syntactically valid JSON text,
        // including a non-object root, but JsonElement.TryGetProperty is documented to throw
        // InvalidOperationException (not JsonException) whenever RootElement.ValueKind isn't
        // JsonValueKind.Object. Unguarded, that exception used to escape ProcessRequest entirely
        // and land in ServiceConnectionAsync's `catch (Exception ex) when (ex is IOException or
        // InvalidOperationException)` clause -- meant for "the client disconnected mid-exchange"
        // -- silently closing the connection instead of returning the documented ErrorReply a
        // malformed request must get. Written directly via IpcFraming.WriteFrameAsync, not
        // through IpcClient, since IpcClient only ever sends real IpcCommand instances and could
        // never produce a non-object body.
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");
        using var pump = new IpcCommandServerPump(processor, NullLogger<IpcCommandServerPump>.Instance);
        await pump.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            var client = new NamedPipeClientStream(
                ".", IpcPipeNames.Command, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);

                byte[] requestBody = Encoding.UTF8.GetBytes(nonObjectJsonBody);
                await IpcFraming.WriteFrameAsync(client, requestBody, TestContext.Current.CancellationToken).ConfigureAwait(true);

                byte[] responseBody = await IpcFraming.ReadFrameAsync(client, TestContext.Current.CancellationToken).ConfigureAwait(true);
                IpcReply reply = JsonSerializer.Deserialize(responseBody, IpcJsonContext.Default.IpcReply)
                    ?? throw new InvalidOperationException("Test setup failure: IPC reply deserialized to null.");

                ErrorReply error = Assert.IsType<ErrorReply>(reply);
                Assert.Contains("Malformed IPC request", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            await pump.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
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

    /// <summary>
    /// Regression test for a real bug (GitHub Copilot review finding on this PR):
    /// <see cref="IpcClient.SendCommandAsync"/> can throw <see cref="JsonException"/> -- either
    /// straight from <see cref="JsonSerializer.Deserialize"/> on genuinely invalid JSON syntax, or
    /// from <see cref="IpcClient"/>'s own explicit <c>?? throw new JsonException("IPC reply
    /// deserialized to null.")</c> guard when the reply body is the valid JSON <c>null</c> literal
    /// -- but nothing in this suite proved either path, so <c>bastionc</c>'s <c>status</c> command
    /// (the only caller) had an uncaught exception waiting for it against a malformed or
    /// differently-versioned daemon instead of the clean, user-facing error every other failure
    /// mode gets.
    /// </summary>
    /// <remarks>
    /// Written directly against a hand-rolled fake daemon bound to the real
    /// <see cref="IpcPipeNames.Command"/> pipe -- never through <see cref="IpcCommandServerPump"/>/
    /// <see cref="IpcCommandProcessor"/>, which could never produce either malformed body -- the
    /// same "fake the other side directly via IpcFraming" approach
    /// <see cref="ANonObjectJsonRootGetsATypedErrorReplyRatherThanADisconnectedPipe"/> already uses
    /// for the request side. Safe to place in this same test class/collection (rather than a
    /// dedicated fake-daemon test class) because it binds the identical fixed
    /// <see cref="IpcPipeNames.Command"/> pipe name every other test in this file also uses: xUnit
    /// runs the methods of one test class sequentially by default, so there is no risk of this
    /// method's raw server instance colliding with another test method's real
    /// <see cref="IpcCommandServerPump"/> the way there would be across two different test classes
    /// (different, potentially-parallel collections) -- see <see cref="IpcFramingTests"/>'s own
    /// remarks for why that cross-class risk is why its own pipe names are GUID-suffixed instead.
    /// </remarks>
    [Theory]
    [InlineData("this is not valid json at all")]
    [InlineData("null")]
    [InlineData("""{"$reply":"someFutureReplyKind","protocolVersion":1}""")]
    public async Task SendCommandAsyncThrowsJsonExceptionForAMalformedOrNullReplyBody(string malformedReplyBody)
    {
        NamedPipeServerStream fakeDaemon = CreateFakeDaemonPipe();
        try
        {
            Task<IpcReply> clientTask = IpcClient.SendCommandAsync(
                new StatusCommand(IpcCommand.CurrentProtocolVersion),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            await fakeDaemon.WaitForConnectionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await IpcFraming.ReadFrameAsync(fakeDaemon, TestContext.Current.CancellationToken).ConfigureAwait(true); // drain the request
            await IpcFraming.WriteFrameAsync(fakeDaemon, Encoding.UTF8.GetBytes(malformedReplyBody), TestContext.Current.CancellationToken).ConfigureAwait(true);

            await Assert.ThrowsAsync<JsonException>(async () => await clientTask.ConfigureAwait(true)).ConfigureAwait(true);
        }
        finally
        {
            await fakeDaemon.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Regression test for a real bug (GitHub Copilot review finding on this PR):
    /// <see cref="IpcFraming.ReadFrameAsync"/>'s frame-length guard (already proven directly
    /// against <see cref="IpcFraming"/> itself by <see cref="IpcFramingTests"/>) applies just as
    /// much to <see cref="IpcClient"/> reading a reply as it does to the server reading a request
    /// -- a corrupt or hostile length prefix from whatever is listening on <c>bastiond</c>'s pipe
    /// name must not go uncaught by <see cref="IpcClient.SendCommandAsync"/> callers such as
    /// <c>bastionc</c>'s <c>status</c> command.
    /// </summary>
    [Fact]
    public async Task SendCommandAsyncThrowsInvalidDataExceptionWhenTheReplyFrameDeclaresACorruptLength()
    {
        NamedPipeServerStream fakeDaemon = CreateFakeDaemonPipe();
        try
        {
            Task<IpcReply> clientTask = IpcClient.SendCommandAsync(
                new StatusCommand(IpcCommand.CurrentProtocolVersion),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            await fakeDaemon.WaitForConnectionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await IpcFraming.ReadFrameAsync(fakeDaemon, TestContext.Current.CancellationToken).ConfigureAwait(true); // drain the request

            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, IpcFraming.MaxFrameLength + 1);
            await fakeDaemon.WriteAsync(header, TestContext.Current.CancellationToken).ConfigureAwait(true);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await clientTask.ConfigureAwait(true)).ConfigureAwait(true);
        }
        finally
        {
            await fakeDaemon.DisposeAsync().ConfigureAwait(true);
        }
    }

    // Mirrors IpcCommandServerPump.CreatePipe()'s own buffer sizing (see that method's remarks for
    // why an explicit, nonzero buffer size is load-bearing) -- this pipe plays the role of a
    // buggy/malicious/differently-versioned bastiond for the two tests above, so it must behave
    // like a real one in every way that matters to IpcClient.
    private static NamedPipeServerStream CreateFakeDaemonPipe() => new(
        IpcPipeNames.Command,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: 4096,
        outBufferSize: 4096);
}
