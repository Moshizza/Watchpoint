using Microsoft.Extensions.Hosting;

namespace Watchpoint.Api.Internal.Idempotency;

/// <summary>Purges idempotency records older than 24 h, hourly (spec API §5.5).</summary>
internal sealed class IdempotencyPurgeService(IIdempotencyStore store, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public Task<int> PurgeOnceAsync(CancellationToken ct) => store.PurgeAsync(time.GetUtcNow() - Retention, ct).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PurgeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
