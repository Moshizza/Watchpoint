using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Scheduling;

/// <summary>Error definitions for the scheduling module (spec SCH §8).</summary>
public static class SchedulingErrors
{
    /// <summary><c>scheduling.calendar.not_found</c></summary>
    public const string CalendarNotFoundCode = "scheduling.calendar.not_found";

    /// <summary><c>scheduling.calendar.key_taken</c></summary>
    public const string CalendarKeyTakenCode = "scheduling.calendar.key_taken";

    /// <summary><c>scheduling.range.overlap</c></summary>
    public const string RangeOverlapCode = "scheduling.range.overlap";

    /// <summary><c>scheduling.range.invalid</c></summary>
    public const string RangeInvalidCode = "scheduling.range.invalid";

    /// <summary><c>scheduling.calendar.in_use</c></summary>
    public const string CalendarInUseCode = "scheduling.calendar.in_use";

    /// <summary><c>scheduling.ics.invalid</c></summary>
    public const string IcsInvalidCode = "scheduling.ics.invalid";

    /// <summary><c>scheduling.cron.invalid</c></summary>
    public const string CronInvalidCode = "scheduling.cron.invalid";

    /// <summary><c>scheduling.timezone.invalid</c></summary>
    public const string TimezoneInvalidCode = "scheduling.timezone.invalid";

    /// <summary>Calendar not found by key or id.</summary>
    public static Error CalendarNotFound(string keyOrId) =>
        Error.Create(ErrorKind.NotFound, CalendarNotFoundCode, ("calendar", keyOrId));

    /// <summary>Calendar not found by id.</summary>
    public static Error CalendarNotFound(CalendarId id) =>
        Error.Create(ErrorKind.NotFound, CalendarNotFoundCode, ("id", id.ToString()));

    /// <summary>Calendar key is already taken.</summary>
    public static Error CalendarKeyTaken(string key) =>
        Error.Create(ErrorKind.Conflict, CalendarKeyTakenCode, ("key", key));

    /// <summary>Ranges overlap within a day.</summary>
    public static Error RangeOverlap(string first, string second) =>
        Error.Create(ErrorKind.Validation, RangeOverlapCode, ("first", first), ("second", second));

    /// <summary>Range start &gt;= end.</summary>
    public static Error RangeInvalid(string start, string end) =>
        Error.Create(ErrorKind.Validation, RangeInvalidCode, ("start", start), ("end", end));

    /// <summary>Calendar cannot be deleted because it is in use.</summary>
    public static Error CalendarInUse(CalendarId id, IReadOnlyList<string> references) =>
        Error.Create(ErrorKind.PreconditionFailed, CalendarInUseCode, ("id", id.ToString()), ("references", string.Join("; ", references)));

    /// <summary>ICS data is invalid or empty.</summary>
    public static Error IcsInvalid(string reason) =>
        Error.Create(ErrorKind.Validation, IcsInvalidCode, ("reason", reason));

    /// <summary>Cron expression is invalid (must be 5 fields).</summary>
    public static Error CronInvalid(string expression, string reason) =>
        Error.Create(ErrorKind.Validation, CronInvalidCode, ("expression", expression), ("reason", reason));

    /// <summary>Time zone is not recognized.</summary>
    public static Error TimezoneInvalid(string timeZone) =>
        Error.Create(ErrorKind.Validation, TimezoneInvalidCode, ("timezone", timeZone));
}
