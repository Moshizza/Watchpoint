using System.Globalization;
using System.Text;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Scheduling;

/// <summary>
/// Parser and serializer for iCalendar (RFC 5545) holiday files (spec SCH §7).
/// Supports VEVENT with all-day DTSTART;VALUE=DATE and optional RRULE:FREQ=YEARLY.
/// Unsupported content is ignored with warnings.
/// </summary>
public static class IcsHolidayParser
{
    /// <summary>Maximum allowed ICS file size (1 MB, per spec SCH §7).</summary>
    public const int MaxSizeBytes = 1024 * 1024;

    /// <summary>Parses an ICS text stream into a list of holidays and warnings.</summary>
    /// <param name="icsContent">The raw iCalendar string.</param>
    /// <returns>Result containing parsed holidays and warnings, or validation error.</returns>
    public static Result<IcsParseResult> Parse(string icsContent)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
        {
            return SchedulingErrors.IcsInvalid("ICS content is empty");
        }

        if (Encoding.UTF8.GetByteCount(icsContent) > MaxSizeBytes)
        {
            return SchedulingErrors.IcsInvalid("ICS content exceeds 1 MB limit");
        }

        var unfoldedLines = UnfoldLines(icsContent);
        var holidays = new List<Holiday>();
        var warnings = new List<string>();

        bool inVCalendar = false;
        bool inVEvent = false;

        DateOnly? eventDate = null;
        string? summary = null;
        bool recursYearly = false;
        bool isAllDay = false;

        foreach (var rawLine in unfoldedLines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                inVCalendar = true;
                continue;
            }

            if (line.Equals("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                inVCalendar = false;
                continue;
            }

            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inVEvent = true;
                eventDate = null;
                summary = null;
                recursYearly = false;
                isAllDay = false;
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (inVEvent)
                {
                    if (eventDate.HasValue && isAllDay)
                    {
                        var name = new LocalizedName(summary ?? "Holiday", new Dictionary<string, string>());
                        holidays.Add(new Holiday(eventDate.Value, name, recursYearly));
                    }
                    else if (eventDate.HasValue && !isAllDay)
                    {
                        warnings.Add($"VEVENT with summary '{summary}' ignored: not an all-day event (missing VALUE=DATE)");
                    }
                    else
                    {
                        warnings.Add($"VEVENT ignored: missing valid DTSTART");
                    }
                }

                inVEvent = false;
                continue;
            }

            if (inVEvent)
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var propHeader = line[..colonIndex];
                var propValue = line[(colonIndex + 1)..];

                var paramParts = propHeader.Split(';');
                var propName = paramParts[0].ToUpperInvariant();

                switch (propName)
                {
                    case "DTSTART":
                        bool hasValueDate = paramParts.Any(p => p.Equals("VALUE=DATE", StringComparison.OrdinalIgnoreCase));
                        // Format YYYYMMDD
                        if (hasValueDate && DateTime.TryParseExact(propValue, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        {
                            eventDate = DateOnly.FromDateTime(parsedDate);
                            isAllDay = true;
                        }
                        else if (!hasValueDate)
                        {
                            eventDate = DateOnly.MinValue;
                            isAllDay = false;
                            warnings.Add($"DTSTART '{propValue}' is not an all-day event (missing VALUE=DATE)");
                        }
                        else
                        {
                            warnings.Add($"DTSTART '{propValue}' could not be parsed as all-day date");
                        }

                        break;

                    case "SUMMARY":
                        summary = UnescapeIcsText(propValue);
                        break;

                    case "RRULE":
                        var rules = propValue.Split(';');
                        foreach (var rule in rules)
                        {
                            var kv = rule.Split('=');
                            if (kv.Length == 2 && kv[0].Equals("FREQ", StringComparison.OrdinalIgnoreCase))
                            {
                                if (kv[1].Equals("YEARLY", StringComparison.OrdinalIgnoreCase))
                                {
                                    recursYearly = true;
                                }
                                else
                                {
                                    warnings.Add($"Unsupported RRULE FREQ: {kv[1]}");
                                }
                            }
                            else if (!kv[0].Equals("FREQ", StringComparison.OrdinalIgnoreCase))
                            {
                                warnings.Add($"Unsupported RRULE component: {rule}");
                            }
                        }

                        break;

                    case "UID":
                    case "DTSTAMP":
                    case "SEQUENCE":
                    case "STATUS":
                    case "TRANSP":
                    case "DESCRIPTION":
                    case "DTEND":
                        // Standard ignored fields
                        break;

                    default:
                        warnings.Add($"Unsupported VEVENT property '{propName}' ignored");
                        break;
                }
            }
            else if (inVCalendar)
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var propName = line[..colonIndex].Split(';')[0].ToUpperInvariant();
                    if (propName is not "PRODID" and not "VERSION" and not "CALSCALE" and not "METHOD")
                    {
                        warnings.Add($"Unsupported VCALENDAR property '{propName}' ignored");
                    }
                }
            }
        }

        if (holidays.Count == 0 && warnings.Count == 0)
        {
            return SchedulingErrors.IcsInvalid("No calendar events found in ICS data");
        }

        return new IcsParseResult(holidays, warnings);
    }

    /// <summary>Serializes a holiday set to iCalendar format.</summary>
    /// <param name="set">The holiday set.</param>
    /// <returns>The RFC 5545 iCalendar content string.</returns>
    public static string Export(HolidaySet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\n");
        sb.Append("VERSION:2.0\r\n");
        sb.Append("PRODID:-//Watchpoint//Scheduling//EN\r\n");
        sb.Append("CALSCALE:GREGORIAN\r\n");

        foreach (var holiday in set.Holidays)
        {
            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"UID:hol-{holiday.Date:yyyyMMdd}-{Math.Abs(holiday.Name.Default.GetHashCode()):X8}@watchpoint\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"DTSTART;VALUE=DATE:{holiday.Date:yyyyMMdd}\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"SUMMARY:{EscapeIcsText(holiday.Name.Default)}\r\n");
            if (holiday.RecursYearly)
            {
                sb.Append("RRULE:FREQ=YEARLY\r\n");
            }

            sb.Append("TRANSP:TRANSPARENT\r\n");
            sb.Append("END:VEVENT\r\n");
        }

        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static List<string> UnfoldLines(string text)
    {
        var rawLines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var result = new List<string>();

        foreach (var rawLine in rawLines)
        {
            if (rawLine.StartsWith(' ') || rawLine.StartsWith('\t'))
            {
                if (result.Count > 0)
                {
                    result[^1] += rawLine[1..];
                }
            }
            else
            {
                result.Add(rawLine);
            }
        }

        return result;
    }

    private static string EscapeIcsText(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace(";", @"\;", StringComparison.Ordinal)
            .Replace(",", @"\,", StringComparison.Ordinal)
            .Replace("\n", @"\n", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal);

    private static string UnescapeIcsText(string text) =>
        text.Replace(@"\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace(@"\,", ",", StringComparison.Ordinal)
            .Replace(@"\;", ";", StringComparison.Ordinal)
            .Replace(@"\\", @"\", StringComparison.Ordinal);
}

/// <summary>Result of parsing an ICS holiday calendar.</summary>
/// <param name="Holidays">Parsed holidays.</param>
/// <param name="Warnings">Warnings for ignored or unsupported content.</param>
public sealed record IcsParseResult(
    IReadOnlyList<Holiday> Holidays,
    IReadOnlyList<string> Warnings);
