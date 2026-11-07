using Microsoft.Extensions.Logging;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

/// <summary>Validates batches and records snapshot metrics and log 1220 around the provider's state store.</summary>
internal sealed class InstrumentedStateStore(IStateStore inner, StorageMetrics metrics, TimeProvider clock, ILogger<InstrumentedStateStore> logger)
    : IStateStore
{
    public async Task WriteSnapshotAsync(StateSnapshotBatch batch, CancellationToken ct)
    {
        StateStoreRules.Validate(batch);
        var start = clock.GetTimestamp();
        await inner.WriteSnapshotAsync(batch, ct).ConfigureAwait(false);
        metrics.RecordSnapshot(batch.Consumer, clock.GetElapsedTime(start), batch.Upserts.Count + batch.Deletes.Count);
        StorageLog.SnapshotWritten(logger, batch.Consumer, batch.Partition, batch.Upserts.Count, batch.Deletes.Count, batch.Sequence);
    }

    public IAsyncEnumerable<StateItem> ReadItemsAsync(string kind, IReadOnlyCollection<int> partitions, CancellationToken ct)
    {
        StateStoreRules.ValidateName(kind, nameof(kind));
        return inner.ReadItemsAsync(kind, partitions, ct);
    }

    public Task<IReadOnlyDictionary<int, long>> ReadCheckpointsAsync(string consumer, CancellationToken ct)
    {
        StateStoreRules.ValidateName(consumer, nameof(consumer));
        return inner.ReadCheckpointsAsync(consumer, ct);
    }
}
