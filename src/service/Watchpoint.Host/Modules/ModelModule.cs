using Watchpoint.Hosting;
using Watchpoint.Model;

namespace Watchpoint.Host.Modules;

/// <summary>Model module (spec MOD §3, HST §5.2): the type catalogue the integration runtime registers package types with.</summary>
public sealed class ModelModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Model";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWatchpointModel();
    }
}
