using System.Globalization;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal;
using Watchpoint.History.Internal.Lag;
using Watchpoint.History.Internal.Policies;
using Watchpoint.History.Internal.Queries;
using Watchpoint.History.Internal.Writers;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History;

/// <summary>Service registration of the history module (spec HIS §3).</summary>
public static class HistoryModule
{
    /// <summary>
    /// Adds history: the writers (a hosted service running the five durable consumers), the policy resolver, the optional periodic
    /// sampler (when an <see cref="IPeriodicPropertySource"/> is registered), the history store of the configured provider and
    /// <see cref="IHistoryQueries"/>. Settings come from the <c>History</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static IServiceCollection AddWatchpointHistory(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddHistory(ReadSettings(configuration));
    }

    internal static IServiceCollection AddHistory(this IServiceCollection services, HistorySettings settings)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(settings);
        services.AddSingleton<HistoryMetrics>();
        services.AddSingleton<IHistoryStore>(sp =>
        {
            var connections = sp.GetRequiredService<IStoreConnectionFactory>();
            return connections.GetProvider(StoreKind.History).CreateHistoryStore(connections.GetDatabase(StoreKind.History));
        });
        services.AddSingleton<IHistoryEnrichment, RegistryHistoryEnrichment>();
        services.AddSingleton(_ => new LastStoredCache(settings.LastStoredCacheSize));
        services.AddSingleton<PolicyResolver>();
        services.AddSingleton<LagMonitor>();
        services.AddSingleton<ConsumerRunner>();
        services.AddSingleton<HistoryWriters>();
        services.AddSingleton<IHistoryQueries, HistoryQueries>();
        services.AddHostedService<HistoryService>();
        return services;
    }

    internal static HistorySettings ReadSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection(HistorySettings.SectionName);
        var settings = new HistorySettings();
        List<string> problems = [];
        if (section["BatchSize"] is { } batch)
        {
            if (int.TryParse(batch, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 10_000)
            {
                settings = settings with { BatchSize = size };
            }
            else
            {
                problems.Add($"History:BatchSize '{batch}' must be a number from 1 to 10000.");
            }
        }

        settings = settings with
        {
            LagWarning = Duration(section, "LagWarning", settings.LagWarning, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), problems),
            QueryTimeout = Duration(section, "QueryTimeout", settings.QueryTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), problems),
        };
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', problems));
        }

        return settings;
    }

    private static TimeSpan Duration(IConfigurationSection section, string name, TimeSpan fallback, TimeSpan min, TimeSpan max, List<string> problems)
    {
        if (section[name] is not { } text)
        {
            return fallback;
        }

        try
        {
            var value = XmlConvert.ToTimeSpan(text);
            if (value >= min && value <= max)
            {
                return value;
            }
        }
        catch (FormatException)
        {
            // Reported below.
        }

        problems.Add($"History:{name} '{text}' must be an ISO 8601 duration from {XmlConvert.ToString(min)} to {XmlConvert.ToString(max)}.");
        return fallback;
    }
}
