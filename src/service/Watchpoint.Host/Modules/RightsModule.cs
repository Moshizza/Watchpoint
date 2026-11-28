using Watchpoint.Hosting;

namespace Watchpoint.Host.Modules;

/// <summary>Rights module (spec RGT, HST §5.2): permission catalogue, grants, evaluator; replaces the M1–M2 permission and command fallbacks.</summary>
public sealed class RightsModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Rights";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        Watchpoint.Rights.RightsModule.AddWatchpointRights(services);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => Watchpoint.Rights.RightsEndpoints.MapRightsEndpoints(endpoints);
}
