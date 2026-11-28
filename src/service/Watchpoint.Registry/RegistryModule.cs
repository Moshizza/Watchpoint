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
    /// <summary>The health check name registered under the <c>ready</c> tag (REG §12).</summary>
    public const string RegistrySiteHealthCheckName = "registry-site";

    /// <summary>
    /// Adds Watchpoint Registry services, including DbContext, in-memory reader, writer and the transactional outbox
    /// (<see cref="IRegistryOutbox"/>, published to the event log by the pipeline module).
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

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RegistryOutbox>();
        services.TryAddSingleton<IRegistryOutbox>(sp => sp.GetRequiredService<RegistryOutbox>());
        services.TryAddSingleton<DeviceEnrichmentCache>();
        services.TryAddSingleton<RegistryChangeFeed>();
        services.TryAddSingleton(sp => new RegistrySiteHealthCheck(sp.GetRequiredService<IRegistryReader>(), siteId));
        services.AddHealthChecks().AddCheck<RegistrySiteHealthCheck>(RegistrySiteHealthCheckName, tags: ["ready"]);

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
        services.TryAddSingleton(sp => sp.GetRequiredService<IRegistryWriter>() as IRegistryTransactionalWriter
            ?? throw new InvalidOperationException("The registry writer does not write in a caller's transaction."));
        services.TryAddSingleton<IInventoryReconciler, InventoryReconciler>();

        return services;
    }
}
