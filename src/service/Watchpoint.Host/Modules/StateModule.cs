using Watchpoint.Hosting;
using Watchpoint.State;
using Watchpoint.State.Contracts;

namespace Watchpoint.Host.Modules;

/// <summary>
/// State module (spec STA §3, HST §5.2): the live state store, overall state computation, aggregates, queries and
/// change feed. Depends on the registry (entity membership) and the pipeline (partition subscriber, event log
/// records); registers no settings validation of its own beyond the <c>State</c> section's own range checks.
/// </summary>
public sealed class StateModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "State";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">A <c>State</c> setting is out of range (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            services.AddWatchpointState(context.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapStateEndpoints();
}
