using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Hosting;
using Watchpoint.Rights.Contracts;
using Watchpoint.Scheduling;

namespace Watchpoint.Host.Modules;

/// <summary>Rights module (spec RGT, HST §5.2): permission catalogue, grants, evaluator; replaces the M1–M2 permission and command fallbacks.</summary>
public sealed class RightsModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Rights";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Grant schedules use the scheduling module's calendars when it is installed (otherwise scheduled grants never apply).
        services.TryAddSingleton<IRightsCalendars>(sp => sp.GetService<ICalendarService>() is { } calendars
            ? new SchedulingCalendars(calendars)
            : Watchpoint.Rights.RightsModule.NoCalendarsInstance);
        Watchpoint.Rights.RightsModule.AddWatchpointRights(services);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => Watchpoint.Rights.RightsEndpoints.MapRightsEndpoints(endpoints);

    private sealed class SchedulingCalendars : IRightsCalendars
    {
        private readonly ICalendarService _calendars;

        public SchedulingCalendars(ICalendarService calendars) => _calendars = calendars;

        public event EventHandler? Changed
        {
            add => _calendars.Changed += (s, _) => value?.Invoke(s, EventArgs.Empty);
            remove { }
        }

        public bool Exists(string calendarKey) => _calendars.Get(calendarKey).IsSuccess;

        public bool IsActive(string calendarKey, DateTimeOffset instant) => _calendars.IsActive(calendarKey, instant);

        public DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after) => _calendars.NextTransition(calendarKey, after);
    }
}
