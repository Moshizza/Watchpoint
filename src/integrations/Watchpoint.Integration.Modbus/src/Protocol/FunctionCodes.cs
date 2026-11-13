namespace Watchpoint.Integration.Modbus.Protocol;

/// <summary>Standard Modbus function codes.</summary>
public static class FunctionCodes
{
    /// <summary>Read Coils (FC 01).</summary>
    public const byte ReadCoils = 0x01;
    /// <summary>Read Discrete Inputs (FC 02).</summary>
    public const byte ReadDiscreteInputs = 0x02;
    /// <summary>Read Holding Registers (FC 03).</summary>
    public const byte ReadHoldingRegisters = 0x03;
    /// <summary>Read Input Registers (FC 04).</summary>
    public const byte ReadInputRegisters = 0x04;
    /// <summary>Write Single Coil (FC 05).</summary>
    public const byte WriteSingleCoil = 0x05;
    /// <summary>Write Single Register (FC 06).</summary>
    public const byte WriteSingleRegister = 0x06;
    /// <summary>Write Multiple Coils (FC 15).</summary>
    public const byte WriteMultipleCoils = 0x0F;
    /// <summary>Write Multiple Registers (FC 16).</summary>
    public const byte WriteMultipleRegisters = 0x10;
}
