using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// Writes the snapshots of the state module and of the snapshot participants (spec STA §8.1). For each partition, a
/// job posted to the partition worker collects the dirty set and the participants' changes together with the
/// partition sequence; the writes then run here, off the worker, in batches of at most
/// <see cref="IStateStore.MaxBatchItems"/> items where only the last batch advances the checkpoint (the others carry
/// the checkpoint already persisted). A failed write puts the unwritten part back (log 1720) and is retried on the
/// next interval.
/// </summary>
internal sealed class Snapshotter : IAsyncDisposable
{
    private readonly StateStore _store;
    private readonly IPartitionWork _work;
    private readonly IStateStore _persistence;
    private readonly IReadOnlyList<ISnapshotParticipant> _participants;
    private readonly StateOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long[] _checkpoints = new long[Partitioning.PartitionCount];
    private readonly Dictionary<(int Participant, int Partition), Dictionary<(string Kind, Guid Key), StateItem?>> _pendingParticipant = [];
    private readonly Dictionary<(int Participant, int Partition), long> _participantCheckpoints = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public Snapshotter(
        StateStore store,
        IPartitionWork work,
        IStateStore persistence,
        IEnumerable<ISnapshotParticipant> participants,
        StateOptions options,
        TimeProvider time,
        ILogger<Snapshotter> logger)
    {
        _store = store;
        _work = work;
        _persistence = persistence;
        _participants = [.. participants];
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the snapshot participants, in registration order.</summary>
    public IReadOnlyList<ISnapshotParticipant> Participants => _participants;

    /// <summary>Gets the sequences between applied and snapshotted, summed over the partitions (<c>watchpoint.state.snapshot.lag</c>).</summary>
    public long Lag
    {
        get
        {
            var lag = 0L;
            for (var p = 0; p < _checkpoints.Length; p++)
            {
                lag += Math.Max(0, _store.PartitionStore(p).Sequence - Volatile.Read(ref _checkpoints[p]));
            }

            return lag;
        }
    }

    /// <summary>Gets the last checkpoint written (or restored) for a partition.</summary>
    public long Checkpoint(int partition) => Volatile.Read(ref _checkpoints[partition]);

    /// <summary>Sets the checkpoint restored for a partition (recovery).</summary>
    public void SetCheckpoint(int partition, long sequence) => Volatile.Write(ref _checkpoints[partition], sequence);

    /// <summary>Sets the checkpoint restored for a participant and partition (recovery).</summary>
    public void SetParticipantCheckpoint(int participant, int partition, long sequence) => _participantCheckpoints[(participant, partition)] = sequence;

    /// <summary>Starts the periodic snapshots every <c>State:SnapshotInterval</c>.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loop = RunAsync(_loopCts.Token);
    }

    /// <summary>Stops the periodic snapshots, then writes a final snapshot collected directly (the workers have stopped).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await StopLoopAsync().ConfigureAwait(false);
        await SnapshotAsync(onWorkers: false, ct).ConfigureAwait(false);
    }

    /// <summary>Writes one snapshot of every partition.</summary>
    /// <param name="onWorkers">
    /// <see langword="true"/> to collect on the partition workers (while the pipeline runs);
    /// <see langword="false"/> to collect directly, only while no worker runs (shutdown, tests).
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when every write succeeded.</returns>
    public async Task<bool> SnapshotAsync(bool onWorkers, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ok = true;
            for (var p = 0; p < Partitioning.PartitionCount; p++)
            {
                ok &= await SnapshotPartitionAsync(p, onWorkers, ct).ConfigureAwait(false);
            }

            return ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopLoopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StopLoopAsync()
    {
        if (_loop is null)
        {
            return;
        }

        await _loopCts!.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _loopCts.Dispose();
        _loopCts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.SnapshotInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await SnapshotAsync(onWorkers: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A failed interval (the pipeline stopping, an unexpected error) never ends the loop: the next interval retries.
                StateLog.SnapshotWriteFailed(_logger, -1, ex);
            }
        }
    }

    private async Task<bool> SnapshotPartitionAsync(int partition, bool onWorkers, CancellationToken ct)
    {
        PartitionSnapshot? snapshot = null;
        SnapshotContribution[] contributions = new SnapshotContribution[_participants.Count];
        void Collect()
        {
            snapshot = _store.CollectSnapshot(partition);
            for (var i = 0; i < _participants.Count; i++)
            {
                contributions[i] = _participants[i].Collect(partition);
            }
        }

        await RunAsync(partition, onWorkers, Collect, ct).ConfigureAwait(false);
        var ok = await WriteStateAsync(snapshot!, onWorkers, ct).ConfigureAwait(false);
        for (var i = 0; i < _participants.Count; i++)
        {
            ok &= await WriteParticipantAsync(i, partition, snapshot!.Sequence, contributions[i], ct).ConfigureAwait(false);
        }

        return ok;
    }

    private async Task<bool> WriteStateAsync(PartitionSnapshot snapshot, bool onWorkers, CancellationToken ct)
    {
        var partition = snapshot.Partition;
        var previous = Checkpoint(partition);
        if (snapshot.Upserts.Count == 0 && snapshot.Deletes.Count == 0 && snapshot.Sequence == previous)
        {
            return true;
        }

        var batches = Split([.. snapshot.Upserts], [.. snapshot.Deletes.Select(id => new StateItemKey(StateSerializer.Kind, id))]);
        for (var b = 0; b < batches.Count; b++)
        {
            var (upserts, deletes) = batches[b];
            var last = b == batches.Count - 1;
            var batch = new StateSnapshotBatch(StateSerializer.Consumer, partition, last ? snapshot.Sequence : previous, upserts, deletes);
            try
            {
                await _persistence.WriteSnapshotAsync(batch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StateLog.SnapshotWriteFailed(_logger, partition, ex);
                var changed = batches.Skip(b).SelectMany(x => x.Upserts).Select(i => i.Key).ToList();
                var deleted = batches.Skip(b).SelectMany(x => x.Deletes).Select(k => k.Key).ToList();
                await RunAsync(partition, onWorkers, () => _store.PartitionStore(partition).MergeBackDirty(changed, deleted), CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }

        SetCheckpoint(partition, snapshot.Sequence);
        StateLog.SnapshotWritten(_logger, partition, snapshot.Upserts.Count, snapshot.Sequence);
        return true;
    }

    private async Task<bool> WriteParticipantAsync(int index, int partition, long sequence, SnapshotContribution contribution, CancellationToken ct)
    {
        var participant = _participants[index];
        var key = (index, partition);
        var merged = _pendingParticipant.Remove(key, out var pending) ? pending : [];
        foreach (var item in contribution.Upserts)
        {
            merged[(item.Kind, item.Key)] = item;
        }

        foreach (var delete in contribution.Deletes)
        {
            merged[(delete.Kind, delete.Key)] = null;
        }

        var upserts = merged.Values.Where(v => v is not null).Select(v => v!).ToList();
        var deletes = merged.Where(kv => kv.Value is null).Select(kv => new StateItemKey(kv.Key.Kind, kv.Key.Key)).ToList();
        if (upserts.Count == 0 && deletes.Count == 0 && _participantCheckpoints.GetValueOrDefault(key) == sequence)
        {
            return true;
        }

        var batches = Split(upserts, deletes);
        for (var b = 0; b < batches.Count; b++)
        {
            var (u, d) = batches[b];
            try
            {
                await _persistence.WriteSnapshotAsync(new StateSnapshotBatch(participant.Consumer, partition, sequence, u, d), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StateLog.SnapshotWriteFailed(_logger, partition, ex);
                var rest = new Dictionary<(string Kind, Guid Key), StateItem?>();
                foreach (var (uu, dd) in batches.Skip(b))
                {
                    foreach (var item in uu)
                    {
                        rest[(item.Kind, item.Key)] = item;
                    }

                    foreach (var k in dd)
                    {
                        rest[(k.Kind, k.Key)] = null;
                    }
                }

                _pendingParticipant[key] = rest;
                return false;
            }
        }

        _participantCheckpoints[key] = sequence;
        return true;
    }

    private static List<(IReadOnlyList<StateItem> Upserts, IReadOnlyList<StateItemKey> Deletes)> Split(List<StateItem> upserts, List<StateItemKey> deletes)
    {
        var result = new List<(IReadOnlyList<StateItem>, IReadOnlyList<StateItemKey>)>();
        var u = 0;
        var d = 0;
        do
        {
            var take = Math.Min(IStateStore.MaxBatchItems, upserts.Count - u);
            var batchUpserts = upserts.Skip(u).Take(take).ToList();
            u += take;
            var takeDeletes = Math.Min(IStateStore.MaxBatchItems - take, deletes.Count - d);
            var batchDeletes = deletes.Skip(d).Take(takeDeletes).ToList();
            d += takeDeletes;
            result.Add((batchUpserts, batchDeletes));
        }
        while (u < upserts.Count || d < deletes.Count);

        return result;
    }

    private Task RunAsync(int partition, bool onWorkers, Action work, CancellationToken ct)
    {
        if (onWorkers)
        {
            return _work.RunAsync(partition, work, ct);
        }

        work();
        return Task.CompletedTask;
    }
}
