using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Integrations.Internal;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;
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
}
