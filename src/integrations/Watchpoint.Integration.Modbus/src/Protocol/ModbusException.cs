namespace Watchpoint.Integration.Modbus.Protocol;

/// <summary>Represents a Modbus protocol exception response from a remote device.</summary>
public sealed class ModbusException : Exception
{
    /// <summary>Original function code that caused the exception.</summary>
    public byte FunctionCode { get; }

    /// <summary>Modbus exception code returned by the device.</summary>
    public byte ExceptionCode { get; }

    /// <summary>Initializes a new instance of the <see cref="ModbusException"/> class.</summary>
    public ModbusException(byte functionCode, byte exceptionCode)
        : base($"Modbus exception 0x{exceptionCode:X2} returned for function 0x{functionCode:X2}.")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }
}
