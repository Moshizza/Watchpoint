using System.Buffers.Binary;

namespace Watchpoint.Integration.Modbus.Protocol;

/// <summary>Encodes and decodes Modbus RTU-over-TCP frames with CRC-16 check.</summary>
public static class RtuOverTcpFrame
{
    private static readonly ushort[] _crcTable = GenerateCrcTable();

    private static ushort[] GenerateCrcTable()
    {
        var table = new ushort[256];
        for (ushort i = 0; i < 256; i++)
        {
            var value = i;
            for (var j = 0; j < 8; j++)
            {
                if ((value & 1) != 0)
                {
                    value = (ushort)((value >> 1) ^ 0xA001);
                }
                else
                {
                    value >>= 1;
                }
            }
            table[i] = value;
        }
        return table;
    }

    /// <summary>Calculates the standard Modbus CRC-16.</summary>
    public static ushort CalculateCrc(ReadOnlySpan<byte> buffer)
    {
        ushort crc = 0xFFFF;
        foreach (var b in buffer)
        {
            var index = (byte)(crc ^ b);
            crc = (ushort)((crc >> 8) ^ _crcTable[index]);
        }
        return crc;
    }

    /// <summary>Encodes a request into an RTU frame with CRC-16.</summary>
    public static byte[] Encode(byte unitId, ModbusPdu pdu)
    {
        var bodyLen = 1 + 1 + pdu.Data.Length;
        var frame = new byte[bodyLen + 2];
        frame[0] = unitId;
        frame[1] = pdu.FunctionCode;
        pdu.Data.Span.CopyTo(frame.AsSpan(2));

        var crc = CalculateCrc(frame.AsSpan(0, bodyLen));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(bodyLen, 2), crc);
        return frame;
    }

    /// <summary>Tries to decode an RTU frame from incoming stream.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, byte expectedUnitId, byte requestedFc, out ModbusPdu pdu, out int bytesConsumed, bool isResponse = true)
    {
        pdu = default;
        bytesConsumed = 0;

        if (buffer.Length < 4)
        {
            return false;
        }

        var fc = buffer[1];
        int expectedBodyLength;

        if ((fc & 0x80) != 0)
        {
            expectedBodyLength = 3;
        }
        else
        {
            switch (fc)
            {
                case FunctionCodes.ReadCoils:
                case FunctionCodes.ReadDiscreteInputs:
                case FunctionCodes.ReadHoldingRegisters:
                case FunctionCodes.ReadInputRegisters:
                    if (isResponse)
                    {
                        var byteCount = buffer[2];
                        expectedBodyLength = 1 + 1 + 1 + byteCount;
                    }
                    else
                    {
                        expectedBodyLength = 1 + 1 + 4;
                    }
                    break;
                case FunctionCodes.WriteSingleCoil:
                case FunctionCodes.WriteSingleRegister:
                    expectedBodyLength = 1 + 1 + 4;
                    break;
                case FunctionCodes.WriteMultipleCoils:
                case FunctionCodes.WriteMultipleRegisters:
                    if (isResponse)
                    {
                        expectedBodyLength = 1 + 1 + 4;
                    }
                    else
                    {
                        var byteCount = buffer[6];
                        expectedBodyLength = 1 + 1 + 4 + 1 + byteCount;
                    }
                    break;
                default:
                    expectedBodyLength = buffer.Length - 2;
                    break;
            }
        }

        var totalLen = expectedBodyLength + 2;
        if (buffer.Length < totalLen)
        {
            return false;
        }

        var expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(expectedBodyLength, 2));
        var actualCrc = CalculateCrc(buffer.Slice(0, expectedBodyLength));

        if (expectedCrc != actualCrc)
        {
            bytesConsumed = 1;
            return false;
        }

        var dataBytes = buffer.Slice(2, expectedBodyLength - 2).ToArray();
        pdu = new ModbusPdu(fc, dataBytes);
        bytesConsumed = totalLen;
        return true;
    }
}
