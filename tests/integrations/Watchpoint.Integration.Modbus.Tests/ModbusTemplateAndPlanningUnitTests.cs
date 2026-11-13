using Shouldly;
using Watchpoint.Integration.Modbus.Engine;
using Watchpoint.Integration.Modbus.Mapping;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusTemplateAndPlanningUnitTests
{
    [Fact]
    public void MODB_U_015_ModiconAddressConversion()
    {
        var (t1, a1) = TemplateLoader.ResolveAddress("holding", 400010);
        t1.ShouldBe(ModbusTable.Holding);
        a1.ShouldBe((ushort)9);

        var (t2, a2) = TemplateLoader.ResolveAddress("input", 300005);
        t2.ShouldBe(ModbusTable.Input);
        a2.ShouldBe((ushort)4);

        var (t3, a3) = TemplateLoader.ResolveAddress("discreteInput", 100020);
        t3.ShouldBe(ModbusTable.DiscreteInput);
        a3.ShouldBe((ushort)19);

        var (t4, a4) = TemplateLoader.ResolveAddress("", 100);
        t4.ShouldBe(ModbusTable.Coil);
        a4.ShouldBe((ushort)99);
    }

    [Fact]
    public void MODB_U_016_Template_ArithmeticExpansion()
    {
        var val1 = TemplateLoader.EvaluateAddressExpression("{ (index - 1) * 2 }", 3);
        val1.ShouldBe(4);

        var val2 = TemplateLoader.EvaluateAddressExpression("{ index + 10 }", 5);
        val2.ShouldBe(15);
    }

    [Fact]
    public void MODB_U_017_Template_RepeatExpansion()
    {
        var tpl = new MappingTemplate(
            "test",
            new Dictionary<string, string> { ["en"] = "Test" },
            null,
            null,
            null,
            null,
            null,
            null,
            [
                new DeviceTemplate(
                    "sensor-{index}",
                    "generic.analog-input",
                    new RepeatDefinition(3, 1),
                    new Dictionary<string, string> { ["en"] = "Sensor {index}" },
                    [
                        new TemplatePoint("value", "holding", "{ index - 1 }", "int16", null, null, null, null, null, null)
                    ],
                    null)
            ]);

        var expanded = TemplateLoader.ExpandTemplate(tpl);
        expanded.Count.ShouldBe(3);
        expanded[0].Device.Key.ShouldBe("sensor-1");
        expanded[0].Points[0].Address.ShouldBe((ushort)0);
        expanded[1].Device.Key.ShouldBe("sensor-2");
        expanded[1].Points[0].Address.ShouldBe((ushort)1);
        expanded[2].Device.Key.ShouldBe("sensor-3");
        expanded[2].Points[0].Address.ShouldBe((ushort)2);
    }

    [Fact]
    public void MODB_U_018_Template_OffsetAndPrefix()
    {
        var tpl = new MappingTemplate(
            "test",
            new Dictionary<string, string> { ["en"] = "Test" },
            null,
            null,
            null,
            null,
            null,
            null,
            [
                new DeviceTemplate(
                    "meter",
                    "generic.analog-input",
                    null,
                    new Dictionary<string, string> { ["en"] = "Meter" },
                    [
                        new TemplatePoint("value", "holding", 10, "int16", null, null, null, null, null, null)
                    ],
                    null)
            ]);

        var expanded = TemplateLoader.ExpandTemplate(tpl, globalOffset: 100, prefix: "rack1_");
        expanded.Count.ShouldBe(1);
        expanded[0].Device.Key.ShouldBe("rack1_meter");
        expanded[0].Points[0].Address.ShouldBe((ushort)110);
    }

    [Fact]
    public void MODB_U_019_Template_ExcludeAndCustomNames()
    {
        var tpl = new MappingTemplate(
            "test",
            new Dictionary<string, string> { ["en"] = "Test" },
            null,
            null,
            null,
            null,
            null,
            null,
            [
                new DeviceTemplate(
                    "dev-{index}",
                    "generic.binary-input",
                    new RepeatDefinition(2, 1),
                    new Dictionary<string, string> { ["en"] = "Dev {index}" },
                    [new TemplatePoint("state", "coil", 0, "bit", null, null, null, null, null, null)],
                    null)
            ]);

        var exclude = new HashSet<string> { "dev-2" };
        var customNames = new Dictionary<string, string> { ["dev-1"] = "Main Breaker" };

        var expanded = TemplateLoader.ExpandTemplate(tpl, customNames: customNames, excludeKeys: exclude);
        expanded.Count.ShouldBe(1);
        expanded[0].Device.Key.ShouldBe("dev-1");
        expanded[0].Device.Name!["en"].ShouldBe("Main Breaker");
    }

    [Fact]
    public void MODB_U_020_ReadPlanner_CoalescesContiguous()
    {
        var points = new[]
        {
            new PointDefinition("p1", ModbusTable.Holding, 0, "uint16", 1),
            new PointDefinition("p2", ModbusTable.Holding, 1, "uint16", 1),
            new PointDefinition("p3", ModbusTable.Holding, 2, "uint16", 1)
        };

        var blocks = ReadPlanner.PlanReads(points);
        blocks.Count.ShouldBe(1);
        blocks[0].StartingAddress.ShouldBe((ushort)0);
        blocks[0].Quantity.ShouldBe((ushort)3);
    }

    [Fact]
    public void MODB_U_021_ReadPlanner_GapBridging()
    {
        var points = new[]
        {
            new PointDefinition("p1", ModbusTable.Holding, 0, "uint16", 1),
            new PointDefinition("p2", ModbusTable.Holding, 5, "uint16", 1)
        };

        var blocks = ReadPlanner.PlanReads(points, maxGap: 5);
        blocks.Count.ShouldBe(1);
        blocks[0].StartingAddress.ShouldBe((ushort)0);
        blocks[0].Quantity.ShouldBe((ushort)6);

        var splitBlocks = ReadPlanner.PlanReads(points, maxGap: 2);
        splitBlocks.Count.ShouldBe(2);
    }

    [Fact]
    public void MODB_U_022_ReadPlanner_MaxRegistersSplit()
    {
        var points = new[]
        {
            new PointDefinition("p1", ModbusTable.Holding, 0, "uint16", 1),
            new PointDefinition("p2", ModbusTable.Holding, 100, "uint16", 1)
        };

        var blocks = ReadPlanner.PlanReads(points, maxGap: 200, maxRegisters: 50);
        blocks.Count.ShouldBe(2);
    }

    [Fact]
    public void MODB_U_023_ReadPlanner_PreservesMultiRegisterBoundary()
    {
        var points = new[]
        {
            new PointDefinition("p1", ModbusTable.Holding, 0, "float32", 2),
            new PointDefinition("p2", ModbusTable.Holding, 2, "float32", 2)
        };

        var blocks = ReadPlanner.PlanReads(points, maxRegisters: 4);
        blocks.Count.ShouldBe(1);
        blocks[0].Quantity.ShouldBe((ushort)4);
    }
}
