using System.Text.Json.Serialization;

namespace Watchpoint.Integration.Modbus.Mapping;

/// <summary>Modbus table kind.</summary>
public enum ModbusTable
{
    /// <summary>Discrete output coils (FC 01, 05, 15).</summary>
    Coil,
    /// <summary>Discrete input contacts (FC 02).</summary>
    DiscreteInput,
    /// <summary>Input registers (FC 04).</summary>
    Input,
    /// <summary>Holding registers (FC 03, 06, 16).</summary>
    Holding
}

/// <summary>Represents a point definition within a device template.</summary>
public sealed record PointDefinition(
    string Property,
    ModbusTable Table,
    ushort Address,
    string DataType,
    int RegisterCount,
    string WordOrder = "bigEndian",
    string ByteOrder = "bigEndian",
    int? Bit = null,
    decimal Scale = 1m,
    decimal Offset = 0m,
    IReadOnlyDictionary<string, string>? EnumMap = null,
    string? DeviceKey = null);

/// <summary>Repeat configuration for expanding array devices.</summary>
public sealed record RepeatDefinition(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("step")] int Step,
    [property: JsonPropertyName("asArray")] bool AsArray = false);

/// <summary>Pulse write specification.</summary>
public sealed record PulseDefinition(
    [property: JsonPropertyName("thenAfterMs")] int ThenAfterMs,
    [property: JsonPropertyName("thenWrite")] IReadOnlyList<WriteActionDefinition> ThenWrite);

/// <summary>Command verification rule.</summary>
public sealed record VerifyDefinition(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("address")] object Address,
    [property: JsonPropertyName("expected")] object Expected,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs = 1000);

/// <summary>Write action item.</summary>
public sealed record WriteActionDefinition(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("address")] object Address,
    [property: JsonPropertyName("value")] object Value);

/// <summary>Command definition.</summary>
public sealed record CommandDefinition(
    [property: JsonPropertyName("write")] IReadOnlyList<WriteActionDefinition> Write,
    [property: JsonPropertyName("pulse")] PulseDefinition? Pulse = null,
    [property: JsonPropertyName("verify")] VerifyDefinition? Verify = null);

/// <summary>Raw point in template JSON.</summary>
public sealed record TemplatePoint(
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("address")] object Address,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("wordOrder")] string? WordOrder,
    [property: JsonPropertyName("byteOrder")] string? ByteOrder,
    [property: JsonPropertyName("bit")] int? Bit,
    [property: JsonPropertyName("scale")] decimal? Scale,
    [property: JsonPropertyName("offset")] decimal? Offset,
    [property: JsonPropertyName("enumMap")] Dictionary<string, string>? EnumMap);

/// <summary>Device block in template JSON.</summary>
public sealed record DeviceTemplate(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("repeat")] RepeatDefinition? Repeat,
    [property: JsonPropertyName("name")] Dictionary<string, string>? Name,
    [property: JsonPropertyName("points")] IReadOnlyList<TemplatePoint> Points,
    [property: JsonPropertyName("commands")] Dictionary<string, CommandDefinition>? Commands);

/// <summary>Root mapping template structure.</summary>
public sealed record MappingTemplate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] Dictionary<string, string> Name,
    [property: JsonPropertyName("description")] Dictionary<string, string>? Description,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("defaultPollIntervalMs")] int? DefaultPollIntervalMs,
    [property: JsonPropertyName("defaultMaxGap")] int? DefaultMaxGap,
    [property: JsonPropertyName("defaultMaxRegisters")] int? DefaultMaxRegisters,
    [property: JsonPropertyName("devices")] IReadOnlyList<DeviceTemplate> Devices);
