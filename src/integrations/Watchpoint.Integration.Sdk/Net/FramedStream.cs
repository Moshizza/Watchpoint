using System.Buffers;
using System.IO.Pipelines;

namespace Watchpoint.Integration.Sdk.Net;

/// <summary>
/// Stream framing helper supporting length-prefix and delimiter framing over a Stream or PipeReader (INT §10).
/// Enforces maximum frame size (default 1 MB).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Named per INT spec §10")]
public static class FramedStream
{
    /// <summary>Default maximum frame size (1 MB).</summary>
    public const int DefaultMaxFrameSize = 1024 * 1024;

    /// <summary>
    /// Reads a frame delimited by a byte sequence (e.g. newline \n or \r\n).
    /// </summary>
    /// <param name="reader">PipeReader to read from.</param>
    /// <param name="delimiter">Delimiter byte sequence.</param>
    /// <param name="maxFrameSize">Maximum allowed frame size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The frame payload without delimiter, or null if end of stream reached before any data.</returns>
    /// <exception cref="InvalidDataException">Thrown if frame size exceeds <paramref name="maxFrameSize"/>.</exception>
    public static async ValueTask<byte[]?> ReadDelimiterFramedAsync(
        PipeReader reader,
        ReadOnlyMemory<byte> delimiter,
        int maxFrameSize = DefaultMaxFrameSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (delimiter.IsEmpty)
        {
            throw new ArgumentException("Delimiter cannot be empty.", nameof(delimiter));
        }

        while (!ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryFindDelimiter(buffer, delimiter.Span, out var position))
            {
                var length = buffer.Slice(0, position).Length;
                if (length > maxFrameSize)
                {
                    throw new InvalidDataException($"Frame size {length} exceeds maximum allowed {maxFrameSize} bytes.");
                }

                var payload = buffer.Slice(0, position).ToArray();
                // Advance past delimiter
                var nextPosition = buffer.GetPosition(delimiter.Length, position);
                reader.AdvanceTo(nextPosition);
                return payload;
            }

            if (buffer.Length > maxFrameSize)
            {
                throw new InvalidDataException($"Frame size exceeds maximum allowed {maxFrameSize} bytes.");
            }

            if (result.IsCompleted)
            {
                if (buffer.Length == 0)
                {
                    return null;
                }

                var payload = buffer.ToArray();
                reader.AdvanceTo(buffer.End);
                return payload;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }

        ct.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>
    /// Reads a frame with a 4-byte big-endian length prefix.
    /// </summary>
    /// <param name="reader">PipeReader to read from.</param>
    /// <param name="maxFrameSize">Maximum allowed frame size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The frame payload, or null if end of stream reached.</returns>
    public static async ValueTask<byte[]?> ReadLengthPrefixedAsync(
        PipeReader reader,
        int maxFrameSize = DefaultMaxFrameSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (!ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length < 4)
            {
                if (result.IsCompleted)
                {
                    reader.AdvanceTo(buffer.End);
                    return null;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            var lengthSpan = buffer.Slice(0, 4).ToArray();
            var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthSpan);

            if (payloadLength < 0 || payloadLength > maxFrameSize)
            {
                throw new InvalidDataException($"Length-prefix frame size {payloadLength} is invalid or exceeds maximum allowed {maxFrameSize} bytes.");
            }

            var totalLength = 4 + payloadLength;
            if (buffer.Length < totalLength)
            {
                if (result.IsCompleted)
                {
                    throw new InvalidDataException("Stream completed before complete length-prefixed frame was read.");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            var payload = buffer.Slice(4, payloadLength).ToArray();
            reader.AdvanceTo(buffer.GetPosition(totalLength));
            return payload;
        }

        ct.ThrowIfCancellationRequested();
        return null;
    }

    private static bool TryFindDelimiter(ReadOnlySequence<byte> sequence, ReadOnlySpan<byte> delimiter, out SequencePosition position)
    {
        var reader = new SequenceReader<byte>(sequence);
        if (reader.TryReadTo(out ReadOnlySequence<byte> _, delimiter, advancePastDelimiter: false))
        {
            position = reader.Position;
            return true;
        }

        position = default;
        return false;
    }
}
