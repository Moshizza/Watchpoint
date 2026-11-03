using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;

namespace Watchpoint.Kernel.Paging;

/// <summary>
/// Encodes and decodes keyset paging cursors (spec K §7): <c>base64url(UTF-8 JSON {"v":1,"k":[…],"id":"&lt;uuid&gt;"})</c>.
/// Cursors are opaque to clients; anything not produced by <see cref="Encode"/> for the same version decodes to
/// <c>common.cursor.invalid</c>. Input is limited to <see cref="MaxLength"/> characters and JSON depth
/// <see cref="MaxDepth"/> (spec K §12).
/// </summary>
public static class CursorCodec
{
    /// <summary>The maximum length of an encoded cursor (characters).</summary>
    public const int MaxLength = 2048;

    /// <summary>The maximum JSON depth of a decoded cursor.</summary>
    public const int MaxDepth = 8;

    /// <summary>Encodes a cursor.</summary>
    /// <param name="version">The cursor version of the query (change it when the sort keys change).</param>
    /// <param name="keyValues">
    /// The sort key values of the last item: <see langword="null"/>, <see cref="string"/>, <see cref="bool"/>,
    /// <see cref="int"/>, <see cref="long"/>, <see cref="double"/>, <see cref="decimal"/>, <see cref="DateTimeOffset"/>
    /// (written as UTC milliseconds), <see cref="Guid"/>, UUID-based or string-based ids, or enums (written as numbers).
    /// </param>
    /// <param name="lastId">The id of the last item.</param>
    /// <returns>The cursor.</returns>
    /// <exception cref="ArgumentException">A key value has an unsupported type.</exception>
    public static string Encode(int version, IReadOnlyList<object?> keyValues, Guid lastId)
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", version);
            writer.WriteStartArray("k");
            foreach (var value in keyValues)
            {
                WriteKey(writer, value);
            }

            writer.WriteEndArray();
            writer.WriteString("id", lastId.ToString("D"));
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    /// <summary>Decodes a cursor.</summary>
    /// <param name="cursor">The cursor received from a client.</param>
    /// <param name="expectedVersion">The cursor version of the query.</param>
    /// <returns>The decoded cursor, or <c>common.cursor.invalid</c>.</returns>
    public static Result<DecodedCursor> Decode(string cursor, int expectedVersion) => Decode(cursor, expectedVersion, null);

    /// <summary>Decodes a cursor and logs an invalid one (event 1001, Warning).</summary>
    /// <param name="cursor">The cursor received from a client.</param>
    /// <param name="expectedVersion">The cursor version of the query.</param>
    /// <param name="logger">The logger, or <see langword="null"/>.</param>
    /// <returns>The decoded cursor, or <c>common.cursor.invalid</c>.</returns>
    public static Result<DecodedCursor> Decode(string cursor, int expectedVersion, ILogger? logger)
    {
        var reason = TryDecode(cursor, expectedVersion, out var decoded);
        if (reason is null)
        {
            return decoded!;
        }

        if (logger is not null)
        {
            KernelLog.CursorInvalid(logger, reason);
        }

        return CommonErrors.CursorInvalid();
    }

    private static string? TryDecode(string? cursor, int expectedVersion, out DecodedCursor? decoded)
    {
        decoded = null;
        if (string.IsNullOrEmpty(cursor))
        {
            return "empty";
        }

        if (cursor.Length > MaxLength)
        {
            return "too long";
        }

        var bytes = new byte[Base64Url.GetMaxDecodedLength(cursor.Length)];
        if (Base64Url.DecodeFromChars(cursor, bytes, out var consumed, out var written) != OperationStatus.Done
            || consumed != cursor.Length)
        {
            return "not base64url";
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, written), new JsonDocumentOptions { MaxDepth = MaxDepth });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "not an object";
            }

            int? version = null;
            List<JsonElement>? keys = null;
            Guid? id = null;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "v" when version is null && property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var v):
                        version = v;
                        break;
                    case "k" when keys is null && property.Value.ValueKind == JsonValueKind.Array:
                        keys = [.. property.Value.EnumerateArray().Select(e => e.Clone())];
                        break;
                    case "id" when id is null && property.Value.ValueKind == JsonValueKind.String
                        && EntityIdHelper.TryParse(property.Value.GetString(), out var g):
                        id = g;
                        break;
                    default:
                        return "unexpected member " + property.Name;
                }
            }

            if (version is null || keys is null || id is null)
            {
                return "missing member";
            }

            if (version != expectedVersion)
            {
                return "version mismatch";
            }

            decoded = new DecodedCursor(keys, id.Value);
            return null;
        }
        catch (JsonException)
        {
            return "invalid JSON";
        }
    }

    private static void WriteKey(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case DateTimeOffset t: writer.WriteStringValue(UtcDateTimeOffsetConverter.Format(t)); break;
            case Guid g: writer.WriteStringValue(g.ToString("D")); break;
            case Enum e: writer.WriteNumberValue(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture)); break;
            case IStringId or ISpanFormattable when IsId(value): writer.WriteStringValue(value.ToString()); break;
            default:
                throw new ArgumentException($"Cursor key values of type {value.GetType().FullName} are not supported.", nameof(value));
        }
    }

    private static bool IsId(object value) =>
        value is IStringId
        || Array.Exists(value.GetType().GetInterfaces(), i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityId<>));
}
