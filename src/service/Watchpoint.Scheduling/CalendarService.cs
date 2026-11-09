using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NodaTime;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Scheduling;

/// <summary>
/// Implementation of ICalendarService (spec SCH §4).
/// Caches active periods per calendar version and day, pure evaluation, transition calculation.
/// </summary>
public sealed class CalendarService : ICalendarService
{
    private readonly ILogger<CalendarService> _logger;
    private readonly ConcurrentDictionary<string, Calendar> _calendarsByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<CalendarId, Calendar> _calendarsById = new();
    private readonly ConcurrentDictionary<HolidaySetId, HolidaySet> _holidaySets = new();
    private readonly HashSet<string> _loggedUnknownKeys = [];
    private readonly Lock _loggedKeysGate = new();
    private readonly IEnumerable<ICalendarReferenceProvider> _referenceProviders;
    private string? _siteTimeZone;

    /// <summary>Creates a CalendarService.</summary>
    public CalendarService(
        ILogger<CalendarService> logger,
        IEnumerable<ICalendarReferenceProvider>? referenceProviders = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _referenceProviders = referenceProviders ?? [];
    }

    /// <summary>Gets or sets the fallback site time zone IANA id.</summary>
    public string? SiteTimeZone
    {
        get => _siteTimeZone;
        set => _siteTimeZone = value;
    }

    /// <inheritdoc/>
    public event EventHandler<CalendarChangedEventArgs>? Changed;

    /// <summary>Adds or updates a calendar in memory and raises Changed event.</summary>
    public Result<bool> Upsert(Calendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        var val = CalendarEvaluation.ValidateCalendar(calendar);
        if (!val.IsSuccess)
        {
            return val;
        }

        if (_calendarsByKey.TryGetValue(calendar.Key, out var existing) && existing.Id != calendar.Id)
        {
            return SchedulingErrors.CalendarKeyTaken(calendar.Key);
        }

        _calendarsByKey[calendar.Key] = calendar;
        _calendarsById[calendar.Id] = calendar;

        Changed?.Invoke(this, new CalendarChangedEventArgs(calendar.Key, calendar.Id));
        return true;
    }

    /// <summary>Adds or updates a holiday set in memory.</summary>
    public void UpsertHolidaySet(HolidaySet holidaySet)
    {
        ArgumentNullException.ThrowIfNull(holidaySet);
        _holidaySets[holidaySet.Id] = holidaySet;
    }

    /// <summary>Deletes a calendar if not in use by any referencing module.</summary>
    public async ValueTask<Result<bool>> DeleteAsync(CalendarId id, CancellationToken ct = default)
    {
        if (!_calendarsById.TryGetValue(id, out var cal))
        {
            return SchedulingErrors.CalendarNotFound(id);
        }

        var refs = new List<string>();
        foreach (var provider in _referenceProviders)
        {
            var r = await provider.GetReferencesAsync(id, ct).ConfigureAwait(false);
            if (r is not null && r.Count > 0)
            {
                refs.AddRange(r);
            }
        }

        if (refs.Count > 0)
        {
            return SchedulingErrors.CalendarInUse(id, refs);
        }

        _calendarsById.TryRemove(id, out _);
        _calendarsByKey.TryRemove(cal.Key, out _);

        Changed?.Invoke(this, new CalendarChangedEventArgs(cal.Key, id));
        return true;
    }

    /// <inheritdoc/>
    public bool IsActive(string calendarKey, DateTimeOffset instant)
    {
        if (!_calendarsByKey.TryGetValue(calendarKey, out var cal))
        {
            bool shouldLog = false;
            lock (_loggedKeysGate)
            {
                if (_loggedUnknownKeys.Add(calendarKey))
                {
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                SchedulingLog.UnknownCalendarKey(_logger, calendarKey);
            }

            return false;
        }

        return CalendarEvaluation.IsActive(cal, instant, _holidaySets, _siteTimeZone);
    }

    /// <inheritdoc/>
    public Result<bool> IsActiveStrict(CalendarId id, DateTimeOffset instant)
    {
        if (!_calendarsById.TryGetValue(id, out var cal))
        {
            return SchedulingErrors.CalendarNotFound(id);
        }

        return CalendarEvaluation.IsActive(cal, instant, _holidaySets, _siteTimeZone);
    }

    /// <inheritdoc/>
    public Result<Calendar> Get(string key)
    {
        if (!_calendarsByKey.TryGetValue(key, out var cal))
        {
            return SchedulingErrors.CalendarNotFound(key);
        }

        return cal;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ActivePeriod> GetPeriods(string calendarKey, DateTimeOffset from, DateTimeOffset to)
    {
        if (to < from)
        {
            return [];
        }

        var limit = from.AddDays(62);
        var effectiveTo = to > limit ? limit : to;

        if (!_calendarsByKey.TryGetValue(calendarKey, out var cal))
        {
            return [];
        }

        var zone = CalendarEvaluation.ResolveZone(cal, _siteTimeZone);
        var startLocal = Instant.FromDateTimeOffset(from).InZone(zone).Date.PlusDays(-1);
        var endLocal = Instant.FromDateTimeOffset(effectiveTo).InZone(zone).Date.PlusDays(1);

        var periods = new List<ActivePeriod>();
        for (var d = startLocal; d <= endLocal; d = d.PlusDays(1))
        {
            var dateOnly = new DateOnly(d.Year, d.Month, d.Day);
            var ranges = CalendarEvaluation.GetActiveRangesForDate(cal, dateOnly, _holidaySets);
            var dayPeriods = CalendarEvaluation.ConvertToUtcPeriods(d, ranges, zone);

            foreach (var p in dayPeriods)
            {
                if (p.End > from && p.Start < effectiveTo)
                {
                    var clampedStart = p.Start < from ? from : p.Start;
                    var clampedEnd = p.End > effectiveTo ? effectiveTo : p.End;
                    if (clampedStart < clampedEnd)
                    {
                        periods.Add(new ActivePeriod(clampedStart, clampedEnd));
                    }
                }
            }
        }

        return MergePeriods(periods);
    }

    /// <inheritdoc/>
    public DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after, int maxDaysAhead = 400)
    {
        if (!_calendarsByKey.TryGetValue(calendarKey, out var cal))
        {
            return null;
        }

        var zone = CalendarEvaluation.ResolveZone(cal, _siteTimeZone);
        var afterInstant = Instant.FromDateTimeOffset(after);
        var startLocalDate = afterInstant.InZone(zone).Date;
        var endLocalDate = startLocalDate.PlusDays(maxDaysAhead);

        bool currentActive = IsActive(calendarKey, after);
        var transitions = new SortedSet<DateTimeOffset>();

        for (var d = startLocalDate; d <= endLocalDate; d = d.PlusDays(1))
        {
            var dateOnly = new DateOnly(d.Year, d.Month, d.Day);
            var ranges = CalendarEvaluation.GetActiveRangesForDate(cal, dateOnly, _holidaySets);
            var dayPeriods = CalendarEvaluation.ConvertToUtcPeriods(d, ranges, zone);

            foreach (var p in dayPeriods)
            {
                if (p.Start > after)
                {
                    transitions.Add(p.Start);
                }

                if (p.End > after)
                {
                    transitions.Add(p.End);
                }
            }
        }

        foreach (var t in transitions)
        {
            var probe = t.AddMilliseconds(1);
            if (IsActive(calendarKey, probe) != currentActive)
            {
                return t;
            }
        }

        return null;
    }

    private static List<ActivePeriod> MergePeriods(List<ActivePeriod> periods)
    {
        if (periods.Count <= 1)
        {
            return periods;
        }

        var sorted = periods.OrderBy(p => p.Start).ToList();
        var merged = new List<ActivePeriod>();
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.Start <= current.End)
            {
                var maxEnd = next.End > current.End ? next.End : current.End;
                current = new ActivePeriod(current.Start, maxEnd);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }
}
