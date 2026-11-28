using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Api.Internal.Idempotency;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Api.Contracts;

/// <summary>Configuration-store registration of the API (schema <c>wp_api</c>, spec API §5.5).</summary>
public static class ApiStorage
{
    /// <summary>
    /// Stores idempotency records in <c>wp_api.idempotency</c> instead of node memory. Call before
    /// <see cref="ApiServiceCollectionExtensions.AddWatchpointApi"/>; needs the storage module.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddApiStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModuleStorage<IdempotencyDbContext>(IdempotencyDbContext.ModuleName, p => p.Add<IdempotencyPostgresDbContext>("postgres"));
        services.AddSingleton<IIdempotencyStore, DbIdempotencyStore>();
        return services;
    }
}
