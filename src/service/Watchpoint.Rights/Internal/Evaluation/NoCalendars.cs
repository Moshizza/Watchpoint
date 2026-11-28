using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Scheduling;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>Used when the scheduling module is not installed: every calendar is unknown, so scheduled grants never apply.</summary>
internal sealed class NoCalendars : ICalendarService
{
    public event EventHandler<CalendarChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public bool IsActive(string calendarKey, DateTimeOffset instant) => false;

    public Result<bool> IsActiveStrict(CalendarId id, DateTimeOffset instant) => Error.NotFound("scheduling.calendar.not_found");

    public DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after, int maxDaysAhead = 400) => null;

    public IReadOnlyList<ActivePeriod> GetPeriods(string calendarKey, DateTimeOffset from, DateTimeOffset to) => [];

    public Result<Calendar> Get(string key) => Error.NotFound("scheduling.calendar.not_found");
}
