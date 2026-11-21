using Watchpoint.Commands;
using Watchpoint.Hosting;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Commands module (spec CMD §3, HST §5.2): command service, its partition subscriber (order 200) and the fallback
/// command authorizer until RGT. Dispatch to connections arrives with T2.02.
/// </summary>
public sealed class CommandsModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Commands";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">A <c>Commands</c> setting is invalid (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            services.AddWatchpointCommands(context.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }
    }
}
