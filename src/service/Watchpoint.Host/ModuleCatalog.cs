using Watchpoint.Hosting;

namespace Watchpoint.Host;

/// <summary>
/// Static catalogue of compiled modules in the product (spec HST §5.2).
/// </summary>
public static class ModuleCatalog
{
    /// <summary>
    /// Static, ordered list of modules compiled into the host.
    /// In M1 / T1.01, other modules are stubbed or added in their respective tasks.
    /// </summary>
    public static readonly IWatchpointModule[] All =
    [
        // Other modules will be added as they are implemented:
        // new KernelModule(), new SecretsModule(), new StorageModule(), new ModelModule(),
        // new RegistryModule(), new PipelineModule(), new StateModule(), new IdentityModule(),
        // new IntegrationRuntimeModule(), new ApiModule(),
    ];
}
