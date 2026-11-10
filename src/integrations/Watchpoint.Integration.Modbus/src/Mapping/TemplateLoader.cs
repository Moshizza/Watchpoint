using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchpoint.Integration.Modbus.Mapping;

/// <summary>Loads, validates, and expands Modbus mapping templates.</summary>
public static partial class TemplateLoader
{
    private static readonly JsonSerializerOptions _serializerOptions = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"\{\s*([^}]+)\s*\}")]
    private static partial Regex ExpressionRegex();

    /// <summary>Parses template JSON text into a strongly-typed <see cref="MappingTemplate"/>.</summary>
    public static MappingTemplate ParseTemplate(string json)
    {
        return JsonSerializer.Deserialize<MappingTemplate>(json, _serializerOptions)
            ?? throw new InvalidOperationException("Failed to deserialize template JSON.");
    }

    /// <summary>Resolves a Modicon or 0-based address to 0-based address and table.</summary>
    public static (ModbusTable Table, ushort Address) ResolveAddress(string tableStr, object addressObj, int index = 1)
    {
        var evaluatedAddress = EvaluateAddressExpression(addressObj, index);

        // Standard 6-digit or 5-digit Modicon addressing ranges
        if (evaluatedAddress >= 400001 && evaluatedAddress <= 465536)
            return (ModbusTable.Holding, (ushort)(evaluatedAddress - 400001));
        if (evaluatedAddress >= 40001 && evaluatedAddress <= 49999)
            return (ModbusTable.Holding, (ushort)(evaluatedAddress - 40001));

        if (evaluatedAddress >= 300001 && evaluatedAddress <= 365536)
            return (ModbusTable.Input, (ushort)(evaluatedAddress - 300001));
        if (evaluatedAddress >= 30001 && evaluatedAddress <= 39999)
            return (ModbusTable.Input, (ushort)(evaluatedAddress - 30001));

        if (evaluatedAddress >= 100001 && evaluatedAddress <= 165536)
            return (ModbusTable.DiscreteInput, (ushort)(evaluatedAddress - 100001));
        if (evaluatedAddress >= 10001 && evaluatedAddress <= 19999)
            return (ModbusTable.DiscreteInput, (ushort)(evaluatedAddress - 10001));

        if (tableStr.Equals("coil", StringComparison.OrdinalIgnoreCase))
        {
            // If explicit 1-based Modicon convention (e.g. 1..65536 in Modicon format), or 0-based protocol offset
            return (ModbusTable.Coil, (ushort)evaluatedAddress);
        }
        if (tableStr.Equals("discreteInput", StringComparison.OrdinalIgnoreCase))
            return (ModbusTable.DiscreteInput, (ushort)evaluatedAddress);
        if (tableStr.Equals("holding", StringComparison.OrdinalIgnoreCase))
            return (ModbusTable.Holding, (ushort)evaluatedAddress);
        if (tableStr.Equals("input", StringComparison.OrdinalIgnoreCase))
            return (ModbusTable.Input, (ushort)evaluatedAddress);

        // When table is not specified or 1-based 00001..65536 coil reference
        if (evaluatedAddress >= 1 && evaluatedAddress <= 65536)
            return (ModbusTable.Coil, (ushort)(evaluatedAddress - 1));

        return (ModbusTable.Holding, (ushort)evaluatedAddress);
    }

    /// <summary>Evaluates an arithmetic string expression containing {index}.</summary>
    public static int EvaluateAddressExpression(object addressObj, int index)
    {
        if (addressObj is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetInt32();
            addressObj = je.GetString() ?? "0";
        }

        var str = addressObj.ToString() ?? "0";
        var match = ExpressionRegex().Match(str);
        if (!match.Success)
        {
            return int.TryParse(str, out var num) ? num : 0;
        }

        var expr = match.Groups[1].Value.Replace("index", index.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).Trim();
        return EvaluateSimpleArithmetic(expr);
    }

    /// <summary>Expands templates by processing repeats, offsets, exclusions, and custom names.</summary>
    public static IReadOnlyList<(DeviceTemplate Device, IReadOnlyList<PointDefinition> Points)> ExpandTemplate(
        MappingTemplate template,
        int globalOffset = 0,
        string prefix = "",
        IReadOnlyDictionary<string, string>? customNames = null,
        ISet<string>? excludeKeys = null)
    {
        var result = new List<(DeviceTemplate, IReadOnlyList<PointDefinition>)>();

        foreach (var dev in template.Devices)
        {
            var repeatCount = dev.Repeat?.Count ?? 1;
            for (var idx = 1; idx <= repeatCount; idx++)
            {
                var expandedKey = dev.Key.Replace("{index}", idx.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
                var fullKey = string.IsNullOrEmpty(prefix) ? expandedKey : $"{prefix}{expandedKey}";

                if (excludeKeys?.Contains(fullKey) == true || excludeKeys?.Contains(expandedKey) == true)
                {
                    continue;
                }

                var points = new List<PointDefinition>();
                foreach (var pt in dev.Points)
                {
                    var (table, addr) = ResolveAddress(pt.Table, pt.Address, idx);
                    var effectiveAddr = (ushort)(addr + globalOffset);
                    var regCount = GetRegisterCount(pt.DataType);

                    points.Add(new PointDefinition(
                        pt.Property,
                        table,
                        effectiveAddr,
                        pt.DataType,
                        regCount,
                        pt.WordOrder ?? "bigEndian",
                        pt.ByteOrder ?? "bigEndian",
                        pt.Bit,
                        pt.Scale ?? 1m,
                        pt.Offset ?? 0m,
                        pt.EnumMap,
                        DeviceKey: fullKey));
                }

                var customName = customNames is not null && customNames.TryGetValue(fullKey, out var cn) ? cn : null;
                var names = dev.Name is null ? new Dictionary<string, string>() : new Dictionary<string, string>(dev.Name);
                if (customName is not null)
                {
                    names["en"] = customName;
                }
                else
                {
                    foreach (var lang in names.Keys.ToList())
                    {
                        names[lang] = names[lang].Replace("{index}", idx.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
                    }
                }

                var expandedDevice = dev with { Key = fullKey, Name = names };
                result.Add((expandedDevice, points));
            }
        }

        return result;
    }

    private static int GetRegisterCount(string dataType)
    {
        return dataType.ToLowerInvariant() switch
        {
            "bit" => 1,
            "int16" => 1,
            "uint16" => 1,
            "bcd16" => 1,
            "int32" => 2,
            "uint32" => 2,
            "float32" => 2,
            "bcd32" => 2,
            "int64" => 4,
            "uint64" => 4,
            "float64" => 4,
            _ => 1
        };
    }

    private static int EvaluateSimpleArithmetic(string expr)
    {
        expr = expr.Trim();
        while (expr.Contains('('))
        {
            var openIdx = expr.LastIndexOf('(');
            var closeIdx = expr.IndexOf(')', openIdx);
            if (closeIdx == -1) break;
            var subExpr = expr.Substring(openIdx + 1, closeIdx - openIdx - 1);
            var subVal = EvaluateSimpleArithmetic(subExpr);
            expr = string.Concat(expr.AsSpan(0, openIdx), subVal.ToString(CultureInfo.InvariantCulture), expr.AsSpan(closeIdx + 1));
        }

        if (expr.Contains('+'))
        {
            var lastIdx = expr.LastIndexOf('+');
            return EvaluateSimpleArithmetic(expr.Substring(0, lastIdx)) + EvaluateSimpleArithmetic(expr.Substring(lastIdx + 1));
        }
        if (expr.Contains('-') && expr.IndexOf('-') > 0)
        {
            var lastIdx = expr.LastIndexOf('-');
            return EvaluateSimpleArithmetic(expr.Substring(0, lastIdx)) - EvaluateSimpleArithmetic(expr.Substring(lastIdx + 1));
        }
        if (expr.Contains('*'))
        {
            var lastIdx = expr.LastIndexOf('*');
            return EvaluateSimpleArithmetic(expr.Substring(0, lastIdx)) * EvaluateSimpleArithmetic(expr.Substring(lastIdx + 1));
        }
        if (expr.Contains('/'))
        {
            var lastIdx = expr.LastIndexOf('/');
            return EvaluateSimpleArithmetic(expr.Substring(0, lastIdx)) / EvaluateSimpleArithmetic(expr.Substring(lastIdx + 1));
        }

        return int.Parse(expr.Trim(), CultureInfo.InvariantCulture);
    }
}
