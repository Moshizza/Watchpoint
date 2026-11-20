using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Integrations.Internal;
using Watchpoint.Integrations.Internal.Connections;
using Watchpoint.Integrations.Internal.Context;
using Watchpoint.Integrations.Internal.Hosting;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Integrations.Contracts;

/// <summary>Service registration of the integration runtime (spec IRT §3). Requires the Model and Storage modules.</summary>
public static class IntegrationsServiceCollectionExtensions
{
    /// <summary>Adds the package catalogue, watcher, verifier, load contexts and the <c>wp_integrations</c> storage.</summary>
    /// <param name="services">The services.</param>
    /// <param name="options">The settings.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointIntegrations(this IServiceCollection services, IntegrationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDocumentReader>(_ => DocumentReaderFactory.FromEmbeddedSchemas());
        services.TryAddSingleton<ILibraryValidator, LibraryValidator>();
        services.AddSingleton(options);
        services.AddSingleton<TrustedKeys>(sp => ActivatorUtilities.CreateInstance<TrustedKeys>(sp));
        services.AddSingleton<SignatureVerifier>();
        services.AddSingleton<PackageVerifier>();
        services.AddSingleton<PackageLoadContextPool>();
        services.TryAddSingleton<IPackageConnections, NoPackageConnections>();
        services.TryAddSingleton<IPackageStore, EfPackageStore>();
        services.TryAddSingleton<IFolderChangeSourceFactory, FileSystemFolderChangeSource.Factory>();
        services.AddSingleton<PackageCatalog>(sp => ActivatorUtilities.CreateInstance<PackageCatalog>(sp));
        services.AddSingleton<IPackageCatalog>(sp => sp.GetRequiredService<PackageCatalog>());
        services.AddSingleton<PackageWatcher>();
        services.AddSingleton<IPackageWatcher>(sp => sp.GetRequiredService<PackageWatcher>());
        services.AddSingleton<IntegrationsMetrics>();

        services.AddModuleStorage<IntegrationsDbContext>(IntegrationsDbContext.ModuleName, p => p.Add<IntegrationsPostgresDbContext>("postgres"));
        services.AddHealthChecks().AddCheck<PackageScanHealthCheck>(PackageScanHealthCheck.Name, tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Adds the connections (spec IRT §7–§10, T1.16): the connection manager (also <see cref="IConnectionCommandChannel"/>
    /// and the ingest's <c>IIngestFloodingListener</c>), the in-process host and the connector context. Needs the
    /// services of PIP (ingest), REG (reader, writer, reconciler), SEC (vault) and MOD.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="options">The <c>Connections:*</c> settings.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointConnections(this IServiceCollection services, ConnectionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<InboundRouter>();
        services.AddSingleton(sp => new ConnectorMeter(sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.TryAddSingleton<IConnectionRepository, EfConnectionRepository>();
        services.TryAddSingleton<IConnectionStoreRepository, EfConnectionStoreRepository>();
        services.TryAddSingleton<IConnectionAdmission, AllowAllConnectionAdmission>();
        services.AddSingleton<IConnectionHost, InProcessHost>();
        services.AddSingleton<ConnectorContextFactory>();
        services.AddSingleton<InventoryCoordinator>();
        services.AddSingleton(sp => new ConnectionStateRecorder(sp.GetRequiredService<IConnectionRepository>(), sp.GetRequiredService<IObservationIngest>(),
            sp.GetRequiredService<Registry.Contracts.IRegistryReader>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConnectionStateRecorder>>(), sp.GetService<IEventLogWriter>(), sp.GetService<ILogRecordTypeRegistry>()));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ConnectionManager>(sp));
        services.AddSingleton<IConnectionManager>(sp => sp.GetRequiredService<ConnectionManager>());
        services.AddSingleton<IConnectionCommandChannel>(sp => sp.GetRequiredService<ConnectionManager>());

        // Resolved on first use: the manager depends on the catalogue and the ingest, which depend on these.
        services.Replace(ServiceDescriptor.Singleton<IPackageConnections, LazyPackageConnections>());
        services.AddSingleton<IIngestFloodingListener, LazyFloodingListener>();
        return services;
    }
}
