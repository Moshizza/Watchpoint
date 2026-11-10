using Shouldly;
using Watchpoint.Integration.Modbus.Mapping;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusDataTypesUnitTests
{
    [Fact]
    public void MODB_U_007_DataTypes_BitHoldingRegister()
    {
        var bytes = new byte[] { 0x00, 0x04 };
        var result = DataTypes.Decode("bit", bytes, bit: 2);
        result.ShouldBe(true);

        var resultFalse = DataTypes.Decode("bit", bytes, bit: 3);
        resultFalse.ShouldBe(false);
    }

    [Fact]
    public void MODB_U_008_DataTypes_Int16_UInt16()
    {
        var signedBytes = new byte[] { 0xFF, 0xFE };
        var i16 = DataTypes.Decode("int16", signedBytes);
        i16.ShouldBe(-2m);

        var unsignedBytes = new byte[] { 0xFF, 0xFE };
        var u16 = DataTypes.Decode("uint16", unsignedBytes);
        u16.ShouldBe(65534m);
    }

    [Fact]
    public void MODB_U_009_DataTypes_Int32_WordOrders()
    {
        var bigEndian = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        DataTypes.Decode("int32", bigEndian, wordOrder: "bigEndian").ShouldBe((decimal)0x12345678);

        var littleEndian = new byte[] { 0x56, 0x78, 0x12, 0x34 };
        DataTypes.Decode("int32", littleEndian, wordOrder: "littleEndian").ShouldBe((decimal)0x12345678);
    }

    [Fact]
    public void MODB_U_010_DataTypes_Float32_ByteOrders()
    {
        var bytes = new byte[] { 0x42, 0xF6, 0xE9, 0x79 };
        var val = (decimal)DataTypes.Decode("float32", bytes, byteOrder: "bigEndian");
        Math.Round(val, 2).ShouldBe(123.46m);

        var swapped = new byte[] { 0xF6, 0x42, 0x79, 0xE9 };
        var valSwapped = (decimal)DataTypes.Decode("float32", swapped, byteOrder: "littleEndian");
        Math.Round(valSwapped, 2).ShouldBe(123.46m);
    }

    [Fact]
    public void MODB_U_011_DataTypes_Bcd16_Bcd32()
    {
        var bcd16Bytes = new byte[] { 0x12, 0x34 };
        DataTypes.Decode("bcd16", bcd16Bytes).ShouldBe(1234m);

        var bcd32Bytes = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        DataTypes.Decode("bcd32", bcd32Bytes).ShouldBe(12345678m);

        var invalidBcd = new byte[] { 0x1A, 0x34 };
        Should.Throw<FormatException>(() => DataTypes.Decode("bcd16", invalidBcd));
    }

    [Fact]
    public void MODB_U_012_DataTypes_Strings_AsciiUtf8()
    {
        var ascii = new byte[] { 0x57, 0x50, 0x30, 0x31, 0x00, 0x00 };
        DataTypes.Decode("stringascii", ascii).ShouldBe("WP01");

        var utf8 = new byte[] { 0xC3, 0xA9, 0x6C, 0x65, 0x63 };
        DataTypes.Decode("stringutf8", utf8).ShouldBe("élec");
    }

    [Fact]
    public void MODB_U_013_DataTypes_ScaleAndOffset()
    {
        var rawBytes = new byte[] { 0x00, 0x64 };
        var scaled = DataTypes.Decode("uint16", rawBytes, scale: 0.1m, offset: -5.0m);
        scaled.ShouldBe(5.0m);
    }

    [Fact]
    public void MODB_U_014_DataTypes_EnumMap()
    {
        var enumMap = new Dictionary<string, string>
        {
            ["0"] = "normal",
            ["1"] = "warning",
            ["2"] = "alarm"
        };
        var rawBytes = new byte[] { 0x00, 0x02 };
        var mapped = DataTypes.Decode("uint16", rawBytes, enumMap: enumMap);
        mapped.ShouldBe("alarm");
    }
}
