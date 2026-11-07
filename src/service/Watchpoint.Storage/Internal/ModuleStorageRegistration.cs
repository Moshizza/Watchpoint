using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

internal sealed record ModuleStorageRegistration<TContext>(string Module, IReadOnlyDictionary<string, Type> ProviderContexts, int Order)
    : IModuleStorageRegistration
    where TContext : ConfigurationDbContextBase
{
    public Type DbContextType => typeof(TContext);
}
