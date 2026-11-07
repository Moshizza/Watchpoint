using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Kernel.Time;

namespace Watchpoint.Kernel.Values;

/// <summary>
/// Reads and writes the full JSON form of <see cref="WpValue"/>: <c>{"k":"bool","v":true}</c>, <c>{"k":"int","v":5}</c>,
/// <c>{"k":"dec","v":"21.5"}</c>, <c>{"k":"enum","v":"open"}</c>, <c>{"k":"str","v":"text"}</c>,
/// <c>{"k":"ts","v":"2026-11-03T09:00:00.000Z"}</c>, <c>{"k":"dur","v":"PT10S"}</c>, <c>{"k":"geo","v":[48.85,2.35]}</c>,
/// <c>{"k":"list","v":[…]}</c>, <c>{"k":"obj","v":{…}}</c>, <c>{"k":"null"}</c>. Reading enforces the limits of
/// spec K §10 (strings and objects 16 KB, lists 1,000 items, depth 8) and throws <see cref="JsonException"/>.
/// </summary>
public sealed class WpValueJsonConverter : JsonConverter<WpValue>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override WpValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var result = WpValueReader.ReadFull(doc.RootElement, 1);
        return result.TryGetValue(out var value, out var error)
            ? value
            : throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"{error.Code}: invalid WpValue ({string.Join(", ", error.Args.Select(a => $"{a.Key}={a.Value}"))})."));
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WpValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteFull(writer, value);
    }

    private static void WriteFull(Utf8JsonWriter writer, WpValue value)
    {
        writer.WriteStartObject();
        writer.WriteString("k", WpValueReader.KindName(value.Kind));
        switch (value.Kind)
        {
            case WpValueKind.Null:
                break;
            case WpValueKind.Bool:
                writer.WriteBoolean("v", value.AsBool());
                break;
            case WpValueKind.Int:
                writer.WriteNumber("v", value.AsInt());
                break;
            case WpValueKind.Decimal:
                writer.WriteString("v", value.AsDecimal().ToString(CultureInfo.InvariantCulture));
                break;
            case WpValueKind.Enum:
                writer.WriteString("v", value.AsEnum());
                break;
            case WpValueKind.String:
                writer.WriteString("v", value.AsString());
                break;
            case WpValueKind.Timestamp:
                writer.WriteString("v", UtcDateTimeOffsetConverter.Format(value.AsTimestamp()));
                break;
            case WpValueKind.Duration:
                writer.WriteString("v", Iso8601DurationConverter.Format(value.AsDuration()));
                break;
            case WpValueKind.Geo:
                var geo = value.AsGeo();
                writer.WriteStartArray("v");
                writer.WriteNumberValue(geo.Latitude);
                writer.WriteNumberValue(geo.Longitude);
                if (geo.Altitude is { } alt)
                {
                    writer.WriteNumberValue(alt);
                }

                writer.WriteEndArray();
                break;
            case WpValueKind.List:
                writer.WriteStartArray("v");
                foreach (var item in value.AsList())
                {
                    WriteFull(writer, item);
                }

                writer.WriteEndArray();
                break;
            case WpValueKind.Object:
                writer.WritePropertyName("v");
                value.AsObject().WriteTo(writer);
                break;
        }

        writer.WriteEndObject();
    }
}
