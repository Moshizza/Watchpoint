using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Watchpoint.State.Internal;

/// <summary>
/// Runs the aggregate consistency check every <see cref="StateOptions.ConsistencyCheckInterval"/> (spec STA §6.4): a
/// safety net that must never find a drift, so a found-and-corrected one is logged at <see cref="LogLevel.Error"/>.
/// </summary>
internal sealed class AggregateConsistencyService(
    StateStore store,
    StateOptions options,
    TimeProvider time,
    ILogger<AggregateConsistencyService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await WaitAsync(stoppingToken).ConfigureAwait(false))
            {
                store.RunAggregateConsistencyCheck(logger);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task<bool> WaitAsync(CancellationToken ct)
    {
        await Task.Delay(options.ConsistencyCheckInterval, time, ct).ConfigureAwait(false);
        return true;
    }
}
