using System.Globalization;
using System.Text;
using System.Text.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Internal;

/// <summary>Implements value and argument validation (spec MOD §4.3).</summary>
internal sealed class ValueValidator : IValueValidator
{
    private const int MaxListItems = 1000;
    private const int MaxObjectBytes = 16 * 1024;

    public Result<WpValue> ValidateProperty(PropertyDefinition property, WpValue value, string? reportedUnit = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ValidateValue(property.ValueType, property, value, reportedUnit, property.Path.Value);
    }

    public Result<IReadOnlyDictionary<string, WpValue>> ValidateArguments(CommandDefinition command, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ValidateSchema(command.Parameters, arguments, command.Path.Value);
    }

    public Result<IReadOnlyDictionary<string, WpValue>> ValidatePayload(EventDefinition evt, IReadOnlyDictionary<string, WpValue> payload)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(payload);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (k, v) in payload)
            {
                writer.WritePropertyName(k);
                WriteWpValue(writer, v);
            }

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return ValidateSchema(evt.Payload, doc.RootElement, evt.Path.Value);
    }

    public Result<IReadOnlyDictionary<string, WpValue>> ValidateAttributes(DeviceTypeDefinition type, JsonElement attributes)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ValidateSchema(type.Attributes, attributes, type.Ref.Id.Value);
    }

    private static Result<IReadOnlyDictionary<string, WpValue>> ValidateSchema(
        ParameterSchema schema,
        JsonElement element,
        string contextPath)
    {
        if (element.ValueKind != JsonValueKind.Object && element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null)
        {
            return ModelErrors.TypeMismatchError("object", element.ValueKind.ToString().ToLowerInvariant(), contextPath);
        }

        var properties = GetSchemaProperties(schema);
        var required = GetSchemaRequired(schema);
        var resultDict = new Dictionary<string, WpValue>(StringComparer.Ordinal);
        var errors = new List<FieldError>();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var req in required)
            {
                if (!element.TryGetProperty(req, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    if (properties.TryGetValue(req, out var propDef) && propDef.TryGetProperty("default", out _))
                    {
                        // Will apply default below
                    }
                    else
                    {
                        errors.Add(CommonErrors.RequiredAt($"/{req}"));
                    }
                }
            }
        }
        else
        {
            foreach (var req in required)
            {
                if (!properties.TryGetValue(req, out var propDef) || !propDef.TryGetProperty("default", out _))
                {
                    errors.Add(CommonErrors.RequiredAt($"/{req}"));
                }
            }
        }

        foreach (var (propName, propDef) in properties)
        {
            var fieldPath = $"/{propName}";
            JsonElement propValue = default;
            var hasValue = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propName, out propValue) && propValue.ValueKind != JsonValueKind.Null && propValue.ValueKind != JsonValueKind.Undefined;

            if (!hasValue)
            {
                if (propDef.TryGetProperty("default", out var defaultVal))
                {
                    var parsedDefault = ParseJsonElementToWpValue(propDef, defaultVal, fieldPath);
                    if (parsedDefault.TryGetValue(out var defWp, out var defErr))
                    {
                        resultDict[propName] = defWp;
                    }
                    else
                    {
                        errors.AddRange(defErr.FieldErrors);
                    }
                }

                continue;
            }

            var parsed = ParseJsonElementToWpValue(propDef, propValue, fieldPath);
            if (parsed.TryGetValue(out var wp, out var err))
            {
                resultDict[propName] = wp;
            }
            else
            {
                errors.AddRange(err.FieldErrors);
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation(ModelErrors.DocumentInvalid.Value, errors);
        }

        return resultDict;
    }

    private static Result<WpValue> ParseJsonElementToWpValue(JsonElement schema, JsonElement value, string fieldPath)
    {
        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;

        IReadOnlyList<string>? allowed = null;
        if (schema.TryGetProperty("enum", out var enumArr) && enumArr.ValueKind == JsonValueKind.Array)
        {
            allowed = enumArr.EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        switch (type)
        {
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return ModelErrors.TypeMismatchError("boolean", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                }

                return WpValue.Bool(value.GetBoolean());

            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var i))
                {
                    return ModelErrors.TypeMismatchError("integer", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                }

                if (schema.TryGetProperty("minimum", out var minInt) && i < minInt.GetInt64())
                {
                    return ModelErrors.OutOfRangeError(minInt.GetInt64(), schema.TryGetProperty("maximum", out var max1) ? max1.GetInt64() : null, fieldPath);
                }

                if (schema.TryGetProperty("maximum", out var maxInt) && i > maxInt.GetInt64())
                {
                    return ModelErrors.OutOfRangeError(schema.TryGetProperty("minimum", out var min1) ? min1.GetInt64() : null, maxInt.GetInt64(), fieldPath);
                }

                return WpValue.Int(i);

            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var dec))
                {
                    return ModelErrors.TypeMismatchError("number", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                }

                if (schema.TryGetProperty("minimum", out var minDec) && dec < minDec.GetDecimal())
                {
                    return ModelErrors.OutOfRangeError(minDec.GetDecimal(), schema.TryGetProperty("maximum", out var max2) ? max2.GetDecimal() : null, fieldPath);
                }

                if (schema.TryGetProperty("maximum", out var maxDec) && dec > maxDec.GetDecimal())
                {
                    return ModelErrors.OutOfRangeError(schema.TryGetProperty("minimum", out var min2) ? min2.GetDecimal() : null, maxDec.GetDecimal(), fieldPath);
                }

                return WpValue.Decimal(dec);

            case "duration":
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return ModelErrors.TypeMismatchError("duration", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                    }

                    var durStr = value.GetString()!;
                    try
                    {
                        var dur = System.Xml.XmlConvert.ToTimeSpan(durStr);
                        if (schema.TryGetProperty("minimum", out var minDur) && dur < System.Xml.XmlConvert.ToTimeSpan(minDur.GetString()!))
                        {
                            return ModelErrors.OutOfRangeError(minDur.GetString(), schema.TryGetProperty("maximum", out var maxD) ? maxD.GetString() : null, fieldPath);
                        }

                        if (schema.TryGetProperty("maximum", out var maxDur) && dur > System.Xml.XmlConvert.ToTimeSpan(maxDur.GetString()!))
                        {
                            return ModelErrors.OutOfRangeError(schema.TryGetProperty("minimum", out var minD) ? minD.GetString() : null, maxDur.GetString(), fieldPath);
                        }

                        return WpValue.Duration(dur);
                    }
                    catch
                    {
                        return CommonErrors.InvalidFormat("duration");
                    }
                }

            case "timestamp":
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        return ModelErrors.TypeMismatchError("timestamp", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                    }

                    var tsStr = value.GetString()!;
                    if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    {
                        return WpValue.Timestamp(dt.ToUniversalTime());
                    }

                    return CommonErrors.InvalidFormat("timestamp");
                }

            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return ModelErrors.TypeMismatchError("string", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                }

                var s = value.GetString()!;
                if (allowed is not null && !allowed.Contains(s, StringComparer.Ordinal))
                {
                    return ModelErrors.EnumUnknownError(s, allowed, fieldPath);
                }

                s = s.Normalize(NormalizationForm.FormC);
                if (schema.TryGetProperty("maxLength", out var maxLen) && s.Length > maxLen.GetInt32())
                {
                    return ModelErrors.TooLongError(maxLen.GetInt32(), fieldPath);
                }

                return allowed is not null ? WpValue.Enum(s) : WpValue.String(s);

            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return ModelErrors.TypeMismatchError("array", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                }

                var list = new List<WpValue>();
                var itemSchema = schema.TryGetProperty("items", out var itemsProp) ? itemsProp : default;
                var idx = 0;
                foreach (var itemElem in value.EnumerateArray())
                {
                    var itemParsed = ParseJsonElementToWpValue(itemSchema, itemElem, $"{fieldPath}/{idx}");
                    if (!itemParsed.TryGetValue(out var parsedItem, out var itemErr))
                    {
                        return itemErr;
                    }

                    list.Add(parsedItem);
                    idx++;
                }

                return WpValue.List(list);

            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return ModelErrors.TypeMismatchError("object", value.ValueKind.ToString().ToLowerInvariant(), fieldPath);
                }

                return WpValue.Object(value);

            default:
                if (value.ValueKind == JsonValueKind.String)
                {
                    return WpValue.String(value.GetString()!.Normalize(NormalizationForm.FormC));
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var defInt))
                {
                    return WpValue.Int(defInt);
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var defDec))
                {
                    return WpValue.Decimal(defDec);
                }

                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return WpValue.Bool(value.GetBoolean());
                }

                return WpValue.Object(value);
        }
    }

    private static Result<WpValue> ValidateValue(
        string expectedType,
        PropertyDefinition? propDef,
        WpValue value,
        string? reportedUnit,
        string path)
    {
        switch (expectedType)
        {
            case "boolean":
                if (value.Kind != WpValueKind.Bool)
                {
                    return ModelErrors.TypeMismatchError("boolean", value.Kind.ToString().ToLowerInvariant(), path);
                }

                return value;

            case "integer":
                long intVal;
                if (value.Kind == WpValueKind.Int)
                {
                    intVal = value.AsInt();
                }
                else if (value.Kind == WpValueKind.Decimal)
                {
                    var decVal = value.AsDecimal();
                    if (decimal.Truncate(decVal) != decVal)
                    {
                        return ModelErrors.TypeMismatchError("integer", "decimal", path);
                    }

                    intVal = (long)decVal;
                }
                else
                {
                    return ModelErrors.TypeMismatchError("integer", value.Kind.ToString().ToLowerInvariant(), path);
                }

                if (propDef?.Range is { } intRange)
                {
                    if (intRange.Min.HasValue && intVal < intRange.Min.Value)
                    {
                        return ModelErrors.OutOfRangeError(intRange.Min.Value, intRange.Max, path);
                    }

                    if (intRange.Max.HasValue && intVal > intRange.Max.Value)
                    {
                        return ModelErrors.OutOfRangeError(intRange.Min, intRange.Max.Value, path);
                    }
                }

                return WpValue.Int(intVal);

            case "decimal":
                decimal decValue;
                if (value.Kind == WpValueKind.Decimal)
                {
                    decValue = value.AsDecimal();
                }
                else if (value.Kind == WpValueKind.Int)
                {
                    decValue = value.AsInt();
                }
                else
                {
                    return ModelErrors.TypeMismatchError("decimal", value.Kind.ToString().ToLowerInvariant(), path);
                }

                if (reportedUnit is not null && propDef?.Unit is not null)
                {
                    var converted = UnitConverter.Convert(decValue, reportedUnit, propDef.Unit, path);
                    if (!converted.TryGetValue(out var convertedVal, out var unitErr))
                    {
                        return unitErr;
                    }

                    decValue = convertedVal;
                }

                if (propDef?.Range is { } decRange)
                {
                    if (decRange.Min.HasValue && (double)decValue < decRange.Min.Value)
                    {
                        return ModelErrors.OutOfRangeError(decRange.Min.Value, decRange.Max, path);
                    }

                    if (decRange.Max.HasValue && (double)decValue > decRange.Max.Value)
                    {
                        return ModelErrors.OutOfRangeError(decRange.Min, decRange.Max.Value, path);
                    }
                }

                return WpValue.Decimal(decValue);

            case "enum":
                string code;
                if (value.Kind == WpValueKind.Enum)
                {
                    code = value.AsEnum();
                }
                else if (value.Kind == WpValueKind.String)
                {
                    code = value.AsString();
                }
                else
                {
                    return ModelErrors.TypeMismatchError("enum", value.Kind.ToString().ToLowerInvariant(), path);
                }

                if (propDef is not null)
                {
                    var allowed = propDef.EnumValues.Select(e => e.Code).ToList();
                    if (!allowed.Contains(code, StringComparer.Ordinal))
                    {
                        return ModelErrors.EnumUnknownError(code, allowed, path);
                    }
                }

                return WpValue.Enum(code);

            case "string":
                if (value.Kind != WpValueKind.String)
                {
                    return ModelErrors.TypeMismatchError("string", value.Kind.ToString().ToLowerInvariant(), path);
                }

                var normalized = value.AsString().Normalize(NormalizationForm.FormC);
                if (propDef?.MaxLength is { } maxLen && normalized.Length > maxLen)
                {
                    return ModelErrors.TooLongError(maxLen, path);
                }

                return WpValue.String(normalized);

            case "timestamp":
                if (value.Kind != WpValueKind.Timestamp)
                {
                    return ModelErrors.TypeMismatchError("timestamp", value.Kind.ToString().ToLowerInvariant(), path);
                }

                var ts = value.AsTimestamp().ToUniversalTime();
                var truncated = new DateTimeOffset(
                    ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, ts.Second, ts.Millisecond, TimeSpan.Zero);
                return WpValue.Timestamp(truncated);

            case "duration":
                if (value.Kind != WpValueKind.Duration)
                {
                    return ModelErrors.TypeMismatchError("duration", value.Kind.ToString().ToLowerInvariant(), path);
                }

                return value;

            case "geoPoint":
                if (value.Kind != WpValueKind.Geo)
                {
                    return ModelErrors.TypeMismatchError("geoPoint", value.Kind.ToString().ToLowerInvariant(), path);
                }

                var (lat, lon, alt) = value.AsGeo();
                if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                {
                    return ModelErrors.OutOfRangeError(-90, 90, path);
                }

                return value;

            case "list":
                if (value.Kind != WpValueKind.List)
                {
                    return ModelErrors.TypeMismatchError("list", value.Kind.ToString().ToLowerInvariant(), path);
                }

                var items = value.AsList();
                if (items.Count > MaxListItems)
                {
                    return ModelErrors.TooLongError(MaxListItems, path);
                }

                var validatedItems = new List<WpValue>(items.Count);
                var itemType = propDef?.ItemType ?? "string";
                for (var i = 0; i < items.Count; i++)
                {
                    var itemRes = ValidateValue(itemType, null, items[i], null, $"{path}/{i}");
                    if (!itemRes.TryGetValue(out var valItem, out var itemErr))
                    {
                        return itemErr;
                    }

                    validatedItems.Add(valItem);
                }

                return WpValue.List(validatedItems);

            case "object":
                if (value.Kind != WpValueKind.Object)
                {
                    return ModelErrors.TypeMismatchError("object", value.Kind.ToString().ToLowerInvariant(), path);
                }

                var objElem = value.AsObject();
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(objElem);
                if (jsonBytes.Length > MaxObjectBytes)
                {
                    return ModelErrors.TooLongError(MaxObjectBytes, path);
                }

                if (propDef?.ObjectSchema is { } schema)
                {
                    var schemaRes = ValidateSchema(schema, objElem, path);
                    if (!schemaRes.TryGetValue(out _, out var schemaErr))
                    {
                        return schemaErr;
                    }
                }

                return value;

            default:
                return value;
        }
    }

    private static Dictionary<string, JsonElement> GetSchemaProperties(ParameterSchema schema)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.RawElement.ValueKind == JsonValueKind.Object
            && schema.RawElement.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                dict[prop.Name] = prop.Value;
            }
        }

        return dict;
    }

    private static HashSet<string> GetSchemaRequired(ParameterSchema schema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (schema.RawElement.ValueKind == JsonValueKind.Object
            && schema.RawElement.TryGetProperty("required", out var req)
            && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    set.Add(item.GetString()!);
                }
            }
        }

        return set;
    }

    private static void WriteWpValue(Utf8JsonWriter writer, WpValue value)
    {
        switch (value.Kind)
        {
            case WpValueKind.Null:
                writer.WriteNullValue();
                break;
            case WpValueKind.Bool:
                writer.WriteBooleanValue(value.AsBool());
                break;
            case WpValueKind.Int:
                writer.WriteNumberValue(value.AsInt());
                break;
            case WpValueKind.Decimal:
                writer.WriteNumberValue(value.AsDecimal());
                break;
            case WpValueKind.Enum:
                writer.WriteStringValue(value.AsEnum());
                break;
            case WpValueKind.String:
                writer.WriteStringValue(value.AsString());
                break;
            case WpValueKind.Timestamp:
                writer.WriteStringValue(value.AsTimestamp().ToString("O"));
                break;
            case WpValueKind.Duration:
                writer.WriteStringValue(System.Xml.XmlConvert.ToString(value.AsDuration()));
                break;
            case WpValueKind.Geo:
                var (lat, lon, alt) = value.AsGeo();
                writer.WriteStartArray();
                writer.WriteNumberValue(lat);
                writer.WriteNumberValue(lon);
                if (alt.HasValue)
                {
                    writer.WriteNumberValue(alt.Value);
                }

                writer.WriteEndArray();
                break;
            case WpValueKind.List:
                writer.WriteStartArray();
                foreach (var item in value.AsList())
                {
                    WriteWpValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            case WpValueKind.Object:
                value.AsObject().WriteTo(writer);
                break;
        }
    }
}
