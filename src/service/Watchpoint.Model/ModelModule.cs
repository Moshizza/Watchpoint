using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Internal;
using Watchpoint.Model.Internal.Storage;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Model;

/// <summary>Service registration extensions for Watchpoint.Model (spec MOD §3, §5.1).</summary>
public static class ModelModule
{
    /// <summary>Adds Watchpoint.Model services and registers the configuration-store context.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointModel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDocumentReader>(sp => DocumentReaderFactory.FromEmbeddedSchemas());
        services.TryAddSingleton<IValueValidator, ValueValidator>();
        services.TryAddSingleton<IStateSummaryEvaluator, StateSummaryEvaluator>();
        services.TryAddSingleton<ModelMetrics>();

        // Register configuration storage for Model module
        services.AddModuleStorage<ModelDbContext>(
            ModelDbContext.ModuleName,
            providers => providers.Add<ModelPostgresDbContext>("postgres"));

        // Register standard library loader
        services.AddSingleton(sp =>
        {
            var reader = sp.GetRequiredService<IDocumentReader>();
            var logger = sp.GetRequiredService<ILogger<TypeCatalog>>();
            var loadedRes = StandardLibraryLoader.Load(reader, logger);
            if (!loadedRes.TryGetValue(out var loaded, out var err))
            {
                throw new InvalidOperationException($"Failed to load standard library: {err.MessageKey}");
            }

            return loaded;
        });

        services.AddSingleton<IStandardLibrary>(sp => sp.GetRequiredService<LoadedLibrary>());

        // Register TypeCatalog and expose ITypeCatalog and ITypeResources
        services.AddSingleton<TypeCatalog>();
        services.AddSingleton<ITypeCatalog>(sp => sp.GetRequiredService<TypeCatalog>());
        services.AddSingleton<ITypeResources>(sp => sp.GetRequiredService<TypeCatalog>().Resources);

        return services;
    }
}
