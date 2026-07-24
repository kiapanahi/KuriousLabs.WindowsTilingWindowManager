using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="IpcFraming"/> directly over a real named-pipe pair — the length prefix
/// format and the <see cref="IpcFraming.MaxFrameLength"/> guard docs/engineering/json-ipc-config.md
/// §4 describes.
/// </summary>
/// <remarks>
/// A dedicated, GUID-suffixed pipe name per test (never <see cref="IpcPipeNames"/>'s fixed
/// production names), so this suite can never collide with the accept-loop tests or with itself
/// under test parallelism. Named, not anonymous, pipes: an in-process
/// <see cref="AnonymousPipeServerStream"/>/<see cref="AnonymousPipeClientStream"/> pair (both ends
/// constructed directly in one process, rather than one end handed to a spawned child process --
/// the shape every BCL sample documents) proved unreliable here: <see cref="IpcFraming.WriteFrameAsync"/>
/// intermittently failed with "the pipe is broken," most plausibly because
/// <see cref="AnonymousPipeServerStream.DisposeLocalCopyOfClientHandle"/>'s documented contract
/// ("should be called after a client handle has been passed to a client process") assumes the
/// handle crosses a real process boundary, a precondition this same-process pairing does not
/// satisfy. Named pipes are exactly what every other IPC test in this suite already uses
/// end-to-end, so this switch is also the more representative choice.
/// </remarks>
public sealed class IpcFramingTests
{
    // An ordinary async helper, not a test method itself -- xUnit1030's ConfigureAwait(true)
    // requirement is scoped to [Fact]/[Theory] methods, so this follows the general
    // library-code convention (ConfigureAwait(false)) instead, same as IpcClient.SendCommandAsync.
    //
    // Explicit, nonzero in/out buffer sizes are required here, not incidental: the
    // parameterless-buffer-size NamedPipeServerStream constructors all pass a literal 0 for both
    // inBufferSize and outBufferSize (confirmed against the dotnet/runtime source), and
    // CreateNamedPipeW's own documented remarks state a write exceeding the remaining buffer quota
    // "will block until the data is read from the pipe"
    // (https://learn.microsoft.com/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew#remarks).
    // WriteFrameAsyncThenReadFrameAsyncRoundTripsThePayloadExactly below writes on the server
    // before the client has posted any read at all, which hung indefinitely against the
    // zero-buffer default -- the same mechanism proven for IpcBroadcastServerPump.PublishAsync
    // (see that type's own CreatePipe remarks). Sized identically to both production pumps'
    // CreatePipe() methods for consistency.
    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> CreateConnectedPipePairAsync(CancellationToken cancellationToken)
    {
        string pipeName = $"Bastion.Tests.IpcFraming.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 4096, outBufferSize: 4096);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        Task connectTask = client.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connectTask.ConfigureAwait(false);

        return (server, client);
    }

    [Fact]
    public async Task WriteFrameAsyncThenReadFrameAsyncRoundTripsThePayloadExactly()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) = await CreateConnectedPipePairAsync(TestContext.Current.CancellationToken);
        using (server)
        using (client)
        {
            byte[] payload = Encoding.UTF8.GetBytes("""{"$cmd":"status","protocolVersion":1}""");

            await IpcFraming.WriteFrameAsync(server, payload, TestContext.Current.CancellationToken);
            byte[] received = await IpcFraming.ReadFrameAsync(client, TestContext.Current.CancellationToken);

            Assert.Equal(payload, received);
        }
    }

    [Fact]
    public async Task ReadFrameAsyncReturnsAnEmptyBodyForAZeroLengthFrame()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) = await CreateConnectedPipePairAsync(TestContext.Current.CancellationToken);
        using (server)
        using (client)
        {
            await IpcFraming.WriteFrameAsync(server, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
            byte[] received = await IpcFraming.ReadFrameAsync(client, TestContext.Current.CancellationToken);

            Assert.Empty(received);
        }
    }

    [Fact]
    public async Task ReadFrameAsyncThrowsInvalidDataExceptionForADeclaredLengthAboveTheMaximum()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) = await CreateConnectedPipePairAsync(TestContext.Current.CancellationToken);
        using (server)
        using (client)
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, IpcFraming.MaxFrameLength + 1);
            await server.WriteAsync(header, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await IpcFraming.ReadFrameAsync(client, TestContext.Current.CancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ReadFrameAsyncThrowsInvalidDataExceptionForANegativeDeclaredLength()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) = await CreateConnectedPipePairAsync(TestContext.Current.CancellationToken);
        using (server)
        using (client)
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, -1);
            await server.WriteAsync(header, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await IpcFraming.ReadFrameAsync(client, TestContext.Current.CancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
        }
    }
}
