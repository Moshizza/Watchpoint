using Watchpoint.Hosting;
using Watchpoint.Identity.Contracts;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Identity module definition for host composition (spec IDN §3, HST §5.2).
/// Registers identity services and the bootstrap admin startup task.
/// </summary>
public sealed class IdentityModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Identity";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddIdentityModule();
        services.AddStartupTask<BootstrapAdminStartupTask>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // HTTP API endpoints are mapped by the API module (task T1.19 / spec IDN §7.2)
    }
}

/// <summary>
/// Startup task creating the initial administrator account during StartupPhase.Configuration (spec IDN §7.1).
/// </summary>
public sealed class BootstrapAdminStartupTask(IBootstrapAdminService bootstrapAdmin, NodeSettings settings) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Configuration;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public string Name => "BootstrapAdmin";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct)
    {
        var isProduction = settings.Node.Environment == NodeEnvironment.Production;
        return bootstrapAdmin.ExecuteAsync(isProduction, ct);
    }
}
