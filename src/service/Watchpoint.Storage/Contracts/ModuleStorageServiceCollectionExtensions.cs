using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Storage.Internal;

namespace Watchpoint.Storage.Contracts;

/// <summary>Registration of a module's configuration-store context (spec STO §6.2).</summary>
public static class ModuleStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the module's context: <see cref="IDbContextFactory{TContext}"/> creates the subclass registered for the configured
    /// provider; the migration runner migrates it in registration order (or <paramref name="order"/>).
    /// </summary>
    /// <typeparam name="TContext">The provider-neutral context.</typeparam>
    /// <param name="services">The services.</param>
    /// <param name="module">Module name, e.g. <c>Registry</c> (schema <c>wp_registry</c>).</param>
    /// <param name="providers">Adds one context per provider.</param>
    /// <param name="order">Migration order; default: registration order.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddModuleStorage<TContext>(this IServiceCollection services, string module,
        Action<ModuleStorageProviders<TContext>> providers, int? order = null)
        where TContext : ConfigurationDbContextBase
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(providers);
        StorageNaming.Schemas.SchemaFor(module); // validates the name
        var builder = new ModuleStorageProviders<TContext>();
        providers(builder);
        var existing = services.Count(d => d.ServiceType == typeof(IModuleStorageRegistration));
        var registration = new ModuleStorageRegistration<TContext>(module, builder.Contexts, order ?? existing);
        services.AddSingleton(registration);
        services.AddSingleton<IModuleStorageRegistration>(registration);
        services.TryAddSingleton<IDbContextFactory<TContext>, ModuleDbContextFactory<TContext>>();
        return services;
    }
}

/// <summary>The provider-specific contexts of a module.</summary>
/// <typeparam name="TContext">The provider-neutral context.</typeparam>
public sealed class ModuleStorageProviders<TContext>
    where TContext : ConfigurationDbContextBase
{
    internal Dictionary<string, Type> Contexts { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds the context owning the migrations for a provider.</summary>
    /// <typeparam name="TProviderContext">The subclass, with a constructor (<see cref="DbContextOptions"/>, <see cref="StorageContextInfo"/>).</typeparam>
    /// <param name="providerId">The provider id, e.g. <c>postgres</c>.</param>
    /// <returns>This builder.</returns>
    public ModuleStorageProviders<TContext> Add<TProviderContext>(string providerId)
        where TProviderContext : TContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        Contexts[providerId] = typeof(TProviderContext);
        return this;
    }
}
