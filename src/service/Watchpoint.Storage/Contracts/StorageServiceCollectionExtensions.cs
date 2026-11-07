using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Storage.Internal;

namespace Watchpoint.Storage.Contracts;

/// <summary>Registration of the storage module (providers register themselves, e.g. <c>AddPostgresStorage()</c>).</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>Health check name (tag <c>ready</c>).</summary>
    public const string HealthCheckName = "storage";

    /// <summary>Registers settings, store map, connections, start-up (wait and migrations), health check and metrics.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration root (section <c>Database</c>).</param>
    /// <param name="isDevelopment">Whether the node runs in Development (<c>PasswordDev</c> allowed).</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointStorage(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(StorageSettings.Bind(configuration));
        services.AddSingleton(new StorageEnvironment(isDevelopment));
        services.AddSingleton<StoreMapHolder>();
        services.AddSingleton<IStoreConnectionFactory, StoreConnectionFactory>();
        services.TryAddSingleton<IDatabasePasswordProvider, DevelopmentPasswordProvider>();
        services.AddSingleton<ModuleContextActivator>();
        services.AddSingleton<IModuleMigrator, EfModuleMigrator>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<IStorageStartup, StorageStartup>();
        services.AddMetrics();
        services.AddSingleton<StorageMetrics>();
        services.AddHostedService<DatabaseSizePoller>();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>(HealthCheckName, tags: ["ready"]);
        return services;
    }
}
