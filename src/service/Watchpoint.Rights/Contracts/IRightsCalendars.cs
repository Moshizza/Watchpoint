namespace Watchpoint.Rights.Contracts;

/// <summary>The calendar queries the evaluator needs for grant schedules (answered by the scheduling module through the host).</summary>
public interface IRightsCalendars
{
    /// <summary>Raised when a calendar is added, changed or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Whether the calendar exists.</summary>
    /// <param name="calendarKey">The calendar key.</param>
    /// <returns><see langword="true"/> when known.</returns>
    bool Exists(string calendarKey);

    /// <summary>Whether the calendar is active at an instant (unknown keys are inactive).</summary>
    /// <param name="calendarKey">The calendar key.</param>
    /// <param name="instant">The instant.</param>
    /// <returns><see langword="true"/> when active.</returns>
    bool IsActive(string calendarKey, DateTimeOffset instant);

    /// <summary>The next active/inactive transition after an instant.</summary>
    /// <param name="calendarKey">The calendar key.</param>
    /// <param name="after">The instant.</param>
    /// <returns>The transition, or <see langword="null"/> when none.</returns>
    DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after);
}
