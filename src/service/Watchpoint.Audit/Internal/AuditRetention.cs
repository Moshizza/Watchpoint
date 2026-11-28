using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchpoint.Audit.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>
/// Purges whole segments up to a checkpoint (spec AUD §6.1, §8): the checkpoint becomes the new genesis, the purge is audited
/// as <c>audit.retentionPurged</c>, and verification continues from the purge checkpoint.
/// </summary>
internal sealed class AuditRetention(IAuditStore store, IAuditSink sink, AuditSettings settings, TimeProvider clock, ILogger<AuditRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PurgeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AuditLog.UpkeepFailed(logger, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>Purges every chain up to its newest checkpoint older than the retention period.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of records purged.</returns>
    public async Task<long> PurgeAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(settings.RetentionDays);
        long total = 0;
        foreach (var chain in await store.ListChainsAsync(ct).ConfigureAwait(false))
        {
            var checkpoint = (await store.GetCheckpointsAsync(chain, ct).ConfigureAwait(false)).LastOrDefault(c => c.At <= cutoff);
            if (checkpoint is null)
            {
                continue;
            }

            var deleted = await store.PurgeUpToAsync(chain, checkpoint.Sequence, ct).ConfigureAwait(false);
            if (deleted == 0)
            {
                continue;
            }

            total += deleted;
            AuditLog.RetentionPurged(logger, deleted, checkpoint.Sequence);
            await sink.WriteAsync(
                new AuditRecord(
                    new AuditAction("audit.retentionPurged"), AuditOutcome.Success, AuditActor.System("audit"), [], null,
                    JsonSerializer.SerializeToElement(new { chainId = chain, sequence = checkpoint.Sequence, hash = Convert.ToHexStringLower(checkpoint.Hash), count = deleted }),
                    AuditSeverity.Sensitive),
                ct).ConfigureAwait(false);
        }

        return total;
    }
}
