using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Watchpoint.Kernel.Time;

/// <summary>
/// Reads and writes <see cref="TimeSpan"/> as an ISO 8601 duration: <c>PT10S</c>, <c>PT1H30M</c>, <c>P1D</c>,
/// <c>P1W</c>, <c>PT0.5S</c>, optionally negative (<c>-PT5M</c>). Years and months are rejected (ambiguous length),
/// as is the .NET <c>hh:mm:ss</c> form; both throw a <see cref="JsonException"/> (<c>common.value.invalid_format</c>).
/// Written durations use days, hours, minutes and seconds (<c>P1DT2H</c>; zero is <c>PT0S</c>).
/// </summary>
public sealed partial class Iso8601DurationConverter : JsonConverter<TimeSpan>
{
    /// <inheritdoc />
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("common.value.invalid_format: expected an ISO 8601 duration without years or months.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }

    /// <summary>Formats a duration in ISO 8601.</summary>
    internal static string Format(TimeSpan value)
    {
        if (value == TimeSpan.Zero)
        {
            return "PT0S";
        }

        var sb = new StringBuilder();
        var ticks = value.Ticks;
        if (ticks < 0)
        {
            sb.Append('-');
        }

        // Work on the magnitude as an unsigned value so TimeSpan.MinValue does not overflow.
        var magnitude = ticks < 0 ? (ulong)-(ticks + 1) + 1 : (ulong)ticks;
        var days = magnitude / TimeSpan.TicksPerDay;
        var rest = magnitude % TimeSpan.TicksPerDay;
        var hours = rest / TimeSpan.TicksPerHour;
        rest %= TimeSpan.TicksPerHour;
        var minutes = rest / TimeSpan.TicksPerMinute;
        rest %= TimeSpan.TicksPerMinute;
        var seconds = rest / TimeSpan.TicksPerSecond;
        var fraction = rest % TimeSpan.TicksPerSecond;

        sb.Append('P');
        if (days > 0)
        {
            sb.Append(days.ToString(CultureInfo.InvariantCulture)).Append('D');
        }

        if (hours + minutes + seconds + fraction > 0)
        {
            sb.Append('T');
            if (hours > 0)
            {
                sb.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            }

            if (minutes > 0)
            {
                sb.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            }

            if (seconds + fraction > 0)
            {
                sb.Append(seconds.ToString(CultureInfo.InvariantCulture));
                if (fraction > 0)
                {
                    sb.Append('.').Append(fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0'));
                }

                sb.Append('S');
            }
        }

        return sb.ToString();
    }

    /// <summary>Parses an ISO 8601 duration without years or months.</summary>
    internal static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (text is null || text.Length > 64)
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success || text.EndsWith('P') || text.EndsWith('T'))
        {
            return false;
        }

        try
        {
            decimal ticks = 0;
            ticks += Component(match, "w") * 7 * TimeSpan.TicksPerDay;
            ticks += Component(match, "d") * TimeSpan.TicksPerDay;
            ticks += Component(match, "h") * TimeSpan.TicksPerHour;
            ticks += Component(match, "m") * TimeSpan.TicksPerMinute;
            ticks += Component(match, "s") * TimeSpan.TicksPerSecond;
            if (match.Groups["neg"].Success)
            {
                ticks = -ticks;
            }

            if (ticks < TimeSpan.MinValue.Ticks || ticks > TimeSpan.MaxValue.Ticks)
            {
                return false;
            }

            value = TimeSpan.FromTicks((long)ticks);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal Component(Match match, string name)
    {
        var group = match.Groups[name];
        return group.Success ? decimal.Parse(group.ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : 0m;
    }

    [GeneratedRegex(
        @"^(?<neg>-)?P(?:(?<w>\d{1,9})W)?(?:(?<d>\d{1,9})D)?(?:T(?:(?<h>\d{1,12})H)?(?:(?<m>\d{1,14})M)?(?:(?<s>\d{1,15}(?:\.\d{1,7})?)S)?)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
