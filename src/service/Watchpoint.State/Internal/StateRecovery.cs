using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Paging;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// The recovery of the state module (spec STA §8.2) and its snapshot lifecycle (<see cref="IStateLifecycle"/>).
/// <see cref="RestoreAsync"/> (start-up task <c>StateRestore</c>, before the pipeline recovery) rebuilds the
/// partition stores from the snapshot and the registry and restores the snapshot participants; the pipeline then
/// replays the log after the checkpoints (<see cref="StateSubscriber.GetRecoveryCheckpoint"/>); when every partition
/// completed (<see cref="OnPartitionRecovered"/>) the derived fields are recomputed and the module is ready.
/// </summary>
internal sealed class StateRecovery : IStateLifecycle
{
    private readonly StateStore _store;
    private readonly Snapshotter _snapshotter;
    private readonly IStateStore _persistence;
    private readonly IRegistryReader _registry;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly bool[] _completed = new bool[Partitioning.PartitionCount];
    private int _completedCount;
    private long _replayed;
    private int _restoredEntities;
    private long _started;
    private volatile bool _restored;
    private volatile bool _recovered;

    public StateRecovery(StateStore store, Snapshotter snapshotter, IStateStore persistence, IRegistryReader registry, TimeProvider time, ILogger<StateRecovery> logger)
    {
        _store = store;
        _snapshotter = snapshotter;
        _persistence = persistence;
        _registry = registry;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Recovered => _recovered;

    /// <summary>Gets a value indicating whether <see cref="RestoreAsync"/> ran.</summary>
    public bool Restored => _restored;

    /// <inheritdoc />
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (_restored)
        {
            return;
        }

        _started = _time.GetTimestamp();
        var partitions = Enumerable.Range(0, Partitioning.PartitionCount).ToArray();
        var checkpoints = await _persistence.ReadCheckpointsAsync(StateSerializer.Consumer, ct).ConfigureAwait(false);
        var persisted = new Dictionary<Guid, PersistedEntity>();
        await foreach (var item in _persistence.ReadItemsAsync(StateSerializer.Kind, partitions, ct).ConfigureAwait(false))
        {
            try
            {
                persisted[item.Key] = StateSerializer.Deserialize(item.PayloadJson.Span);
            }
            catch (JsonException)
            {
                // An unreadable item is treated as missing: the entity is initialised as new and the replay fills it.
            }
        }

        // Servers first, so their state exists when the devices are restored.
        var now = _time.GetUtcNow();
        var registered = new HashSet<Guid>();
        foreach (var info in RegisteredEntities())
        {
            if (!registered.Add(info.Id))
            {
                continue;
            }

            _store.RestoreEntity(info, persisted.GetValueOrDefault(info.Id), now);
            if (persisted.ContainsKey(info.Id))
            {
                _restoredEntities++;
            }
        }

        foreach (var partition in partitions)
        {
            var store = _store.PartitionStore(partition);
            store.ClearDirty();
            var checkpoint = checkpoints.GetValueOrDefault(partition);
            store.Sequence = Math.Max(store.Sequence, checkpoint);
            _snapshotter.SetCheckpoint(partition, checkpoint);
        }

        // Entities no longer in the registry: discarded from the state and deleted from the snapshot.
        foreach (var id in persisted.Keys.Where(id => !registered.Contains(id)))
        {
            _store.PartitionStore(Partitioning.Of(id)).MarkDeleted([id]);
        }

        for (var i = 0; i < _snapshotter.Participants.Count; i++)
        {
            await RestoreParticipantAsync(i, _snapshotter.Participants[i], partitions, ct).ConfigureAwait(false);
        }

        _restored = true;
    }

    /// <inheritdoc />
    public void StartSnapshots() => _snapshotter.Start();

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct) => _snapshotter.StopAsync(ct);

    /// <summary>The checkpoint the state module reports to the pipeline for a partition (spec STA §8.2 step 2).</summary>
    public long GetCheckpoint(int partition) => _snapshotter.Checkpoint(partition);

    /// <summary>Counts records replayed during the recovery (log 1701).</summary>
    public void CountReplayed(int records) => Interlocked.Add(ref _replayed, records);

    /// <summary>
    /// A partition completed its replay (spec STA §8.2 step 3): <see cref="DeviceState.ServerUnavailable"/> is
    /// recomputed for its devices (the pipeline completes partitions only after every replay, so the server states of
    /// the other partitions are final); after the last partition the aggregates are checked from scratch and the
    /// module is ready (step 5, log 1701).
    /// </summary>
    public void OnPartitionRecovered(int partition)
    {
        _store.RecomputeServerUnavailable(partition, _time.GetUtcNow());
        if (_completed[partition])
        {
            return;
        }

        _completed[partition] = true;
        if (Interlocked.Increment(ref _completedCount) != Partitioning.PartitionCount)
        {
            return;
        }

        _store.RunAggregateConsistencyCheck(_logger);
        var duration = (long)_time.GetElapsedTime(_started).TotalMilliseconds;
        var replayed = Interlocked.Read(ref _replayed);
        StateLog.StateRecovered(_logger, _restoredEntities, replayed, duration);
        _recovered = true;
    }

    private IEnumerable<EntityInfo> RegisteredEntities()
    {
        string? cursor = null;
        do
        {
            var page = _registry.QueryServers(new ServerQuery(), new PageRequest(PageRequest.MaxLimit, cursor));
            foreach (var server in page.Items)
            {
                yield return EntityInfo.Of(server);
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        do
        {
            var page = _registry.QueryDevices(new DeviceQuery(), new PageRequest(PageRequest.MaxLimit, cursor));
            foreach (var device in page.Items)
            {
                yield return EntityInfo.Of(device);
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    private async Task RestoreParticipantAsync(int index, ISnapshotParticipant participant, int[] partitions, CancellationToken ct)
    {
        var checkpoints = await _persistence.ReadCheckpointsAsync(participant.Consumer, ct).ConfigureAwait(false);
        var byPartition = new Dictionary<int, List<StateItem>>();
        await foreach (var item in _persistence.ReadItemsAsync(participant.Kind, partitions, ct).ConfigureAwait(false))
        {
            (byPartition.TryGetValue(item.Partition, out var list) ? list : byPartition[item.Partition] = []).Add(item);
        }

        foreach (var partition in partitions)
        {
            participant.Restore(partition, byPartition.TryGetValue(partition, out var items) ? items : []);
            var checkpoint = checkpoints.GetValueOrDefault(partition);
            participant.RestoreCheckpoint(partition, checkpoint);
            _snapshotter.SetParticipantCheckpoint(index, partition, checkpoint);
        }
    }
}
