using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Watchpoint.Registry.Internal.Storage;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Registry;

/// <summary>
/// Service collection extensions for registering Watchpoint Registry module.
/// </summary>
public static class RegistryModule
{
    /// <summary>
    /// Adds Watchpoint Registry services, including DbContext, in-memory reader, and writer.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="siteId">
    /// The installation's site id (<c>Site:Id</c> of the node settings, HST §4). The site row is created with this id
    /// (REG §4 R1); a stored row with another id is kept and reported once (event 1504).
    /// </param>
    public static IServiceCollection AddWatchpointRegistry(this IServiceCollection services, SiteId siteId)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddModuleStorage<RegistryDbContext>(
            RegistryDbContext.ModuleName,
            providers => providers.Add<RegistryPostgresDbContext>("postgres"));

        services.TryAddSingleton<NoOpRegistryChangePublisher>();
        services.TryAddSingleton<IRegistryChangePublisher>(sp => sp.GetRequiredService<NoOpRegistryChangePublisher>());
        services.TryAddSingleton<DeviceEnrichmentCache>();

        // Snapshot holder
        RegistrySnapshot? currentSnapshot = null;
        services.AddSingleton<Func<RegistrySnapshot>>(sp => () =>
        {
            if (currentSnapshot != null)
                return currentSnapshot;

            var factory = sp.GetRequiredService<IDbContextFactory<RegistryDbContext>>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(RegistrySnapshotLoader)) ?? NullLogger.Instance;
            using var db = factory.CreateDbContext();
            currentSnapshot = RegistrySnapshotLoader.Load(db, siteId, logger);
            return currentSnapshot;
        });

        services.AddSingleton<Action<RegistrySnapshot>>(sp => snap =>
        {
            currentSnapshot = snap;
        });

        services.TryAddSingleton<IRegistryReader, RegistryReader>();
        services.TryAddSingleton<IRegistryWriter, RegistryWriter>();
        services.TryAddSingleton<IInventoryReconciler, InventoryReconciler>();

        return services;
    }
}

/// <summary>
/// Default no-op change publisher for when PIP is not registered.
/// </summary>
public sealed class NoOpRegistryChangePublisher : IRegistryChangePublisher
{
    /// <inheritdoc />
    public Task PublishAsync(RegistryApplyResult result, ChangeContext context, CancellationToken ct = default) =>
        Task.CompletedTask;
}
