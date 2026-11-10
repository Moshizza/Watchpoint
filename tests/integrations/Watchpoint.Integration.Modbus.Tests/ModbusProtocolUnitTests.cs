using System.Buffers.Binary;
using Shouldly;
using Watchpoint.Integration.Modbus.Protocol;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusProtocolUnitTests
{
    [Fact]
    public void MODB_U_001_MbapFrame_EncodeAndDecode()
    {
        var pdu = new ModbusPdu(FunctionCodes.ReadHoldingRegisters, new byte[] { 0x00, 0x01, 0x00, 0x0A });
        var encoded = MbapFrame.Encode(1234, 1, pdu);

        encoded.Length.ShouldBe(12);
        BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(0, 2)).ShouldBe((ushort)1234);
        BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(2, 2)).ShouldBe((ushort)0);
        BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(4, 2)).ShouldBe((ushort)6);
        encoded[6].ShouldBe((byte)1);
        encoded[7].ShouldBe(FunctionCodes.ReadHoldingRegisters);

        var ok = MbapFrame.TryDecode(encoded, out var tid, out var uid, out var decodedPdu, out var consumed);
        ok.ShouldBeTrue();
        tid.ShouldBe((ushort)1234);
        uid.ShouldBe((byte)1);
        consumed.ShouldBe(12);
        decodedPdu.FunctionCode.ShouldBe(FunctionCodes.ReadHoldingRegisters);
        decodedPdu.Data.ToArray().ShouldBe(new byte[] { 0x00, 0x01, 0x00, 0x0A });
    }

    [Fact]
    public void MODB_U_002_MbapFrame_IncompleteData()
    {
        var pdu = new ModbusPdu(FunctionCodes.ReadHoldingRegisters, new byte[] { 0x00, 0x01, 0x00, 0x0A });
        var encoded = MbapFrame.Encode(1, 1, pdu);

        var incomplete = encoded.AsSpan(0, 5);
        var ok = MbapFrame.TryDecode(incomplete, out _, out _, out _, out _);
        ok.ShouldBeFalse();
    }

    [Fact]
    public void MODB_U_003_RtuOverTcpFrame_CrcValid()
    {
        var pdu = new ModbusPdu(FunctionCodes.ReadHoldingRegisters, new byte[] { 0x00, 0x01, 0x00, 0x0A });
        var frame = RtuOverTcpFrame.Encode(1, pdu);

        frame.Length.ShouldBe(8);
        var ok = RtuOverTcpFrame.TryDecode(frame, 1, FunctionCodes.ReadHoldingRegisters, out var decodedPdu, out var consumed, isResponse: false);
        ok.ShouldBeTrue();
        consumed.ShouldBe(8);
        decodedPdu.FunctionCode.ShouldBe(FunctionCodes.ReadHoldingRegisters);
        decodedPdu.Data.ToArray().ShouldBe(new byte[] { 0x00, 0x01, 0x00, 0x0A });
    }

    [Fact]
    public void MODB_U_004_RtuOverTcpFrame_CorruptCrcRejected()
    {
        var pdu = new ModbusPdu(FunctionCodes.ReadHoldingRegisters, new byte[] { 0x00, 0x01, 0x00, 0x0A });
        var frame = RtuOverTcpFrame.Encode(1, pdu);
        frame[^1] ^= 0xFF;

        var ok = RtuOverTcpFrame.TryDecode(frame, 1, FunctionCodes.ReadHoldingRegisters, out _, out var consumed);
        ok.ShouldBeFalse();
        consumed.ShouldBe(1);
    }

    [Fact]
    public void MODB_U_005_ModbusException_Decode()
    {
        var ex = new ModbusException(FunctionCodes.ReadHoldingRegisters, 0x02);
        ex.FunctionCode.ShouldBe(FunctionCodes.ReadHoldingRegisters);
        ex.ExceptionCode.ShouldBe((byte)0x02);
        ex.Message.ShouldContain("0x02");
    }

    [Fact]
    public void MODB_U_006_ModbusPdu_BuildWriteMultipleRegisters()
    {
        var registers = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var pdu = ModbusPdu.BuildWriteMultipleRegisters(100, registers);

        pdu.FunctionCode.ShouldBe(FunctionCodes.WriteMultipleRegisters);
        pdu.Data.Length.ShouldBe(9);
        BinaryPrimitives.ReadUInt16BigEndian(pdu.Data.Span.Slice(0, 2)).ShouldBe((ushort)100);
        BinaryPrimitives.ReadUInt16BigEndian(pdu.Data.Span.Slice(2, 2)).ShouldBe((ushort)2);
        pdu.Data.Span[4].ShouldBe((byte)4);
        pdu.Data.Span.Slice(5, 4).ToArray().ShouldBe(registers);
    }
}
