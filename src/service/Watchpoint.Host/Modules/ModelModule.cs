using Watchpoint.Hosting;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;

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

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapModelEndpoints();
}
