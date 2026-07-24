using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace Bastion.Win32;

/// <summary>
/// Length-prefixed frame read/write over a <see cref="PipeStream"/> — docs/engineering/json-ipc-config.md
/// §4's documented framing: a 4-byte little-endian length prefix followed by the UTF-8 JSON body,
/// over <see cref="PipeTransmissionMode.Byte"/> (the default), never
/// <see cref="PipeTransmissionMode.Message"/>. Shared by every reader/writer of an IPC frame —
/// <see cref="IpcCommandServerPump"/>, <see cref="IpcBroadcastServerPump"/>, and
/// <see cref="IpcClient"/> — so the wire shape cannot drift between them.
/// </summary>
/// <remarks>
/// Adapted verbatim from the doc's own <c>WriteFrameAsync</c>/<c>ReadFrameAsync</c> sample, with
/// the one change the doc itself flags as worth considering: <see cref="MaxFrameLength"/> guards
/// <see cref="ReadFrameAsync"/> against a corrupt or hostile length prefix driving an unbounded
/// <c>new byte[length]</c> allocation. The body itself is intentionally <em>not</em> pooled via
/// <see cref="ArrayPool{T}"/> — IPC command/reply payloads are small, occasional, user-initiated
/// control-plane messages, nothing like the WinEvent ingest hot path
/// (docs/engineering/concurrency-performance.md's "never optimize a path that has not shown up as
/// hot" rule) — pooling here would add rented-buffer-length-vs-requested-length bookkeeping to
/// every call site for no measured benefit.
/// </remarks>
internal static class IpcFraming
{
    /// <summary>
    /// Upper bound on a single frame's declared body length. 1 MiB comfortably exceeds any real
    /// <see cref="Bastion.Core.IpcCommand"/>/<see cref="Bastion.Core.IpcReply"/> payload for the
    /// foreseeable command set; a declared length outside <c>[0, MaxFrameLength]</c> is treated as
    /// a malformed frame rather than an allocation request.
    /// </summary>
    internal const int MaxFrameLength = 1024 * 1024;

    /// <summary>Writes <paramref name="payload"/> as one length-prefixed frame.</summary>
    public static async ValueTask WriteFrameAsync(PipeStream pipe, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        byte[] header = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await pipe.WriteAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>Reads one length-prefixed frame and returns its body.</summary>
    /// <exception cref="InvalidDataException">The frame's declared length is negative or exceeds <see cref="MaxFrameLength"/>.</exception>
    public static async ValueTask<byte[]> ReadFrameAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrameLength)
        {
            throw new InvalidDataException($"IPC frame declared a length of {length} bytes, outside the allowed [0, {MaxFrameLength}] range.");
        }

        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return body;
    }
}
