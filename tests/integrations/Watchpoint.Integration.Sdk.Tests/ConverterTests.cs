using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Sdk.Mapping;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

public sealed class ConverterTests
{
    private sealed class FakeMetrics : IConnectorMetrics
    {
        public Dictionary<string, double> Gauges { get; } = [];

        public void CountRequest(string operation, bool success, TimeSpan duration) { }

        public void SetGauge(string name, double value)
        {
            lock (Gauges)
            {
                Gauges[name] = value;
            }
        }
    }

    [Fact]
    public void INT_U_026_EnumMap_unknown_vendor_code_fallback_and_counter()
    {
        var metrics = new FakeMetrics();
        var map = new EnumMap<string, string>(
            "test_status",
            new Dictionary<string, string>
            {
                ["OK"] = "normal",
                ["ALM"] = "alarm",
            },
            fallback: "unknown",
            metrics: metrics);

        map.Map("OK").ShouldBe("normal");
        map.Map("ALM").ShouldBe("alarm");
        map.UnknownCount.ShouldBe(0);

        map.Map("WEIRD_VENDOR_CODE").ShouldBe("unknown");
        map.UnknownCount.ShouldBe(1);
        metrics.Gauges["enum_map_test_status_unknown_total"].ShouldBe(1);

        map.Map("ANOTHER_UNKNOWN").ShouldBe("unknown");
        map.UnknownCount.ShouldBe(2);
        metrics.Gauges["enum_map_test_status_unknown_total"].ShouldBe(2);
    }

    [Fact]
    public void INT_U_027_ValueConverters_scaling_and_offset()
    {
        // raw 1234, scale 0.1, offset -40 -> 83.4
        var result = ValueConverters.ScaleAndOffset(1234, 0.1, -40);
        result.ShouldBe(83.4, tolerance: 0.0001);
    }

    [Fact]
    public void INT_U_028_ValueConverters_byte_orders_for_32bit_int_and_float()
    {
        // Test with integer 0x12345678 = 305419896
        // ABCD: [0x12, 0x34, 0x56, 0x78]
        // DCBA: [0x78, 0x56, 0x34, 0x12]
        // BADC: [0x34, 0x12, 0x78, 0x56]
        // CDAB: [0x56, 0x78, 0x12, 0x34]
        byte[] abcd = [0x12, 0x34, 0x56, 0x78];
        byte[] dcba = [0x78, 0x56, 0x34, 0x12];
        byte[] badc = [0x34, 0x12, 0x78, 0x56];
        byte[] cdab = [0x56, 0x78, 0x12, 0x34];

        const int expectedInt = 0x12345678;

        ValueConverters.ToInt32(abcd, ByteOrder.ABCD).ShouldBe(expectedInt);
        ValueConverters.ToInt32(dcba, ByteOrder.DCBA).ShouldBe(expectedInt);
        ValueConverters.ToInt32(badc, ByteOrder.BADC).ShouldBe(expectedInt);
        ValueConverters.ToInt32(cdab, ByteOrder.CDAB).ShouldBe(expectedInt);

        // Test with float: 12.34f
        // IEEE 754 for 12.34f is 0x414570A4
        byte[] floatAbcd = [0x41, 0x45, 0x70, 0xA4];
        byte[] floatDcba = [0xA4, 0x70, 0x45, 0x41];
        byte[] floatBadc = [0x45, 0x41, 0xA4, 0x70];
        byte[] floatCdab = [0x70, 0xA4, 0x41, 0x45];

        const float expectedFloat = 12.34f;

        ValueConverters.ToSingle(floatAbcd, ByteOrder.ABCD).ShouldBe(expectedFloat, tolerance: 0.0001f);
        ValueConverters.ToSingle(floatDcba, ByteOrder.DCBA).ShouldBe(expectedFloat, tolerance: 0.0001f);
        ValueConverters.ToSingle(floatBadc, ByteOrder.BADC).ShouldBe(expectedFloat, tolerance: 0.0001f);
        ValueConverters.ToSingle(floatCdab, ByteOrder.CDAB).ShouldBe(expectedFloat, tolerance: 0.0001f);
    }

    [Fact]
    public void INT_U_029_ValueConverters_bit_extraction_and_BCD()
    {
        // 0b10101100 = 172
        var val = 0b10101100;
        ValueConverters.ExtractBit(val, 0).ShouldBeFalse();
        ValueConverters.ExtractBit(val, 2).ShouldBeTrue();
        ValueConverters.ExtractBit(val, 3).ShouldBeTrue();
        ValueConverters.ExtractBit(val, 7).ShouldBeTrue();

        // Extract bits: start 2, length 4 -> bits 2..5: 0b1011 = 11
        ValueConverters.ExtractBits(val, 2, 4).ShouldBe(11);

        // BCD decoding: 0x42 -> 42
        ValueConverters.DecodeBcd(0x42).ShouldBe(42);
        ValueConverters.DecodeBcd(0x99).ShouldBe(99);
        ValueConverters.DecodeBcd(0x05).ShouldBe(5);
        Should.Throw<ArgumentException>(() => ValueConverters.DecodeBcd(0xFA));

        // 16-bit BCD decoding: 0x1234 -> 1234
        ValueConverters.DecodeBcd16(0x1234).ShouldBe(1234);
        ValueConverters.DecodeBcd16(0x9876).ShouldBe(9876);
        Should.Throw<ArgumentException>(() => ValueConverters.DecodeBcd16(0xA123));
    }
}
