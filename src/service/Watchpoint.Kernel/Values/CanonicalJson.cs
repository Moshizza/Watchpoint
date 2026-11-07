using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Watchpoint.Kernel.Values;

/// <summary>
/// Canonical JSON text of an element, used to compare <see cref="WpValueKind.Object"/> values: no whitespace,
/// object members sorted by name (ordinal), numbers kept as written.
/// </summary>
internal static class CanonicalJson
{
    public static string Write(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, element);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Returns the nesting depth: 1 for an object or array of scalars, 0 for a scalar.</summary>
    public static int Depth(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => 1 + element.EnumerateObject().Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
            JsonValueKind.Array => 1 + element.EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max(),
            _ => 0,
        };

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
