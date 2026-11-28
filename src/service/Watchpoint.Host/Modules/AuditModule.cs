using Watchpoint.Audit;
using Watchpoint.Audit.Contracts;
using Watchpoint.Hosting;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Audit module (spec AUD, HST §5.2): the hash-chained audit trail. It replaces the log-only audit sinks of the other modules,
/// verifies the chain in the background and serves the <c>/api/v1/audit</c> endpoints.
/// </summary>
public sealed class AuditModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Audit";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">An <c>Audit</c> setting is out of range (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            services.AddWatchpointAudit(context.Configuration, context.Node.Node.Id.ToString("D"), context.Paths.Data);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapAuditEndpoints();
}
