using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Internal;

namespace Watchpoint.State;

/// <summary>Service registration of the state module (spec STA §3).</summary>
public static class StateModule
{
    /// <summary>
    /// Adds the live state store: the per-partition maps (<see cref="IStateReader"/>), the overall-state aggregates
    /// (<see cref="IAggregateReader"/>), the change feed (<see cref="IStateChangeFeed"/>), the partition subscriber
    /// (registered as <see cref="IPartitionSubscriber"/>, applying event log records at order 100) and the registry
    /// change listener that creates, updates and removes entities (spec STA §6.3), the snapshotter and the recovery
    /// (<see cref="IStateLifecycle"/>, spec STA §8; needs an <c>IStateStore</c>), the last value source of the
    /// pipeline (<see cref="ILastValueSource"/>) and the health check <c>ready</c> of the recovery. Settings come from
    /// the <c>State</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">A <c>State</c> setting is out of range.</exception>
    public static IServiceCollection AddWatchpointState(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddState(StateOptions.FromConfiguration(configuration));
    }

    internal static IServiceCollection AddState(this IServiceCollection services, StateOptions options)
    {
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<AggregateCounters>();
        services.AddSingleton(sp => new RecentChangesRing(sp.GetRequiredService<StateOptions>().RecentChanges));
        services.AddSingleton<ChangeFanout>();
        services.AddSingleton<IStateChangeFeed>(sp => sp.GetRequiredService<ChangeFanout>());
        services.AddSingleton<StateStore>();
        services.AddSingleton<IStateReader>(sp => sp.GetRequiredService<StateStore>());
        services.AddSingleton<IAggregateReader>(sp => sp.GetRequiredService<StateStore>());
        services.AddSingleton<IPartitionWork, DispatcherPartitionWork>();
        services.AddSingleton<ServerAvailabilityCascade>();
        services.AddSingleton<StateSubscriber>();
        services.AddSingleton<IPartitionSubscriber>(sp => sp.GetRequiredService<StateSubscriber>());
        services.AddSingleton<ILastValueSource>(sp => sp.GetRequiredService<StateStore>());
        services.AddSingleton<Snapshotter>();
        services.AddSingleton<StateMetrics>();
        services.AddSingleton<StateRecovery>();
        services.AddSingleton<IStateLifecycle>(sp =>
        {
            _ = sp.GetRequiredService<StateMetrics>();
            return sp.GetRequiredService<StateRecovery>();
        });
        services.AddHealthChecks().AddCheck<StateRecoveryHealthCheck>(StateRecoveryHealthCheck.Name, HealthStatus.Unhealthy, ["ready"]);
        services.AddSingleton<MembershipTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<MembershipTracker>());
        services.AddHostedService<AggregateConsistencyService>();
        return services;
    }
}
