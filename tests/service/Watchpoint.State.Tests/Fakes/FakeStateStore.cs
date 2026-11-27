using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.State.Tests.Fakes;

/// <summary>An in-memory <see cref="IStateStore"/> that records every batch and can fail on demand.</summary>
internal sealed class FakeStateStore : IStateStore
{
    private readonly ConcurrentDictionary<(string Kind, Guid Key), StateItem> _items = new();
    private readonly ConcurrentDictionary<(string Consumer, int Partition), long> _checkpoints = new();

    /// <summary>Gets every batch written successfully, in order.</summary>
    public List<StateSnapshotBatch> Batches { get; } = [];

    /// <summary>Gets or sets the number of next writes that fail.</summary>
    public int FailNext { get; set; }

    public IReadOnlyCollection<StateItem> Items => [.. _items.Values];

    public long? CheckpointOf(string consumer, int partition) => _checkpoints.TryGetValue((consumer, partition), out var value) ? value : null;

    public Task WriteSnapshotAsync(StateSnapshotBatch batch, CancellationToken ct)
    {
        StateStoreRules.Validate(batch);
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("Simulated snapshot write failure.");
        }

        lock (Batches)
        {
            foreach (var item in batch.Upserts)
            {
                _items[(item.Kind, item.Key)] = item;
            }

            foreach (var key in batch.Deletes)
            {
                _items.TryRemove((key.Kind, key.Key), out _);
            }

            _checkpoints[(batch.Consumer, batch.Partition)] = batch.Sequence;
            Batches.Add(batch);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StateItem> ReadItemsAsync(string kind, IReadOnlyCollection<int> partitions, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        foreach (var item in _items.Values.Where(i => i.Kind == kind && partitions.Contains(i.Partition)).ToList())
        {
            yield return item;
        }
    }

    public Task<IReadOnlyDictionary<int, long>> ReadCheckpointsAsync(string consumer, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, long>>(_checkpoints.Where(kv => kv.Key.Consumer == consumer).ToDictionary(kv => kv.Key.Partition, kv => kv.Value));
}
