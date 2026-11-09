using System.Text.RegularExpressions;
using NodaTime;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Scheduling;

/// <summary>
/// A named weekly schedule with exceptions and holiday sets, in a time zone (spec SCH §3).
/// </summary>
/// <param name="Id">The calendar identifier.</param>
/// <param name="Key">The calendar key (<c>^[a-z][a-z0-9-]{1,63}$</c>, unique).</param>
/// <param name="Name">Localized display name.</param>
/// <param name="TimeZone">IANA time zone id; null = site time zone.</param>
/// <param name="Weekly">Weekly schedule indexed by day of week.</param>
/// <param name="Exceptions">Date exceptions.</param>
/// <param name="HolidaySets">Holiday sets in evaluation order.</param>
/// <param name="ValidFrom">Optional validity start date.</param>
/// <param name="ValidUntil">Optional validity end date.</param>
/// <param name="Version">Version / concurrency token.</param>
public sealed partial record Calendar(
    CalendarId Id,
    string Key,
    LocalizedName Name,
    string? TimeZone,
    IReadOnlyDictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>> Weekly,
    IReadOnlyList<CalendarException> Exceptions,
    IReadOnlyList<HolidaySetUse> HolidaySets,
    DateOnly? ValidFrom,
    DateOnly? ValidUntil,
    long Version)
{
    /// <summary>The maximum length of a calendar key.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The key pattern regex.</summary>
    public const string KeyPattern = @"^[a-z][a-z0-9-]{1,63}$";

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    /// <summary>Validates whether a calendar key conforms to the specification.</summary>
    /// <param name="key">The key to validate.</param>
    /// <returns>True if valid; otherwise false.</returns>
    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && KeyRegex().IsMatch(key);
}

/// <summary>
/// A time range within a day (Start &lt; End, end exclusive; "24:00" represented by 24h).
/// </summary>
public readonly record struct TimeRange
{
    /// <summary>Creates a time range.</summary>
    /// <param name="start">Start time (inclusive).</param>
    /// <param name="end">End time (exclusive).</param>
    /// <param name="isEndMidnightNextDay">True if the end time represents 24:00 (the end of the day).</param>
    public TimeRange(LocalTime start, LocalTime end, bool isEndMidnightNextDay = false)
    {
        Start = start;
        End = end;
        IsEndMidnightNextDay = isEndMidnightNextDay;
    }

    /// <summary>Start time (inclusive).</summary>
    public LocalTime Start { get; init; }

    /// <summary>End time (exclusive).</summary>
    public LocalTime End { get; init; }

    /// <summary>True if the range ends at 24:00 (midnight at the close of day).</summary>
    public bool IsEndMidnightNextDay { get; init; }

    /// <summary>Creates a range ending at 24:00.</summary>
    public static TimeRange UntilMidnight(LocalTime start) => new(start, LocalTime.Midnight, isEndMidnightNextDay: true);

    /// <summary>Checks whether this range contains the local time.</summary>
    public bool Contains(LocalTime time)
    {
        if (IsEndMidnightNextDay)
        {
            return time >= Start;
        }

        return time >= Start && time < End;
    }

    /// <summary>Validates the time range.</summary>
    public Result<bool> Validate()
    {
        if (IsEndMidnightNextDay)
        {
            return true;
        }

        if (Start >= End)
        {
            return SchedulingErrors.RangeInvalid(Start.ToString(), End.ToString());
        }

        return true;
    }
}

/// <summary>Exception mode for a calendar exception or holiday set use.</summary>
public enum ExceptionMode
{
    /// <summary>Inactive all day.</summary>
    Closed,

    /// <summary>Active all day.</summary>
    Open,

    /// <summary>Active during specified custom ranges.</summary>
    Custom,
}

/// <summary>Calendar exception for a specific date.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Named CalendarException by spec SCH §3.")]
public sealed record CalendarException(
    DateOnly Date,
    ExceptionMode Mode,
    IReadOnlyList<TimeRange>? Ranges,
    string? Note);

/// <summary>Use of a holiday set within a calendar.</summary>
public sealed record HolidaySetUse(
    HolidaySetId Set,
    ExceptionMode Mode,
    IReadOnlyList<TimeRange>? Ranges);

/// <summary>A set of holidays for a country or site.</summary>
public sealed record HolidaySet(
    HolidaySetId Id,
    LocalizedName Name,
    string? CountryCode,
    IReadOnlyList<Holiday> Holidays,
    long Version);

/// <summary>A single holiday entry.</summary>
public sealed record Holiday(
    DateOnly Date,
    LocalizedName Name,
    bool RecursYearly);

/// <summary>Active period interval in UTC.</summary>
public sealed record ActivePeriod(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Event arguments for calendar changes.</summary>
public sealed class CalendarChangedEventArgs(string calendarKey, CalendarId id) : EventArgs
{
    /// <summary>Gets the calendar key.</summary>
    public string CalendarKey { get; } = calendarKey;

    /// <summary>Gets the calendar identifier.</summary>
    public CalendarId Id { get; } = id;
}

/// <summary>Edge kind for calendar edge triggers.</summary>
public enum EdgeKind
{
    /// <summary>Trigger at the start of active periods.</summary>
    Start,

    /// <summary>Trigger at the end of active periods.</summary>
    End,

    /// <summary>Trigger at both starts and ends of active periods.</summary>
    Both,
}

/// <summary>Solar event kind.</summary>
public enum SunEvent
{
    /// <summary>Sunrise event.</summary>
    Sunrise,

    /// <summary>Sunset event.</summary>
    Sunset,
}

/// <summary>Policy for handling triggers scheduled during downtime or missed firing times.</summary>
public enum MissedPolicy
{
    /// <summary>Ignore missed occurrences.</summary>
    Skip,

    /// <summary>Fire once with CatchUp = true at start-up if occurrences were missed.</summary>
    FireOnce,
}

/// <summary>Specification of a time trigger.</summary>
public abstract record TriggerSpec
{
    /// <summary>One-shot trigger at a specific UTC instant.</summary>
    public sealed record At(DateTimeOffset Utc) : TriggerSpec;

    /// <summary>Cron trigger (5 fields) in a given time zone.</summary>
    public sealed record Cron(string Expression, string? TimeZone) : TriggerSpec;

    /// <summary>Trigger on calendar active/inactive edges.</summary>
    public sealed record CalendarEdge(string CalendarKey, EdgeKind Edge) : TriggerSpec;

    /// <summary>Recurring interval trigger with optional anchor instant.</summary>
    public sealed record Every(TimeSpan Interval, DateTimeOffset? Anchor) : TriggerSpec;

    /// <summary>Solar sunrise/sunset trigger with offset for a location or site.</summary>
    public sealed record Sun(SunEvent Event, TimeSpan Offset, LocationId? Location) : TriggerSpec;
}

/// <summary>Payload passed when a trigger fires.</summary>
public sealed record TriggerFired(
    string Owner,
    string Key,
    DateTimeOffset ScheduledAt,
    DateTimeOffset FiredAt,
    bool CatchUp);
