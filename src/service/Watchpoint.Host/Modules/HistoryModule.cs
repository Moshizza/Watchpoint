using Watchpoint.History;
using Watchpoint.Hosting;

namespace Watchpoint.Host.Modules;

/// <summary>
/// History module (spec HIS §3, HST §5.2): writes events, alarms, commands and property changes from the event log into the
/// history store, and answers history queries. It runs as hosted services from the start of the host; a database or log that is
/// not ready yet is retried.
/// </summary>
public sealed class HistoryModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "History";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">A <c>History</c> setting is out of range (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            services.AddWatchpointHistory(context.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }
    }
}
