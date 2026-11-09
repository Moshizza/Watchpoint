using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Scheduling;

/// <summary>Evaluation service for schedules and calendars (spec SCH §4).</summary>
public interface ICalendarService
{
    /// <summary>Checks whether a calendar is active at the given instant. Unknown keys return false and log once.</summary>
    bool IsActive(string calendarKey, DateTimeOffset instant);

    /// <summary>Strict check whether a calendar is active by id. Returns error if not found.</summary>
    Result<bool> IsActiveStrict(CalendarId id, DateTimeOffset instant);

    /// <summary>Returns the next active/inactive state transition after the given instant.</summary>
    DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after, int maxDaysAhead = 400);

    /// <summary>Returns the active periods for a calendar within the given range (limited to 62 days).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Matches spec SCH §4 signature GetPeriods(calendarKey, from, to).")]
    IReadOnlyList<ActivePeriod> GetPeriods(string calendarKey, DateTimeOffset from, DateTimeOffset to);

    /// <summary>Gets a calendar by key.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Matches spec SCH §4 method Get(key).")]
    Result<Calendar> Get(string key);

    /// <summary>Raised when a calendar is added, updated, or removed.</summary>
    event EventHandler<CalendarChangedEventArgs>? Changed;
}

/// <summary>Service for registering and managing time triggers (spec SCH §5).</summary>
public interface ITimeTriggerService
{
    /// <summary>
    /// Registers an in-memory time trigger callback. Key must be stable across restarts.
    /// </summary>
    IDisposable Register(
        string owner,
        string key,
        TriggerSpec spec,
        MissedPolicy missed,
        Func<TriggerFired, CancellationToken, ValueTask> callback);

    /// <summary>Returns the next scheduled fire time for a registered trigger.</summary>
    DateTimeOffset? NextFire(string owner, string key);
}

/// <summary>Provider interface implemented by modules that reference calendars to prevent deletion (spec SCH §7).</summary>
public interface ICalendarReferenceProvider
{
    /// <summary>Returns descriptions of any objects referencing the specified calendar.</summary>
    ValueTask<IReadOnlyList<string>> GetReferencesAsync(CalendarId calendarId, CancellationToken ct);
}
