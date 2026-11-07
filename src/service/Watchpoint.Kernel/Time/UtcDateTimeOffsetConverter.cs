using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Watchpoint.Kernel.Time;

/// <summary>
/// Writes <see cref="DateTimeOffset"/> as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (UTC, milliseconds). Reads any ISO 8601
/// date and time with an offset (<c>Z</c> or <c>±hh:mm</c>) and converts it to UTC, truncated to milliseconds;
/// values without offset are rejected with a <see cref="JsonException"/> (<c>common.value.invalid_format</c>).
/// </summary>
public sealed partial class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("common.value.invalid_format: expected an ISO 8601 date and time with offset.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }

    /// <summary>Formats an instant as <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>.</summary>
    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses an ISO 8601 date and time with offset; returns UTC truncated to milliseconds.</summary>
    internal static bool TryParse(string? text, out DateTimeOffset value)
    {
        if (text is not null
            && Pattern().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            var ticks = parsed.UtcTicks;
            value = new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
            return true;
        }

        value = default;
        return false;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
