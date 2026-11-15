using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>
/// The JSON form of <see cref="LogEnvelope"/> (spec PIP §5.2). Writing is deterministic: every member is written, in
/// the order <c>id, kind, ts, src, p, dev, srv, origin, corr, body</c>, without whitespace, ids as lowercase UUIDs and
/// times as UTC with milliseconds. Reading ignores unknown members and accepts missing optional ones.
/// </summary>
internal static class LogEnvelopeJson
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly JsonWriterOptions _writerOptions = new() { Indented = false, SkipValidation = false };

    public static byte[] Serialize(LogEnvelope envelope)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id"u8, envelope.Id);
            writer.WriteString("kind"u8, envelope.Kind);
            writer.WriteString("ts"u8, Format(envelope.Ts));
            WriteTime(writer, "src"u8, envelope.Src);
            writer.WriteNumber("p"u8, envelope.P);
            WriteId(writer, "dev"u8, envelope.Dev?.Value);
            WriteId(writer, "srv"u8, envelope.Srv?.Value);
            writer.WriteStartObject("origin"u8);
            writer.WriteString("type"u8, OriginName(envelope.Origin.Type));
            WriteId(writer, "id"u8, envelope.Origin.Id);
            writer.WriteEndObject();
            writer.WriteStartObject("corr"u8);
            WriteId(writer, "command"u8, envelope.Corr.Command?.Value);
            WriteId(writer, "alarm"u8, envelope.Corr.Alarm?.Value);
            WriteId(writer, "automationRun"u8, envelope.Corr.AutomationRun?.Value);
            writer.WriteEndObject();
            writer.WritePropertyName("body"u8);
            envelope.Body.WriteTo(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads an envelope.</summary>
    /// <exception cref="FormatException">The JSON is not an envelope: malformed, or <c>id</c>, <c>kind</c> or <c>ts</c> missing.</exception>
    public static LogEnvelope Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FormatException("The envelope is not valid JSON.", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The envelope is not a JSON object.");
        }

        var id = ReadId(root, "id") ?? throw new FormatException("The envelope has no id.");
        var kind = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
            ? kindElement.GetString()!
            : throw new FormatException("The envelope has no kind.");
        if (!LogEnvelope.IsValidKind(kind))
        {
            throw new FormatException("The envelope kind is malformed.");
        }

        var ts = ReadTime(root, "ts") ?? throw new FormatException("The envelope has no ts.");
        var partition = root.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var number)
            ? number
            : 0;
        var origin = RecordOrigin.System;
        if (root.TryGetProperty("origin", out var originElement) && originElement.ValueKind == JsonValueKind.Object)
        {
            var typeName = originElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            origin = new RecordOrigin(ParseOrigin(typeName), ReadId(originElement, "id"));
        }

        var corr = RecordCorrelation.None;
        if (root.TryGetProperty("corr", out var corrElement) && corrElement.ValueKind == JsonValueKind.Object)
        {
            corr = new RecordCorrelation(
                ReadId(corrElement, "command") is { } command ? CommandId.From(command) : null,
                ReadId(corrElement, "alarm") is { } alarm ? AlarmId.From(alarm) : null,
                ReadId(corrElement, "automationRun") is { } run ? AutomationRunId.From(run) : null);
        }

        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The envelope has no body object.");
        }

        return new LogEnvelope(
            id,
            kind,
            ts,
            ReadTime(root, "src"),
            partition,
            ReadId(root, "dev") is { } dev ? DeviceId.From(dev) : null,
            ReadId(root, "srv") is { } srv ? ServerId.From(srv) : null,
            origin,
            corr,
            body);
    }

    internal static string OriginName(RecordOriginType type) => type switch
    {
        RecordOriginType.Connection => "connection",
        RecordOriginType.User => "user",
        RecordOriginType.System => "system",
        RecordOriginType.Automation => "automation",
        RecordOriginType.Script => "script",
        RecordOriginType.Federation => "federation",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown origin type."),
    };

    private static RecordOriginType ParseOrigin(string? name) => name switch
    {
        "connection" => RecordOriginType.Connection,
        "user" => RecordOriginType.User,
        "system" => RecordOriginType.System,
        "automation" => RecordOriginType.Automation,
        "script" => RecordOriginType.Script,
        "federation" => RecordOriginType.Federation,
        _ => throw new FormatException("The envelope origin type is unknown."),
    };

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static void WriteTime(Utf8JsonWriter writer, ReadOnlySpan<byte> name, DateTimeOffset? value)
    {
        if (value is { } time)
        {
            writer.WriteString(name, Format(time));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteId(Utf8JsonWriter writer, ReadOnlySpan<byte> name, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(name, id);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static Guid? ReadId(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String && Guid.TryParseExact(element.GetString(), "D", out var id) && id != Guid.Empty
            ? id
            : throw new FormatException($"The envelope member '{name}' is not a UUID.");
    }

    private static DateTimeOffset? ReadTime(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : throw new FormatException($"The envelope member '{name}' is not a date and time.");
    }
}
