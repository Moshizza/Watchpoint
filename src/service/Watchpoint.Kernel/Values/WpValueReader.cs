using System.Globalization;
using System.Text;
using System.Text.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;

namespace Watchpoint.Kernel.Values;

/// <summary>Reads <see cref="WpValue"/> from JSON (full and compact forms) with the size and depth limits of spec K §10.</summary>
internal static class WpValueReader
{
    public static string KindName(WpValueKind kind) =>
        kind switch
        {
            WpValueKind.Null => "null",
            WpValueKind.Bool => "bool",
            WpValueKind.Int => "int",
            WpValueKind.Decimal => "dec",
            WpValueKind.Enum => "enum",
            WpValueKind.String => "str",
            WpValueKind.Timestamp => "ts",
            WpValueKind.Duration => "dur",
            WpValueKind.Geo => "geo",
            WpValueKind.List => "list",
            WpValueKind.Object => "obj",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    public static Result<WpValue> ReadFull(JsonElement json, int depth)
    {
        if (depth > WpValue.MaxDepth)
        {
            return TooDeep();
        }

        if (json.ValueKind != JsonValueKind.Object)
        {
            return CommonErrors.InvalidFormat("{\"k\":<kind>,\"v\":<value>}");
        }

        string? kindName = null;
        JsonElement? value = null;
        foreach (var property in json.EnumerateObject())
        {
            switch (property.Name)
            {
                case "k" when kindName is null && property.Value.ValueKind == JsonValueKind.String:
                    kindName = property.Value.GetString();
                    break;
                case "v" when value is null:
                    value = property.Value;
                    break;
                default:
                    return CommonErrors.InvalidFormat("{\"k\":<kind>,\"v\":<value>}");
            }
        }

        var v = value ?? default;
        var hasValue = value is { ValueKind: not JsonValueKind.Null };
        switch (kindName)
        {
            case "null":
                return hasValue ? CommonErrors.InvalidFormat("null without value") : WpValue.Null;
            case "dec" when hasValue && v.ValueKind == JsonValueKind.String:
                return ReadDecimalString(v.GetString()!);
            case "list" when hasValue && v.ValueKind == JsonValueKind.Array:
                return ReadList(v, depth);
            case "bool" or "int" or "enum" or "str" or "ts" or "dur" or "geo" or "obj" when hasValue:
                return ReadScalar(v, Kind(kindName), depth);
            default:
                return CommonErrors.InvalidFormat("a known kind with a value of that kind");
        }
    }

    public static Result<WpValue> ReadCompact(JsonElement json, WpValueKind kind)
    {
        if (json.ValueKind == JsonValueKind.Null)
        {
            return WpValue.Null;
        }

        return kind switch
        {
            WpValueKind.Null => CommonErrors.InvalidFormat("null"),
            WpValueKind.Decimal when json.ValueKind == JsonValueKind.Number =>
                json.TryGetDecimal(out var d) ? WpValue.Decimal(d) : CommonErrors.InvalidFormat("dec"),
            WpValueKind.Decimal when json.ValueKind == JsonValueKind.String => ReadDecimalString(json.GetString()!),
            WpValueKind.List => CommonErrors.InvalidFormat("list items in full form (no compact form for lists)"),
            _ => ReadScalar(json, kind, 1),
        };
    }

    private static Result<WpValue> ReadScalar(JsonElement v, WpValueKind kind, int depth)
    {
        switch (kind)
        {
            case WpValueKind.Bool when v.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return WpValue.Bool(v.GetBoolean());
            case WpValueKind.Int when v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l):
                return WpValue.Int(l);
            case WpValueKind.Enum when v.ValueKind == JsonValueKind.String:
                var code = v.GetString()!;
                return IsEnumCode(code) ? WpValue.Enum(code) : CommonErrors.InvalidFormat(WpValue.EnumPattern);
            case WpValueKind.String when v.ValueKind == JsonValueKind.String:
                var text = v.GetString()!;
                return Encoding.UTF8.GetByteCount(text) > WpValue.MaxStringBytes ? TooLong(WpValue.MaxStringBytes) : WpValue.String(text);
            case WpValueKind.Timestamp when v.ValueKind == JsonValueKind.String:
                return UtcDateTimeOffsetConverter.TryParse(v.GetString(), out var ts)
                    ? WpValue.Timestamp(ts)
                    : CommonErrors.InvalidFormat("ISO 8601 date and time with offset");
            case WpValueKind.Duration when v.ValueKind == JsonValueKind.String:
                return Iso8601DurationConverter.TryParse(v.GetString(), out var dur)
                    ? WpValue.Duration(dur)
                    : CommonErrors.InvalidFormat("ISO 8601 duration");
            case WpValueKind.Geo when v.ValueKind == JsonValueKind.Array:
                return ReadGeo(v);
            case WpValueKind.Object when v.ValueKind == JsonValueKind.Object:
                if (depth - 1 + CanonicalJson.Depth(v) > WpValue.MaxDepth)
                {
                    return TooDeep();
                }

                var obj = WpValue.Object(v);
                return Encoding.UTF8.GetByteCount(obj.CanonicalObject) > WpValue.MaxObjectBytes ? TooLong(WpValue.MaxObjectBytes) : obj;
            default:
                return CommonErrors.InvalidFormat(KindName(kind));
        }
    }

    private static Result<WpValue> ReadList(JsonElement v, int depth)
    {
        if (v.GetArrayLength() > WpValue.MaxListItems)
        {
            return TooLong(WpValue.MaxListItems);
        }

        var items = new List<WpValue>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            var read = ReadFull(item, depth + 1);
            if (!read.IsSuccess)
            {
                return read;
            }

            items.Add(read.Value);
        }

        return WpValue.List(items);
    }

    private static Result<WpValue> ReadGeo(JsonElement v)
    {
        var length = v.GetArrayLength();
        if (length is < 2 or > 3)
        {
            return CommonErrors.InvalidFormat("[lat, lon] or [lat, lon, alt]");
        }

        Span<double> c = stackalloc double[3];
        for (var i = 0; i < length; i++)
        {
            var item = v[i];
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out c[i]))
            {
                return CommonErrors.InvalidFormat("[lat, lon] or [lat, lon, alt]");
            }
        }

        if (c[0] is < -90 or > 90 || c[1] is < -180 or > 180)
        {
            return CommonErrors.InvalidFormat("latitude -90..90, longitude -180..180");
        }

        return WpValue.Geo(c[0], c[1], length == 3 ? c[2] : null);
    }

    private static Result<WpValue> ReadDecimalString(string text) =>
        text.Length <= 64 && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? WpValue.Decimal(d)
            : CommonErrors.InvalidFormat("decimal number as a string, e.g. \"21.5\"");

    private static bool IsEnumCode(string code)
    {
        if (code.Length == 0 || code[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var ch in code)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static WpValueKind Kind(string name) =>
        name switch
        {
            "bool" => WpValueKind.Bool,
            "int" => WpValueKind.Int,
            "enum" => WpValueKind.Enum,
            "str" => WpValueKind.String,
            "ts" => WpValueKind.Timestamp,
            "dur" => WpValueKind.Duration,
            "geo" => WpValueKind.Geo,
            _ => WpValueKind.Object,
        };

    private static Error TooLong(int max) =>
        Error.Create(ErrorKind.Validation, CommonErrorCodes.ValueTooLong.Value, ("max", max));

    private static Error TooDeep() =>
        Error.Create(ErrorKind.Validation, CommonErrorCodes.ValueTooLong.Value, ("max", WpValue.MaxDepth));
}
