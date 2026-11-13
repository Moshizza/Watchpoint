using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Watchpoint.Integration.Modbus.Mapping;

/// <summary>Decodes and encodes Modbus register and coil payloads.</summary>
public static class DataTypes
{
    /// <summary>Decodes raw register bytes according to dataType and formatting rules.</summary>
    public static object Decode(
        string dataType,
        ReadOnlySpan<byte> bytes,
        string wordOrder = "bigEndian",
        string byteOrder = "bigEndian",
        int? bit = null,
        decimal scale = 1m,
        decimal offset = 0m,
        IReadOnlyDictionary<string, string>? enumMap = null)
    {
        var isBigWord = !wordOrder.Equals("littleEndian", StringComparison.OrdinalIgnoreCase);
        var isBigByte = !byteOrder.Equals("littleEndian", StringComparison.OrdinalIgnoreCase);

        var ordered = ReorderBytes(bytes, isBigWord, isBigByte);

        switch (dataType.ToLowerInvariant())
        {
            case "bit":
                var reg = BinaryPrimitives.ReadUInt16BigEndian(ordered);
                var bitIndex = bit ?? 0;
                return (reg & (1 << bitIndex)) != 0;

            case "int16":
                var i16 = BinaryPrimitives.ReadInt16BigEndian(ordered);
                return ApplyScaleAndOffset(i16, scale, offset, enumMap);

            case "uint16":
                var u16 = BinaryPrimitives.ReadUInt16BigEndian(ordered);
                return ApplyScaleAndOffset(u16, scale, offset, enumMap);

            case "int32":
                var i32 = BinaryPrimitives.ReadInt32BigEndian(ordered);
                return ApplyScaleAndOffset(i32, scale, offset, enumMap);

            case "uint32":
                var u32 = BinaryPrimitives.ReadUInt32BigEndian(ordered);
                return ApplyScaleAndOffset(u32, scale, offset, enumMap);

            case "int64":
                var i64 = BinaryPrimitives.ReadInt64BigEndian(ordered);
                return ApplyScaleAndOffset(i64, scale, offset, enumMap);

            case "uint64":
                var u64 = BinaryPrimitives.ReadUInt64BigEndian(ordered);
                return ApplyScaleAndOffset(u64, scale, offset, enumMap);

            case "float32":
                var f32 = BinaryPrimitives.ReadSingleBigEndian(ordered);
                return ApplyScaleAndOffset((decimal)f32, scale, offset, enumMap);

            case "float64":
                var f64 = BinaryPrimitives.ReadDoubleBigEndian(ordered);
                return ApplyScaleAndOffset((decimal)f64, scale, offset, enumMap);

            case "bcd16":
                var bcd16 = DecodeBcd(ordered);
                return ApplyScaleAndOffset(bcd16, scale, offset, enumMap);

            case "bcd32":
                var bcd32 = DecodeBcd(ordered);
                return ApplyScaleAndOffset(bcd32, scale, offset, enumMap);

            case "stringascii":
                return Encoding.ASCII.GetString(ordered).TrimEnd('\0', ' ');

            case "stringutf8":
                return Encoding.UTF8.GetString(ordered).TrimEnd('\0', ' ');

            default:
                throw new NotSupportedException($"Data type '{dataType}' is not supported.");
        }
    }

    /// <summary>Encodes a high-level value back into register bytes.</summary>
    public static byte[] Encode(
        string dataType,
        object value,
        string wordOrder = "bigEndian",
        string byteOrder = "bigEndian",
        decimal scale = 1m,
        decimal offset = 0m,
        IReadOnlyDictionary<string, string>? enumMap = null)
    {
        var isBigWord = !wordOrder.Equals("littleEndian", StringComparison.OrdinalIgnoreCase);
        var isBigByte = !byteOrder.Equals("littleEndian", StringComparison.OrdinalIgnoreCase);

        var num = InvertScaleAndOffset(value, scale, offset, enumMap);

        byte[] raw;
        switch (dataType.ToLowerInvariant())
        {
            case "int16":
                raw = new byte[2];
                BinaryPrimitives.WriteInt16BigEndian(raw, (short)num);
                break;
            case "uint16":
                raw = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)num);
                break;
            case "int32":
                raw = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(raw, (int)num);
                break;
            case "uint32":
                raw = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(raw, (uint)num);
                break;
            case "float32":
                raw = new byte[4];
                BinaryPrimitives.WriteSingleBigEndian(raw, (float)num);
                break;
            case "float64":
                raw = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(raw, (double)num);
                break;
            default:
                throw new NotSupportedException($"Encode not supported for '{dataType}'.");
        }

        return ReorderBytes(raw, isBigWord, isBigByte);
    }

    private static byte[] ReorderBytes(ReadOnlySpan<byte> span, bool bigWord, bool bigByte)
    {
        var result = span.ToArray();

        if (!bigByte)
        {
            for (var i = 0; i < result.Length; i += 2)
            {
                if (i + 1 < result.Length)
                {
                    (result[i], result[i + 1]) = (result[i + 1], result[i]);
                }
            }
        }

        if (!bigWord && result.Length >= 4)
        {
            var words = new byte[result.Length / 2][];
            for (var i = 0; i < result.Length; i += 2)
            {
                words[i / 2] = [result[i], result[i + 1]];
            }
            Array.Reverse(words);
            for (var i = 0; i < words.Length; i++)
            {
                result[i * 2] = words[i][0];
                result[i * 2 + 1] = words[i][1];
            }
        }

        return result;
    }

    private static object ApplyScaleAndOffset(
        decimal raw,
        decimal scale,
        decimal offset,
        IReadOnlyDictionary<string, string>? enumMap)
    {
        var scaled = (raw * scale) + offset;
        if (enumMap is not null)
        {
            var keyStr = ((long)scaled).ToString(CultureInfo.InvariantCulture);
            if (enumMap.TryGetValue(keyStr, out var mapped))
            {
                return mapped;
            }
        }

        return scaled;
    }

    private static decimal InvertScaleAndOffset(
        object value,
        decimal scale,
        decimal offset,
        IReadOnlyDictionary<string, string>? enumMap)
    {
        if (enumMap is not null && value is string strVal)
        {
            foreach (var kvp in enumMap)
            {
                if (kvp.Value.Equals(strVal, StringComparison.OrdinalIgnoreCase))
                {
                    return decimal.Parse(kvp.Key, CultureInfo.InvariantCulture);
                }
            }
        }

        var dec = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (scale == 0m) scale = 1m;
        return (dec - offset) / scale;
    }

    private static long DecodeBcd(ReadOnlySpan<byte> bytes)
    {
        long result = 0;
        foreach (var b in bytes)
        {
            var high = (b >> 4) & 0x0F;
            var low = b & 0x0F;
            if (high > 9 || low > 9)
            {
                throw new FormatException($"Invalid BCD nibble in byte 0x{b:X2}.");
            }
            result = (result * 100) + (high * 10) + low;
        }
        return result;
    }
}
