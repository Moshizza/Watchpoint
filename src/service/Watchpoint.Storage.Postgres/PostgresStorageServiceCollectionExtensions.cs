using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres;

/// <summary>Registration of the PostgreSQL provider.</summary>
public static class PostgresStorageServiceCollectionExtensions
{
    /// <summary>Registers the <c>postgres</c> storage provider (with <c>AddWatchpointStorage</c>).</summary>
    /// <param name="services">The services.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddPostgresStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PostgresDataSources>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStorageProvider, PostgresProvider>());
        return services;
    }
}
