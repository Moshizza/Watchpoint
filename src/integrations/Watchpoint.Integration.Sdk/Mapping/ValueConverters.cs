using System.Buffers.Binary;

namespace Watchpoint.Integration.Sdk.Mapping;

/// <summary>Byte order for 32-bit values (Modbus / BACnet word and byte ordering).</summary>
public enum ByteOrder
{
    /// <summary>Big-endian (standard network order): bytes [0, 1, 2, 3].</summary>
    ABCD,

    /// <summary>Little-endian: bytes [3, 2, 1, 0].</summary>
    DCBA,

    /// <summary>Byte-swapped big-endian (word-swap preserved): bytes [1, 0, 3, 2].</summary>
    BADC,

    /// <summary>Word-swapped big-endian: bytes [2, 3, 0, 1].</summary>
    CDAB,
}

/// <summary>
/// Pure conversion helpers for industrial protocols (Modbus, BACnet) (INT §10).
/// </summary>
public static class ValueConverters
{
    /// <summary>Scales and offsets a numeric value: <c>(raw * scale) + offset</c>.</summary>
    /// <param name="raw">The raw numeric input.</param>
    /// <param name="scale">The scale factor multiplier.</param>
    /// <param name="offset">The offset addend.</param>
    /// <returns>The scaled result.</returns>
    public static double ScaleAndOffset(double raw, double scale, double offset)
    {
        return (raw * scale) + offset;
    }

    /// <summary>Reorders 4 bytes according to the specified 32-bit byte order into standard ABCD (big-endian) layout.</summary>
    /// <param name="span">Span of 4 bytes in the specified byte order.</param>
    /// <param name="order">The byte order format of the input span.</param>
    /// <returns>A 4-byte array in standard big-endian (ABCD) layout.</returns>
    public static byte[] ToBigEndianBytes(ReadOnlySpan<byte> span, ByteOrder order)
    {
        if (span.Length < 4)
        {
            throw new ArgumentException("Span must contain at least 4 bytes.", nameof(span));
        }

        byte a, b, c, d;
        switch (order)
        {
            case ByteOrder.ABCD:
                a = span[0];
                b = span[1];
                c = span[2];
                d = span[3];
                break;
            case ByteOrder.DCBA:
                a = span[3];
                b = span[2];
                c = span[1];
                d = span[0];
                break;
            case ByteOrder.BADC:
                a = span[1];
                b = span[0];
                c = span[3];
                d = span[2];
                break;
            case ByteOrder.CDAB:
                a = span[2];
                b = span[3];
                c = span[0];
                d = span[1];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order, "Unsupported byte order.");
        }

        return [a, b, c, d];
    }

    /// <summary>Converts 4 bytes in the specified byte order to a 32-bit signed integer.</summary>
    /// <param name="span">Span containing 4 bytes.</param>
    /// <param name="order">The byte order.</param>
    /// <returns>The 32-bit signed integer.</returns>
    public static int ToInt32(ReadOnlySpan<byte> span, ByteOrder order)
    {
        var bytes = ToBigEndianBytes(span, order);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    /// <summary>Converts 4 bytes in the specified byte order to a 32-bit floating point number (IEEE 754).</summary>
    /// <param name="span">Span containing 4 bytes.</param>
    /// <param name="order">The byte order.</param>
    /// <returns>The 32-bit float.</returns>
    public static float ToSingle(ReadOnlySpan<byte> span, ByteOrder order)
    {
        var bytes = ToBigEndianBytes(span, order);
        return BinaryPrimitives.ReadSingleBigEndian(bytes);
    }

    /// <summary>Extracts a bit from an integer at the specified 0-based bit index.</summary>
    /// <param name="value">The source integer.</param>
    /// <param name="bitIndex">The 0-based bit index (0 to 31).</param>
    /// <returns><see langword="true"/> if the bit is 1; otherwise <see langword="false"/>.</returns>
    public static bool ExtractBit(int value, int bitIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitIndex, 31);

        return ((value >> bitIndex) & 1) == 1;
    }

    /// <summary>Extracts a range of bits from an integer.</summary>
    /// <param name="value">The source integer.</param>
    /// <param name="startBit">Starting bit index (0-based).</param>
    /// <param name="length">Number of bits to extract.</param>
    /// <returns>The extracted bits as an integer.</returns>
    public static int ExtractBits(int value, int startBit, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startBit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (startBit + length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "startBit + length cannot exceed 32 bits.");
        }

        var mask = (1 << length) - 1;
        return (value >> startBit) & mask;
    }

    /// <summary>Decodes a Binary Coded Decimal (BCD) byte (e.g. 0x42 -> 42).</summary>
    /// <param name="bcd">The BCD encoded byte.</param>
    /// <returns>The decoded decimal value.</returns>
    public static int DecodeBcd(byte bcd)
    {
        var high = (bcd >> 4) & 0x0F;
        var low = bcd & 0x0F;
        if (high > 9 || low > 9)
        {
            throw new ArgumentException($"Invalid BCD byte 0x{bcd:X2}: nibble exceeds 9.", nameof(bcd));
        }

        return (high * 10) + low;
    }

    /// <summary>Decodes 16-bit BCD encoded bytes (e.g. 0x1234 -> 1234).</summary>
    /// <param name="bcd">16-bit BCD value.</param>
    /// <returns>Decoded integer.</returns>
    public static int DecodeBcd16(ushort bcd)
    {
        var d1 = (bcd >> 12) & 0x0F;
        var d2 = (bcd >> 8) & 0x0F;
        var d3 = (bcd >> 4) & 0x0F;
        var d4 = bcd & 0x0F;

        if (d1 > 9 || d2 > 9 || d3 > 9 || d4 > 9)
        {
            throw new ArgumentException($"Invalid BCD-16 value 0x{bcd:X4}: nibble exceeds 9.", nameof(bcd));
        }

        return (d1 * 1000) + (d2 * 100) + (d3 * 10) + d4;
    }
}
