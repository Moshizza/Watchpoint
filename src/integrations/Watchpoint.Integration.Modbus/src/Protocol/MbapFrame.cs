using System.Buffers.Binary;

namespace Watchpoint.Integration.Modbus.Protocol;

/// <summary>Encodes and decodes Modbus TCP MBAP frames.</summary>
public static class MbapFrame
{
    /// <summary>Encodes a Modbus request into an MBAP packet.</summary>
    public static byte[] Encode(ushort transactionId, byte unitId, ModbusPdu pdu)
    {
        var len = 1 + 1 + pdu.Data.Length;
        var buffer = new byte[6 + len];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), (ushort)len);
        buffer[6] = unitId;
        buffer[7] = pdu.FunctionCode;
        pdu.Data.Span.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    /// <summary>Tries to decode an MBAP packet from incoming span.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out ushort transactionId, out byte unitId, out ModbusPdu pdu, out int bytesConsumed)
    {
        transactionId = 0;
        unitId = 0;
        pdu = default;
        bytesConsumed = 0;

        if (buffer.Length < 7)
        {
            return false;
        }

        var len = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(4, 2));
        if (len < 2)
        {
            return false;
        }
        var totalFrameSize = 6 + len;
        if (buffer.Length < totalFrameSize)
        {
            return false;
        }

        transactionId = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
        unitId = buffer[6];
        var fc = buffer[7];
        var dataLength = len - 2;
        var data = buffer.Slice(8, dataLength).ToArray();

        pdu = new ModbusPdu(fc, data);
        bytesConsumed = totalFrameSize;
        return true;
    }
}
