using System.Buffers.Binary;

namespace Watchpoint.Integration.Modbus.Protocol;

/// <summary>Represents a Modbus Protocol Data Unit (PDU).</summary>
public readonly struct ModbusPdu : IEquatable<ModbusPdu>
{
    /// <summary>The Modbus function code.</summary>
    public byte FunctionCode { get; }

    /// <summary>Function data bytes.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Initializes a new Modbus PDU.</summary>
    public ModbusPdu(byte functionCode, ReadOnlyMemory<byte> data)
    {
        FunctionCode = functionCode;
        Data = data;
    }

    /// <summary>Checks whether this PDU represents an error response.</summary>
    public bool IsError => (FunctionCode & 0x80) != 0;

    /// <summary>Builds a standard read request PDU.</summary>
    public static ModbusPdu BuildReadRequest(byte functionCode, ushort startingAddress, ushort quantity)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), startingAddress);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), quantity);
        return new ModbusPdu(functionCode, buffer);
    }

    /// <summary>Builds a Write Single Coil (FC 05) PDU.</summary>
    public static ModbusPdu BuildWriteSingleCoil(ushort address, bool value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)(value ? 0xFF00 : 0x0000));
        return new ModbusPdu(FunctionCodes.WriteSingleCoil, buffer);
    }

    /// <summary>Builds a Write Single Register (FC 06) PDU.</summary>
    public static ModbusPdu BuildWriteSingleRegister(ushort address, ushort value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), value);
        return new ModbusPdu(FunctionCodes.WriteSingleRegister, buffer);
    }

    /// <summary>Builds a Write Multiple Coils (FC 15) PDU.</summary>
    public static ModbusPdu BuildWriteMultipleCoils(ushort address, IReadOnlyList<bool> values)
    {
        var byteCount = (values.Count + 7) / 8;
        var buffer = new byte[5 + byteCount];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), (ushort)values.Count);
        buffer[4] = (byte)byteCount;

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i])
            {
                buffer[5 + (i / 8)] |= (byte)(1 << (i % 8));
            }
        }

        return new ModbusPdu(FunctionCodes.WriteMultipleCoils, buffer);
    }

    /// <summary>Builds a Write Multiple Registers (FC 16) PDU.</summary>
    public static ModbusPdu BuildWriteMultipleRegisters(ushort address, ReadOnlySpan<byte> registers)
    {
        var qty = (ushort)(registers.Length / 2);
        var buffer = new byte[5 + registers.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), qty);
        buffer[4] = (byte)registers.Length;
        registers.CopyTo(buffer.AsSpan(5));
        return new ModbusPdu(FunctionCodes.WriteMultipleRegisters, buffer);
    }

    /// <inheritdoc />
    public bool Equals(ModbusPdu other) => FunctionCode == other.FunctionCode && Data.Span.SequenceEqual(other.Data.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ModbusPdu other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FunctionCode, Data.Length);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ModbusPdu left, ModbusPdu right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ModbusPdu left, ModbusPdu right) => !left.Equals(right);
}
