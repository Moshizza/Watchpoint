using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchpoint.Audit.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Writes signed checkpoints <c>(chainId, sequence, hash, at, signature)</c> every hour and at shutdown (spec AUD §6).</summary>
internal sealed class CheckpointService(IAuditStore store, IAuditSigner signer, IAuditSink sink, AuditSettings settings, TimeProvider clock, ILogger<CheckpointService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.CheckpointInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TryCreateAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await TryCreateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a checkpoint at the head of the chain, unless the head is already checkpointed.</summary>
    public async Task<AuditCheckpointRow?> CreateAsync(CancellationToken ct)
    {
        var head = await store.GetHeadAsync(settings.ChainId, ct).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        var existing = await store.GetCheckpointsAsync(settings.ChainId, ct).ConfigureAwait(false);
        if (existing.Count > 0 && existing[^1].Sequence >= head.Sequence)
        {
            return null;
        }

        var at = HashChain.Truncate(clock.GetUtcNow());
        var (keyId, signature) = await signer.SignAsync(HashChain.CheckpointPayload(settings.ChainId, head.Sequence, head.Hash, at), ct).ConfigureAwait(false);
        var checkpoint = new AuditCheckpointRow(settings.ChainId, head.Sequence, head.Hash, at, signature, keyId);
        await store.AddCheckpointAsync(checkpoint, ct).ConfigureAwait(false);
        AuditLog.CheckpointCreated(logger, checkpoint.ChainId, checkpoint.Sequence);
        await sink.WriteAsync(
            new AuditRecord(new AuditAction("audit.checkpointCreated"), AuditOutcome.Success, AuditActor.System("audit"), [], null, null, AuditSeverity.Normal, null, at),
            ct).ConfigureAwait(false);
        return checkpoint;
    }

    private async Task TryCreateAsync(CancellationToken ct)
    {
        try
        {
            await CreateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AuditLog.UpkeepFailed(logger, ex.GetType().Name);
        }
    }
}
