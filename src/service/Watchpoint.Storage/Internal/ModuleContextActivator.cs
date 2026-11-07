using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

/// <summary>Creates a module's provider-specific context for the provider of the configuration store.</summary>
internal sealed class ModuleContextActivator(IStoreConnectionFactory connections, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Type, DbContextOptions> _options = new();

    public ConfigurationDbContextBase Create(IModuleStorageRegistration registration)
    {
        var provider = connections.GetProvider(StoreKind.Configuration);
        if (!registration.ProviderContexts.TryGetValue(provider.ProviderId, out var contextType))
        {
            throw new InvalidOperationException(
                $"Module {registration.Module} has no {provider.ProviderId} context: add it with AddModuleStorage(…, p => p.Add<…>(\"{provider.ProviderId}\")).");
        }

        var info = new StorageContextInfo(registration.Module, provider.Naming, clock);
        var options = _options.GetOrAdd(contextType, _ =>
        {
            var builder = new DbContextOptionsBuilder();
            provider.ConfigureDbContext(builder, connections.GetDatabase(StoreKind.Configuration), info.Naming.SchemaFor(registration.Module));
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            return builder.Options;
        });
        return (ConfigurationDbContextBase)Activator.CreateInstance(contextType, options, info)!;
    }
}

/// <summary><see cref="IDbContextFactory{TContext}"/> of a module: the provider-specific subclass of <typeparamref name="TContext"/>.</summary>
internal sealed class ModuleDbContextFactory<TContext>(ModuleContextActivator activator, ModuleStorageRegistration<TContext> registration)
    : IDbContextFactory<TContext>
    where TContext : ConfigurationDbContextBase
{
    public TContext CreateDbContext() => (TContext)activator.Create(registration);
}
