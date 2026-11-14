using Watchpoint.Hosting;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Identity module definition for host composition (spec IDN §3, HST §5.2).
/// Binds and validates the <c>Identity:*</c> settings (IDN §7.1), derives the token issuer from <c>Site:Id</c> (IDN §6.2),
/// registers identity services and the bootstrap admin startup task.
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

        services.AddIdentityModule(BindSettings(context));
        services.AddStartupTask<BootstrapAdminStartupTask>();
    }

    /// <summary>
    /// Binds the <c>Identity:*</c> settings for the node's site. Invalid settings throw <see cref="InvalidSettingsException"/>,
    /// which stops the start (exit code 78) rather than running with defaults the operator did not choose.
    /// </summary>
    /// <param name="context">The module context.</param>
    /// <returns>The validated settings.</returns>
    /// <exception cref="InvalidSettingsException">A setting is invalid, unknown or the site id is missing.</exception>
    public static IdentitySettings BindSettings(ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Node.Site.Id == Guid.Empty || !NodeSettings.IsValidV7(context.Node.Site.Id))
        {
            throw new InvalidSettingsException("Identity", "Site:Id must be a valid UUIDv7 (generated at first start)");
        }

        var bound = IdentitySettings.Bind(context.Configuration, SiteId.From(context.Node.Site.Id));
        if (!bound.TryGetValue(out var settings, out var error))
        {
            throw new InvalidSettingsException("Identity", IdentitySettings.Describe(error));
        }

        return settings;
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
