using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Watchpoint.Scheduling;

/// <summary>
/// DbContext-backed trigger state store for persisting in table <c>wp_scheduling.trigger_state</c>.
/// </summary>
public sealed class DbTriggerStateStore : ITriggerStateStore
{
    private readonly IDbContextFactory<SchedulingDbContext> _factory;

    /// <summary>Creates a DbTriggerStateStore.</summary>
    public DbTriggerStateStore(IDbContextFactory<SchedulingDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc/>
    public async ValueTask<DateTimeOffset?> GetLastFiredAsync(string owner, string key, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.TriggerStates.FindAsync([owner, key], ct).ConfigureAwait(false);
        return entity?.LastFiredAt;
    }

    /// <inheritdoc/>
    public async ValueTask SetLastFiredAsync(string owner, string key, DateTimeOffset firedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.TriggerStates.FindAsync([owner, key], ct).ConfigureAwait(false);
        if (entity is null)
        {
            db.TriggerStates.Add(new TriggerStateEntity
            {
                Owner = owner,
                Key = key,
                LastFiredAt = firedAt,
            });
        }
        else
        {
            entity.LastFiredAt = firedAt;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Service collection extension methods for scheduling module (spec SCH §2).
/// </summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>Registers scheduling services, state store, and evaluation components.</summary>
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICalendarService, CalendarService>();
        services.AddSingleton<ITriggerStateStore, InMemoryTriggerStateStore>();
        services.AddSingleton<ILocationGeoProvider, DefaultLocationGeoProvider>();
        services.AddSingleton<ITriggerDispatcher, DefaultTriggerDispatcher>();
        services.AddSingleton<ITimeTriggerService, TimeTriggerService>();

        return services;
    }
}
