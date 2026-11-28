using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>Used when the scheduling module is not installed: every calendar is unknown, so scheduled grants never apply.</summary>
internal sealed class NoCalendars : IRightsCalendars
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public bool Exists(string calendarKey) => false;

    public bool IsActive(string calendarKey, DateTimeOffset instant) => false;

    public DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after) => null;
}
