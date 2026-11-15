using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Ingest;
using Watchpoint.Pipeline.Internal.SideEffects;

namespace Watchpoint.Pipeline.Internal.Partitions;

/// <summary>
/// Owns the 64 partition workers (spec PIP §7.3, §7.4): routes validated observations and posted work to them, runs the
/// recovery handshake and starts and stops them. In M1 one node owns all partitions.
/// </summary>
internal sealed class PartitionRouter : IPartitionDispatcher, IPipelineLifecycle, IAsyncDisposable
{
    private const int ReplayBatch = 500;

    private readonly PartitionWorker[] _workers = new PartitionWorker[Partitioning.PartitionCount];
    private readonly PartitionServices _services;
    private readonly IReadOnlyList<ILastValueSource> _lastValueSources;
    private readonly SideEffectQueue _sideEffects;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _recovered;
    private bool _stopped;
    private volatile bool _running;

    public PartitionRouter(
        IngestOptions options,
        PipelineOptions logOptions,
        IEventLogWriter writer,
        IEventLogReader reader,
        ILogRecordTypeRegistry registry,
        IEnumerable<IPartitionSubscriber> subscribers,
        IEnumerable<ILastValueSource> lastValueSources,
        SideEffectQueue sideEffects,
        TimeProvider timeProvider,
        PipelineMetrics metrics,
        ILogger<PartitionRouter> logger)
    {
        // Ascending order; registration order among equals (OrderBy is stable).
        var ordered = subscribers.OrderBy(s => s.Order).ToList();
        _services = new PartitionServices(options, logOptions, writer, reader, registry, ordered, sideEffects, timeProvider, metrics, logger);
        _lastValueSources = lastValueSources.ToList();
        _sideEffects = sideEffects;
        for (var p = 0; p < _workers.Length; p++)
        {
            _workers[p] = new PartitionWorker(p, _services);
        }

        metrics.ObservePartitionQueues(() => _workers.Select(w => new Measurement<int>(w.Queued, new KeyValuePair<string, object?>("partition", w.Partition))));
    }

    public bool Running => _running;

    internal PartitionWorker Worker(int partition) => _workers[partition];

    /// <summary>
    /// Hands an observation to its partition; waits while the partition's queue is full (back-pressure, spec PIP §8).
    /// After the pipeline stopped the observation is rejected.
    /// </summary>
    public async ValueTask RouteAsync(IngestItem item, CancellationToken ct)
    {
        var input = _workers[item.Partition].Input;
        if (input.TryWrite(item))
        {
            return;
        }

        try
        {
            await input.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            item.Reject(PipelineErrors.LogUnavailable("The pipeline is stopping."));
        }
    }

    public async ValueTask PostAsync(int partition, Func<PartitionContext, ValueTask> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await InvokeAsync(
            partition,
            async context =>
            {
                await work(context).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    public async ValueTask<T> InvokeAsync<T>(int partition, Func<PartitionContext, ValueTask<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegative(partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, Partitioning.PartitionCount);
        var item = new ControlItem<T>(work);
        try
        {
            await _workers[partition].Control.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new PipelineException(PipelineErrors.LogUnavailable("The pipeline is stopping."));
        }

        return await item.Completion.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            await RecoverCoreAsync(ct).ConfigureAwait(false);
            _sideEffects.Start();
            foreach (var worker in _workers)
            {
                worker.Start();
            }

            _running = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _running = false;
            foreach (var worker in _workers)
            {
                worker.CompleteInput();
            }

            // Graceful drain, bounded: the workers finish what is queued, then they are stopped.
            var all = Task.WhenAll(_workers.Select(w => w.Completion));
            try
            {
                await all.WaitAsync(_services.Options.DrainTimeout, _services.Time, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Not waited for: a worker held by a subscriber that never returns must not hold the shutdown.
                foreach (var worker in _workers)
                {
                    worker.Abort();
                }
            }

            var backlog = 0;
            foreach (var worker in _workers)
            {
                backlog += worker.RejectBacklog();
            }

            if (backlog > 0)
            {
                PipelineLog.StoppedWithBacklog(_services.Logger, backlog);
            }

            await _sideEffects.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
        foreach (var worker in _workers)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        _lifecycle.Dispose();
    }

    /// <summary>The recovery handshake (spec PIP §7.4). Runs once, before any worker starts.</summary>
    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        if (_recovered)
        {
            return;
        }

        var started = _services.Time.GetTimestamp();
        var subscribers = _services.Subscribers;

        // 1–2. Subscribers restore their snapshots and report their checkpoints.
        var last = await _services.Reader.GetLastSequenceAsync(ct).ConfigureAwait(false);
        var checkpoints = new long?[_workers.Length][];
        var starts = new long[_workers.Length];
        var minStart = long.MaxValue;
        for (var p = 0; p < _workers.Length; p++)
        {
            _workers[p].BeginRecovery();
            checkpoints[p] = new long?[subscribers.Count];
            var start = last + 1;
            for (var i = 0; i < subscribers.Count; i++)
            {
                var checkpoint = subscribers[i].GetRecoveryCheckpoint(p);
                checkpoints[p][i] = checkpoint;
                if (checkpoint is { } value)
                {
                    start = Math.Min(start, Math.Max(value, 0) + 1);
                }
            }

            starts[p] = start;
            minStart = Math.Min(minStart, start);
        }

        // 3. One replay for all partitions that have something to replay, from the smallest start.
        var replayed = new long[_workers.Length];
        if (minStart <= last)
        {
            var first = await _services.Reader.GetFirstAvailableSequenceAsync(ct).ConfigureAwait(false);
            var partitions = new HashSet<int>();
            for (var p = 0; p < _workers.Length; p++)
            {
                if (starts[p] > last)
                {
                    continue;
                }

                partitions.Add(p);
                if (starts[p] < first)
                {
                    PipelineLog.ReplayAfterRetention(_services.Logger, p, starts[p], first);
                    starts[p] = first;
                }
            }

            var pending = new List<LogRecord>?[_workers.Length];
            await foreach (var record in _services.Reader.ReadPartitionsAsync(partitions, Math.Max(minStart, first), ct).ConfigureAwait(false))
            {
                var p = record.Envelope.P;
                if (p < 0 || p >= _workers.Length || record.Sequence < starts[p] || record.Sequence > last)
                {
                    continue;
                }

                var batch = pending[p] ??= new List<LogRecord>(ReplayBatch);
                batch.Add(record);
                replayed[p]++;
                if (batch.Count >= ReplayBatch)
                {
                    _workers[p].Replay(batch, checkpoints[p]);
                    pending[p] = null;
                }
            }

            for (var p = 0; p < _workers.Length; p++)
            {
                if (pending[p] is { Count: > 0 } batch)
                {
                    _workers[p].Replay(batch, checkpoints[p]);
                }
            }
        }

        // 4–5. Current values for the change detection, then the subscribers are told and the workers may start.
        var elapsed = (long)_services.Time.GetElapsedTime(started).TotalMilliseconds;
        for (var p = 0; p < _workers.Length; p++)
        {
            await _workers[p].CompleteRecoveryAsync(last, _lastValueSources, ct).ConfigureAwait(false);
            if (starts[p] <= last)
            {
                PipelineLog.PartitionRecovered(_services.Logger, p, starts[p], last, replayed[p], elapsed);
            }
        }

        _recovered = true;
    }
}
