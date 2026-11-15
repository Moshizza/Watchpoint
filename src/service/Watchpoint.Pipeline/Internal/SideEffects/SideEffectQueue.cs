using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.SideEffects;

/// <summary>
/// The side-effect queue (spec PIP §9): per class one bounded queue and its own workers, so a full or slow class never
/// delays another. An effect is identified by its id and runs at most once per node lifetime however often it is
/// enqueued; a failing run is repeated up to <c>MaxAttempts</c> with exponential back-off, then logged (1660) and
/// dropped. Nothing is persisted: effects not yet run are lost when the node stops.
/// </summary>
internal sealed class SideEffectQueue : ISideEffectQueue, IAsyncDisposable
{
    private readonly Dictionary<SideEffectClass, ClassQueue> _classes = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly IngestOptions _options;
    private readonly TimeProvider _time;
    private readonly PipelineMetrics _metrics;
    private readonly ILogger<SideEffectQueue> _logger;
    private readonly Lock _lifecycle = new();
    private readonly List<Task> _workers = [];
    private bool _started;
    private bool _stopped;
    private int _disposed;

    public SideEffectQueue(IngestOptions options, TimeProvider timeProvider, PipelineMetrics metrics, ILogger<SideEffectQueue> logger)
    {
        _options = options;
        _time = timeProvider;
        _metrics = metrics;
        _logger = logger;
        foreach (var effectClass in Enum.GetValues<SideEffectClass>())
        {
            _classes[effectClass] = new ClassQueue(effectClass, options.SideEffectCapacity[effectClass], options.SideEffectIdMemory);
        }

        metrics.ObserveSideEffectQueues(() => _classes.Values.Select(c =>
            new Measurement<int>(c.Channel.Reader.Count, new KeyValuePair<string, object?>("class", c.Class.ToString()))));
    }

    public bool TryEnqueue(SideEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (!_classes.TryGetValue(effect.Class, out var queue))
        {
            throw new ArgumentOutOfRangeException(nameof(effect), effect.Class, "Unknown side-effect class.");
        }

        Start();
        long refused;
        lock (queue)
        {
            if (queue.Seen.Contains(effect.Id))
            {
                queue.Duplicates++;
                return true;
            }

            if (queue.Channel.Writer.TryWrite(effect))
            {
                queue.Remember(effect.Id);
                return true;
            }

            queue.Rejected++;
            queue.RefusedSinceLog++;
            var now = _time.GetTimestamp();
            if (queue.RefusalLogged && _time.GetElapsedTime(queue.LastRefusalLog, now) < _options.RejectionLogInterval)
            {
                return false;
            }

            queue.RefusalLogged = true;
            queue.LastRefusalLog = now;
            refused = queue.RefusedSinceLog;
            queue.RefusedSinceLog = 0;
        }

        PipelineLog.SideEffectRefused(_logger, effect.Class.ToString(), refused, effect.Kind);
        return false;
    }

    public SideEffectQueueStats GetStats()
    {
        var stats = new Dictionary<SideEffectClass, SideEffectClassStats>();
        foreach (var (effectClass, queue) in _classes)
        {
            lock (queue)
            {
                stats[effectClass] = new SideEffectClassStats(
                    queue.Channel.Reader.Count, queue.Running, queue.Completed, queue.Failed, queue.Rejected, queue.Duplicates);
            }
        }

        return new SideEffectQueueStats(stats);
    }

    /// <summary>Starts the workers of every class; idempotent.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _started))
        {
            return;
        }

        lock (_lifecycle)
        {
            if (_started || _stopped)
            {
                return;
            }

            foreach (var queue in _classes.Values)
            {
                for (var i = 0; i < _options.SideEffectWorkers[queue.Class]; i++)
                {
                    _workers.Add(Task.Run(() => RunAsync(queue)));
                }
            }

            Volatile.Write(ref _started, true);
        }
    }

    /// <summary>Stops taking effects, lets the workers finish what is queued for at most the drain time, then cancels them.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        Task all;
        lock (_lifecycle)
        {
            _stopped = true;
            foreach (var queue in _classes.Values)
            {
                queue.Channel.Writer.TryComplete();
            }

            all = Task.WhenAll(_workers);
        }

        try
        {
            await all.WaitAsync(_options.DrainTimeout, _time, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await all.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The container disposes the queue once per registration (class and interface).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(ClassQueue queue)
    {
        var stop = _stop.Token;
        try
        {
            await foreach (var effect in queue.Channel.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                lock (queue)
                {
                    queue.Running++;
                }

                var succeeded = false;
                try
                {
                    succeeded = await RunEffectAsync(effect, stop).ConfigureAwait(false);
                }
                finally
                {
                    lock (queue)
                    {
                        queue.Running--;
                        if (succeeded)
                        {
                            queue.Completed++;
                        }
                        else
                        {
                            queue.Failed++;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped: effects not yet run are lost (at most once).
        }
    }

    /// <summary>Runs an effect until it succeeds or its attempts are used up; never throws except when the queue stops.</summary>
    private async Task<bool> RunEffectAsync(SideEffect effect, CancellationToken stop)
    {
        var attempts = Math.Max(effect.MaxAttempts, 1);
        var delay = _options.SideEffectRetryDelay;
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(delay, _time, stop).ConfigureAwait(false);
                delay = delay + delay < _options.SideEffectRetryDelayMax ? delay + delay : _options.SideEffectRetryDelayMax;
            }

            using var timeout = new CancellationTokenSource(effect.Timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
            try
            {
                // WaitAsync: an effect that ignores its cancellation token must not hold a worker for ever.
                var run = effect.Run(linked.Token) ?? throw new InvalidOperationException("The side effect returned no task.");
                await run.WaitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Whatever a side effect throws is its failure: retried, then logged and dropped.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                last = ex is OperationCanceledException && timeout.IsCancellationRequested
                    ? new TimeoutException($"The side effect did not complete within {effect.Timeout}.")
                    : ex;
            }
        }

        PipelineLog.SideEffectFailed(_logger, last, effect.Kind, effect.Class.ToString(), attempts);
        _metrics.SideEffectFailed(effect.Class, effect.Kind);
        return false;
    }

    /// <summary>The queue and counters of one class; guarded by locking the instance.</summary>
    private sealed class ClassQueue(SideEffectClass effectClass, int capacity, int idMemory)
    {
        private readonly Queue<Guid> _order = new();

        public SideEffectClass Class { get; } = effectClass;

        public Channel<SideEffect> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<SideEffect>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });

        public HashSet<Guid> Seen { get; } = [];

        public int Running { get; set; }

        public long Completed { get; set; }

        public long Failed { get; set; }

        public long Rejected { get; set; }

        public long Duplicates { get; set; }

        public long RefusedSinceLog { get; set; }

        public bool RefusalLogged { get; set; }

        public long LastRefusalLog { get; set; }

        /// <summary>Remembers an id; the oldest is forgotten when the memory is full.</summary>
        public void Remember(Guid id)
        {
            Seen.Add(id);
            _order.Enqueue(id);
            if (_order.Count > Math.Max(idMemory, capacity))
            {
                Seen.Remove(_order.Dequeue());
            }
        }
    }
}
