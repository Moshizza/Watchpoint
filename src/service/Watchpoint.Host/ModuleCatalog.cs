using Watchpoint.Host.Modules;
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
        new StorageModule(),
        new ModelModule(),
        new IdentityModule(),
        new IntegrationRuntimeModule(),

        // Other modules are added by their tasks, in this order (HST §5.2):
        // new KernelModule(), new SecretsModule(), [StorageModule], [ModelModule],
        // new RegistryModule(), new PipelineModule(), new StateModule(), [IdentityModule],
        // [IntegrationRuntimeModule], new ApiModule(),
    ];
}
