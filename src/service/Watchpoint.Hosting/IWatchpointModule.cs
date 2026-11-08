using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Watchpoint.Hosting;

/// <summary>
/// A module of the Watchpoint service.
/// </summary>
public interface IWatchpointModule
{
    /// <summary>Gets the unique module name (e.g. "Registry").</summary>
    string Name { get; }

    /// <summary>Configures the services provided by this module.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="context">The module initialization context.</param>
    void ConfigureServices(IServiceCollection services, ModuleContext context);

    /// <summary>Maps HTTP / gRPC endpoints for this module.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
