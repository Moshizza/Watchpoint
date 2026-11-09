using NodaTime;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;

namespace Watchpoint.Scheduling;

/// <summary>
/// Pure evaluation algorithms for calendars and schedules (spec SCH §3, §4).
/// Converts local times to UTC using NodaTime LenientResolver:
/// - A range starting in a skipped hour starts at the first valid instant after the gap.
/// - A range ending in a repeated hour ends at the later occurrence.
/// Precedence for a given date: explicit Exceptions &gt; holiday sets (in list order) &gt; weekly.
/// </summary>
public static class CalendarEvaluation
{
    /// <summary>
    /// Validates a calendar definition: key pattern, no start &gt;= end, and no overlapping ranges within any day.
    /// </summary>
    public static Result<bool> ValidateCalendar(Calendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (!Calendar.IsValidKey(calendar.Key))
        {
            return CommonErrors.InvalidFormat("^[a-z][a-z0-9-]{1,63}$");
        }

        if (!string.IsNullOrEmpty(calendar.TimeZone))
        {
            var tzRes = SiteTimeZone.FromIana(calendar.TimeZone);
            if (!tzRes.IsSuccess)
            {
                return SchedulingErrors.TimezoneInvalid(calendar.TimeZone);
            }
        }

        if (calendar.Weekly is not null)
        {
            foreach (var (day, ranges) in calendar.Weekly)
            {
                var val = ValidateRanges(ranges);
                if (!val.IsSuccess)
                {
                    return val;
                }
            }
        }

        if (calendar.Exceptions is not null)
        {
            foreach (var ex in calendar.Exceptions)
            {
                if (ex.Mode == ExceptionMode.Custom && ex.Ranges is not null)
                {
                    var val = ValidateRanges(ex.Ranges);
                    if (!val.IsSuccess)
                    {
                        return val;
                    }
                }
            }
        }

        if (calendar.HolidaySets is not null)
        {
            foreach (var hs in calendar.HolidaySets)
            {
                if (hs.Mode == ExceptionMode.Custom && hs.Ranges is not null)
                {
                    var val = ValidateRanges(hs.Ranges);
                    if (!val.IsSuccess)
                    {
                        return val;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Validates that ranges do not have start &gt;= end and do not overlap.
    /// </summary>
    public static Result<bool> ValidateRanges(IReadOnlyList<TimeRange>? ranges)
    {
        if (ranges is null || ranges.Count == 0)
        {
            return true;
        }

        foreach (var r in ranges)
        {
            var v = r.Validate();
            if (!v.IsSuccess)
            {
                return v;
            }
        }

        // Check for overlaps: sort ranges by start time
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var cur = sorted[i];
            var next = sorted[i + 1];

            // If cur ends at 24:00, anything after it in the same day overlaps
            if (cur.IsEndMidnightNextDay)
            {
                return SchedulingErrors.RangeOverlap(cur.ToString(), next.ToString());
            }

            // Since end is exclusive, cur.End > next.Start is an overlap
            if (cur.End > next.Start)
            {
                return SchedulingErrors.RangeOverlap(cur.ToString(), next.ToString());
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves active ranges for a specific date given a calendar and loaded holiday sets.
    /// Precedence: explicit Exceptions &gt; holiday sets (in list order) &gt; weekly.
    /// </summary>
    public static IReadOnlyList<TimeRange> GetActiveRangesForDate(
        Calendar calendar,
        DateOnly date,
        IReadOnlyDictionary<HolidaySetId, HolidaySet>? holidaySets)
    {
        // 1. Check calendar validity window
        if (calendar.ValidFrom.HasValue && date < calendar.ValidFrom.Value)
        {
            return [];
        }

        if (calendar.ValidUntil.HasValue && date > calendar.ValidUntil.Value)
        {
            return [];
        }

        // 2. Explicit exceptions
        if (calendar.Exceptions is not null)
        {
            var ex = calendar.Exceptions.FirstOrDefault(e => e.Date == date);
            if (ex is not null)
            {
                return ex.Mode switch
                {
                    ExceptionMode.Closed => [],
                    ExceptionMode.Open => [TimeRange.UntilMidnight(LocalTime.Midnight)],
                    ExceptionMode.Custom => ex.Ranges ?? [],
                    _ => [],
                };
            }
        }

        // 3. Holiday sets (in list order)
        if (calendar.HolidaySets is not null && holidaySets is not null)
        {
            foreach (var hsUse in calendar.HolidaySets)
            {
                if (holidaySets.TryGetValue(hsUse.Set, out var hs))
                {
                    bool isHoliday = hs.Holidays.Any(h =>
                        h.Date == date ||
                        (h.RecursYearly && h.Date.Month == date.Month && h.Date.Day == date.Day));

                    if (isHoliday)
                    {
                        return hsUse.Mode switch
                        {
                            ExceptionMode.Closed => [],
                            ExceptionMode.Open => [TimeRange.UntilMidnight(LocalTime.Midnight)],
                            ExceptionMode.Custom => hsUse.Ranges ?? [],
                            _ => [],
                        };
                    }
                }
            }
        }

        // 4. Weekly schedule
        if (calendar.Weekly is not null)
        {
            var isoDay = (IsoDayOfWeek)(int)date.DayOfWeek;
            if (isoDay == 0)
            {
                isoDay = IsoDayOfWeek.Sunday;
            }

            if (calendar.Weekly.TryGetValue(isoDay, out var ranges) && ranges is not null)
            {
                return ranges;
            }
        }

        return [];
    }

    /// <summary>
    /// Resolves the effective DateTimeZone for the calendar.
    /// If calendar.TimeZone is null or empty, uses siteTimeZone.
    /// </summary>
    public static DateTimeZone ResolveZone(Calendar calendar, string? siteTimeZone)
    {
        var tzId = !string.IsNullOrEmpty(calendar.TimeZone) ? calendar.TimeZone : siteTimeZone;
        if (string.IsNullOrEmpty(tzId))
        {
            return DateTimeZone.Utc;
        }

        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(tzId) ?? DateTimeZone.Utc;
    }

    /// <summary>
    /// Converts a local date and a list of daily TimeRanges into UTC ActivePeriods using LenientResolver.
    /// In a spring gap: range start moves forward to first valid instant after gap.
    /// In an autumn overlap: range end moves to later occurrence.
    /// </summary>
    public static IReadOnlyList<ActivePeriod> ConvertToUtcPeriods(
        LocalDate localDate,
        IReadOnlyList<TimeRange> ranges,
        DateTimeZone zone)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        var periods = new List<ActivePeriod>(ranges.Count);

        foreach (var range in ranges)
        {
            var startLocal = localDate.At(range.Start);
            var startZoned = ResolveStart(startLocal, zone);

            ZonedDateTime endZoned;
            if (range.IsEndMidnightNextDay)
            {
                var nextDayLocal = localDate.PlusDays(1).AtMidnight();
                endZoned = ResolveEnd(nextDayLocal, zone);
            }
            else
            {
                var endLocal = localDate.At(range.End);
                endZoned = ResolveEnd(endLocal, zone);
            }

            var startUtc = startZoned.ToInstant().ToDateTimeOffset();
            var endUtc = endZoned.ToInstant().ToDateTimeOffset();

            if (startUtc < endUtc)
            {
                periods.Add(new ActivePeriod(startUtc, endUtc));
            }
        }

        return periods;
    }

    /// <summary>
    /// Evaluates if an instant is within any active period for the given calendar.
    /// </summary>
    public static bool IsActive(
        Calendar calendar,
        DateTimeOffset instant,
        IReadOnlyDictionary<HolidaySetId, HolidaySet>? holidaySets,
        string? siteTimeZone)
    {
        var zone = ResolveZone(calendar, siteTimeZone);
        var nodaInstant = Instant.FromDateTimeOffset(instant);
        var zonedDateTime = nodaInstant.InZone(zone);
        var localDate = zonedDateTime.Date;

        for (int dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var checkLocalDate = localDate.PlusDays(dayOffset);
            var checkDateOnly = new DateOnly(checkLocalDate.Year, checkLocalDate.Month, checkLocalDate.Day);
            var ranges = GetActiveRangesForDate(calendar, checkDateOnly, holidaySets);
            var periods = ConvertToUtcPeriods(checkLocalDate, ranges, zone);

            foreach (var p in periods)
            {
                if (instant >= p.Start && instant < p.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ZonedDateTime ResolveStart(LocalDateTime local, DateTimeZone zone)
    {
        var mapping = zone.MapLocal(local);
        return mapping.Count switch
        {
            1 => mapping.Single(),
            2 => mapping.First(),
            _ => mapping.LateInterval.Start.InZone(zone),
        };
    }

    private static ZonedDateTime ResolveEnd(LocalDateTime local, DateTimeZone zone)
    {
        var mapping = zone.MapLocal(local);
        return mapping.Count switch
        {
            1 => mapping.Single(),
            2 => mapping.Last(),
            _ => mapping.LateInterval.Start.InZone(zone),
        };
    }
}
