using System.Runtime.CompilerServices;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Tests;

/// <summary>A minimal state store for unit tests of the storage module (no provider).</summary>
internal sealed class MemoryStateStore : IStateStore
{
    private readonly Dictionary<(string, Guid), StateItem> _items = [];
    private readonly Dictionary<(string, int), long> _checkpoints = [];

    public Task WriteSnapshotAsync(StateSnapshotBatch batch, CancellationToken ct)
    {
        foreach (var item in batch.Upserts)
        {
            _items[(item.Kind, item.Key)] = item;
        }

        foreach (var key in batch.Deletes)
        {
            _items.Remove((key.Kind, key.Key));
        }

        _checkpoints[(batch.Consumer, batch.Partition)] = batch.Sequence;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StateItem> ReadItemsAsync(string kind, IReadOnlyCollection<int> partitions, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var item in _items.Values.Where(i => i.Kind == kind && partitions.Contains(i.Partition)))
        {
            yield return item;
        }
    }

    public Task<IReadOnlyDictionary<int, long>> ReadCheckpointsAsync(string consumer, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, long>>(_checkpoints.Where(c => c.Key.Item1 == consumer).ToDictionary(c => c.Key.Item2, c => c.Value));
}
