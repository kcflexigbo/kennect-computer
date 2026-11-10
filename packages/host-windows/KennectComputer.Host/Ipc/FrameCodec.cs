using System.Buffers.Binary;

namespace KennectComputer.Host.Ipc;

/// <summary>Raised when a frame header announces a payload above <see cref="FrameCodec.MaxFrameBytes"/>.</summary>
public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException(int length)
        : base($"Frame length {length} exceeds the maximum of {FrameCodec.MaxFrameBytes} bytes.")
        => Length = length;

    public int Length { get; }
}

/// <summary>
/// Frames are a 4-byte little-endian payload length followed by UTF-8 JSON.
/// </summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    private const int HeaderLength = sizeof(int);

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(payload.Length);
        }

        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(payload.Length);
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; returns <see langword="null"/> only for a clean EOF before the header.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderLength];
        var headerRead = await ReadUpToAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < HeaderLength)
        {
            throw new EndOfStreamException("The stream ended in the middle of a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(length);
        }

        if (length == 0)
        {
            return [];
        }

        var payload = new byte[length];
        var payloadRead = await ReadUpToAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new EndOfStreamException("The stream ended in the middle of a frame payload.");
        }

        return payload;
    }

    private static async Task<int> ReadUpToAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
