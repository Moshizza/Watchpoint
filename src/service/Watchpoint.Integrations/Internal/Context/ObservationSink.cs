using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>The counters of a connection, shared by its sink, command channel and status (thread-safe).</summary>
internal sealed class ConnectionCounterSource
{
    private readonly Lock _latencyLock = new();
    private readonly Queue<double> _latencies = new();
    private long _accepted;
    private long _rejected;
    private long _unknown;
    private long _commandsSucceeded;
    private long _commandsFailed;

    public long Accepted => Interlocked.Read(ref _accepted);

    public long Rejected => Interlocked.Read(ref _rejected);

    public long Unknown => Interlocked.Read(ref _unknown);

    public void AddAccepted(long count) => Interlocked.Add(ref _accepted, count);

    public void AddRejected(long count) => Interlocked.Add(ref _rejected, count);

    public void AddUnknown() => Interlocked.Increment(ref _unknown);

    public void CountCommand(bool success, TimeSpan duration)
    {
        Interlocked.Increment(ref success ? ref _commandsSucceeded : ref _commandsFailed);
        lock (_latencyLock)
        {
            _latencies.Enqueue(duration.TotalMilliseconds);
            if (_latencies.Count > 100)
            {
                _latencies.Dequeue();
            }
        }
    }

    public ConnectionCounters Snapshot()
    {
        double p95;
        lock (_latencyLock)
        {
            var sorted = _latencies.Order().ToArray();
            p95 = sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1)];
        }

        return new ConnectionCounters(Accepted, Rejected, Unknown, Interlocked.Read(ref _commandsSucceeded), Interlocked.Read(ref _commandsFailed), p95);
    }
}

/// <summary>
/// The connector's observation sink (spec IRT §8, INT §7.1): <c>Report*</c> never blocks; a background flusher sends one
/// batch at a time to <see cref="IObservationIngest"/> every 50 ms or 500 items, so the ingest's back-pressure stalls the
/// flusher, not the connector. Beyond <c>SinkBuffer</c> the reports spill into an overflow as large again; beyond that,
/// property reports are coalesced latest-per-(target, path) in place and events and health are kept. A report whose
/// target does not resolve against the device map is deferred, not counted unknown, while the connection's first
/// connect reconcile and map rebuild (IRT §7.2, §7.4) have not completed yet — ConnectorBase's own post-connect
/// refresh/catch-up (INT §9) otherwise races that rebuild and every such report would be lost as "unknown".
/// </summary>
internal sealed class ObservationSink : IObservationSink, IAsyncDisposable
{
    public const int BatchSize = 500;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _unknownLogInterval = TimeSpan.FromMinutes(1);

    private readonly ConnectionId _connection;
    private readonly IObservationIngest _ingest;
    private readonly DeviceMap _map;
    private readonly ConnectionCounterSource _counters;
    private readonly Action<InventorySnapshot> _onInventory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly LinkedList<Entry> _queue = new();
    private readonly List<Action> _deferredUntilMapped = [];
    private readonly Dictionary<(Guid Target, string Path), Entry> _coalescible = [];
    private readonly List<(long Seq, TaskCompletionSource Done)> _waiters = [];
    private readonly Dictionary<string, Connectivity> _serverConnectivity = new(StringComparer.Ordinal);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _flusher;
    private List<Entry> _inFlight = [];
    private long _seq;
    private bool _flushNow;
    private bool _closed;
    private bool _flooding;
    private long _reportedAccepted;
    private long _reportedRejected;
    private long _reportedUnknown;
    private long _unknownSinceLog;
    private long _lastUnknownLog;
    private long _lastReport;

    public ObservationSink(ConnectionId connection, IObservationIngest ingest, DeviceMap map, ConnectionCounterSource counters,
        Action<InventorySnapshot> onInventory, int bufferSize, TimeProvider time, ILogger logger)
    {
        _connection = connection;
        _ingest = ingest;
        _map = map;
        _counters = counters;
        _onInventory = onInventory;
        _time = time;
        _logger = logger;
        _capacity = Math.Max(1, bufferSize) * 2;
        _lastUnknownLog = time.GetTimestamp();
        _map.BecameReady += OnMapReady;
        _flusher = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Gets the observations waiting in the buffer (gauge <c>watchpoint.integrations.sink.buffer</c>).</summary>
    public int Buffered
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count + _inFlight.Count + _deferredUntilMapped.Count;
            }
        }
    }

    /// <summary>Gets the timestamp (<see cref="TimeProvider.GetTimestamp"/>) of the last report, 0 before the first (silence watchdog).</summary>
    public long LastReport => Interlocked.Read(ref _lastReport);

    /// <summary>Gets or sets a value indicating whether the ingest reports this connection as flooding: root server health then carries the fault <c>flooding</c>.</summary>
    public bool Flooding
    {
        get => Volatile.Read(ref _flooding);
        set => Volatile.Write(ref _flooding, value);
    }

    /// <summary>The last connectivity the connector reported for a server.</summary>
    public Connectivity? LastServerConnectivity(string serverNativeId)
    {
        lock (_lock)
        {
            return _serverConnectivity.TryGetValue(serverNativeId, out var c) ? c : null;
        }
    }

    public void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good,
        DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null)
    {
        if (!TryDevice(device, () => ReportProperty(device, propertyPath, value, quality, sourceTime, unit, causedByCommand), out var id)
            || !TryPath(propertyPath, out var path))
        {
            return;
        }

        Enqueue(new Observation
        {
            Id = NewId(),
            Kind = ObservationKind.Property,
            Device = id,
            Property = path,
            Value = value,
            Quality = quality,
            SourceTime = sourceTime,
            ReportedUnit = unit,
            Connection = _connection,
            CorrelatedCommand = causedByCommand is { } command && command != Guid.Empty ? CommandId.From(command) : null,
        }, (id.Value, path.Value));
    }

    public void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null, DateTimeOffset? sourceTime = null)
    {
        if (!TryDevice(device, () => ReportEvent(device, eventPath, payload, sourceTime), out var id))
        {
            return;
        }

        if (!EventPath.Parse(eventPath).TryGetValue(out var path, out _))
        {
            Refuse("event", eventPath);
            return;
        }

        Enqueue(new Observation
        {
            Id = NewId(),
            Kind = ObservationKind.Event,
            Device = id,
            Event = path,
            Payload = payload,
            SourceTime = sourceTime,
            Connection = _connection,
        }, null);
    }

    public void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
    {
        if (TryDevice(device, () => ReportDeviceHealth(device, connectivity, faults, reason), out var id))
        {
            Enqueue(Health(connectivity, faults ?? [], reason) with { Device = id }, null);
        }
    }

    public void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(serverNativeId);
        lock (_lock)
        {
            _serverConnectivity[serverNativeId] = connectivity;
        }

        if (!TryServer(serverNativeId, () => ReportServerHealth(serverNativeId, connectivity, faults, reason), out var server))
        {
            return;
        }

        var all = faults ?? [];
        if (server.IsRoot && Flooding && !all.Contains(FloodingFault, StringComparer.Ordinal))
        {
            all = [.. all, FloodingFault];
        }

        Enqueue(Health(connectivity, all, reason) with { Server = server.Id }, null);
    }

    public void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null)
    {
        if (!TryServer(serverNativeId, () => ReportServerProperty(serverNativeId, propertyPath, value, quality, sourceTime), out var server)
            || !TryPath(propertyPath, out var path))
        {
            return;
        }

        Enqueue(new Observation
        {
            Id = NewId(),
            Kind = ObservationKind.Property,
            Server = server.Id,
            Property = path,
            Value = value,
            Quality = quality,
            SourceTime = sourceTime,
            Connection = _connection,
        }, (server.Id.Value, path.Value));
    }

    public void ReportInventoryChanged(InventorySnapshot partialOrComplete)
    {
        ArgumentNullException.ThrowIfNull(partialOrComplete);
        if (!Volatile.Read(ref _closed))
        {
            _onInventory(partialOrComplete);
        }
    }

    public const string FloodingFault = "flooding";

    private Observation Health(Connectivity connectivity, IReadOnlyList<string> faults, string? reason) => new()
    {
        Id = NewId(),
        Kind = ObservationKind.Health,
        Health = new HealthReport(connectivity, faults, reason),
        Connection = _connection,
    };

    public async ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct)
    {
        TaskCompletionSource done;
        lock (_lock)
        {
            if (_closed && _queue.Count == 0 && _inFlight.Count == 0)
            {
                return TakeReport();
            }

            var target = _seq;
            if (FlushedSeq() >= target)
            {
                return TakeReport();
            }

            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, done));
            _flushNow = true;
        }

        _wake.Writer.TryWrite(true);
        await done.Task.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            return TakeReport();
        }
    }

    /// <summary>
    /// Closes the sink (INT L6: nothing the connector reports afterwards is kept), sends what is buffered within
    /// <paramref name="drainTimeout"/> and drops the rest (log 1843).
    /// </summary>
    public async Task CloseAsync(TimeSpan drainTimeout)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _flushNow = true;
        }

        _wake.Writer.TryWrite(true);
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_queue.Count == 0 && _inFlight.Count == 0)
            {
                drained.TrySetResult();
            }
            else
            {
                _waiters.Add((_seq, drained));
            }
        }

        try
        {
            await drained.Task.WaitAsync(drainTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Dropped below.
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _closed = true;
        }

        _map.BecameReady -= OnMapReady;
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _flusher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        int dropped;
        List<TaskCompletionSource> waiters;
        lock (_lock)
        {
            dropped = _queue.Count + _inFlight.Count + _deferredUntilMapped.Count;
            _queue.Clear();
            _inFlight = [];
            _coalescible.Clear();
            _deferredUntilMapped.Clear();
            waiters = [.. _waiters.Select(w => w.Done)];
            _waiters.Clear();
        }

        if (dropped > 0)
        {
            _counters.AddRejected(dropped);
            IntegrationLog.SinkDropped(_logger, _connection.Value, dropped);
        }

        waiters.ForEach(w => w.TrySetCanceled());
        _stop.Dispose();
    }

    private Guid NewId() => Guid.CreateVersion7(_time.GetUtcNow());

    private bool TryDevice(DeviceKey device, Action retry, out DeviceId id)
    {
        if (_map.Snapshot.Ids.TryGetValue(device, out id))
        {
            return true;
        }

        if (Defer(retry))
        {
            return false;
        }

        Unknown();
        return false;
    }

    private bool TryServer(string nativeId, Action retry, out MappedServer server)
    {
        if (_map.Snapshot.Servers.TryGetValue(nativeId, out server!))
        {
            return true;
        }

        if (Defer(retry))
        {
            return false;
        }

        Unknown();
        return false;
    }

    /// <summary>
    /// Buffers <paramref name="retry"/> (a replay of the original <c>Report*</c> call) instead of counting its target
    /// unknown, while the connection's device map has never been rebuilt since connect (<see cref="DeviceMap.Ready"/>);
    /// bounded by the sink's own capacity, oldest dropped first. Once rebuilt, <see cref="OnMapReady"/> replays every
    /// deferred call, which by then either resolves or is genuinely unknown.
    /// </summary>
    private bool Defer(Action retry)
    {
        bool deferred;
        var evicted = false;
        lock (_lock)
        {
            deferred = !_closed && !_map.Ready;
            if (deferred)
            {
                _lastReport = _time.GetTimestamp();
                if (_deferredUntilMapped.Count >= _capacity)
                {
                    _deferredUntilMapped.RemoveAt(0);
                    evicted = true;
                }

                _deferredUntilMapped.Add(retry);
            }
        }

        if (evicted)
        {
            // Rare (the race window is short): capacity-bounded, like the main buffer's overflow; no log per item.
            _counters.AddRejected(1);
        }

        return deferred;
    }

    /// <summary>Replays every report deferred by <see cref="Defer"/> once the device map has been rebuilt.</summary>
    private void OnMapReady()
    {
        List<Action> deferred;
        lock (_lock)
        {
            if (_closed || _deferredUntilMapped.Count == 0)
            {
                return;
            }

            deferred = [.. _deferredUntilMapped];
            _deferredUntilMapped.Clear();
        }

        foreach (var retry in deferred)
        {
            try
            {
                retry();
            }
            catch (Exception ex)
            {
                IntegrationLog.RuntimeOperationFailed(_logger, ex, _connection.Value, "deferred observation");
            }
        }
    }

    private bool TryPath(string text, out PropertyPath path)
    {
        if (PropertyPath.Parse(text).TryGetValue(out path, out _))
        {
            return true;
        }

        Refuse("property", text);
        return false;
    }

    private void Refuse(string kind, string path)
    {
        _counters.AddRejected(1);
        IntegrationLog.ObservationRejected(_logger, _connection.Value, "common.value.invalid_format", kind + " " + path);
    }

    private void Unknown()
    {
        if (Volatile.Read(ref _closed))
        {
            return;
        }

        _counters.AddUnknown();
        var count = Interlocked.Increment(ref _unknownSinceLog);
        var last = Interlocked.Read(ref _lastUnknownLog);
        if (_time.GetElapsedTime(last) >= _unknownLogInterval && Interlocked.CompareExchange(ref _lastUnknownLog, _time.GetTimestamp(), last) == last)
        {
            Interlocked.Add(ref _unknownSinceLog, -count);
            IntegrationLog.UnknownDevices(_logger, _connection.Value, count);
        }
    }

    private void Enqueue(Observation observation, (Guid, string)? coalesceKey)
    {
        bool wake;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            var seq = ++_seq;
            _lastReport = _time.GetTimestamp();
            if (_queue.Count + _inFlight.Count >= _capacity && coalesceKey is { } key)
            {
                if (_coalescible.TryGetValue(key, out var existing))
                {
                    // Keeps the place of the value it replaces (a property that changes all the time is not starved).
                    existing.Observation = observation;
                    return;
                }

                var entry = new Entry(observation, seq, _time.GetTimestamp(), key);
                _coalescible[key] = entry;
                _queue.AddLast(entry);
            }
            else
            {
                _queue.AddLast(new Entry(observation, seq, _time.GetTimestamp(), null));
            }

            wake = _queue.Count == 1 || _queue.Count >= BatchSize;
        }

        if (wake)
        {
            _wake.Writer.TryWrite(true);
        }
    }

    private long FlushedSeq() =>
        (_inFlight.Count > 0 ? _inFlight[0].Seq : _queue.First is { } first ? first.Value.Seq : _seq + 1) - 1;

    private SinkFlushResult TakeReport()
    {
        var accepted = _counters.Accepted;
        var rejected = _counters.Rejected;
        var unknown = _counters.Unknown;
        var result = new SinkFlushResult((int)(accepted - _reportedAccepted), (int)(rejected - _reportedRejected), (int)(unknown - _reportedUnknown));
        (_reportedAccepted, _reportedRejected, _reportedUnknown) = (accepted, rejected, unknown);
        return result;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.Reader.ReadAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested && await WaitForBatchAsync(ct).ConfigureAwait(false) is { Count: > 0 } batch)
                {
                    await SendAsync(batch, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never leaves the flusher dead with reports waiting: log, pause, go on.
                IntegrationLog.RuntimeOperationFailed(_logger, ex, _connection.Value, "sink flush");
                await Task.Delay(_retryDelay, _time, ct).ConfigureAwait(false);
                _wake.Writer.TryWrite(true);
            }
        }
    }

    /// <summary>Waits until 500 items are buffered, a flush is asked for, or the oldest item has waited 50 ms; takes a batch.</summary>
    private async Task<List<Observation>?> WaitForBatchAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _flushNow = false;
                    return null;
                }

                var waited = _time.GetElapsedTime(_queue.First!.Value.EnqueuedAt);
                if (_queue.Count >= BatchSize || _flushNow || waited >= FlushInterval)
                {
                    return TakeBatch();
                }

                wait = FlushInterval - waited;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(wait, _time, timeout.Token);
            var woken = _wake.Reader.WaitToReadAsync(timeout.Token).AsTask();
            await Task.WhenAny(delay, woken).ConfigureAwait(false);
            await timeout.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _wake.Reader.TryRead(out _);
        }
    }

    private List<Observation> TakeBatch()
    {
        var batch = new List<Observation>(Math.Min(BatchSize, _queue.Count));
        var taken = new List<Entry>(batch.Capacity);
        while (batch.Count < BatchSize && _queue.First is { } node)
        {
            _queue.RemoveFirst();
            var entry = node.Value;
            if (entry.CoalesceKey is { } key && _coalescible.TryGetValue(key, out var indexed) && ReferenceEquals(indexed, entry))
            {
                _coalescible.Remove(key);
            }

            taken.Add(entry);
            batch.Add(entry.Observation);
        }

        if (_queue.Count == 0)
        {
            _flushNow = false;
        }

        _inFlight = taken;
        return batch;
    }

    private async Task SendAsync(List<Observation> batch, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var result = await _ingest.IngestAsync(batch, ct).ConfigureAwait(false);
                _counters.AddAccepted(result.Accepted + result.Unchanged + result.Coalesced);
                _counters.AddRejected(result.Rejected.Count);
                foreach (var rejected in result.Rejected.Take(10))
                {
                    IntegrationLog.ObservationRejected(_logger, _connection.Value, rejected.Error.Code.Value, rejected.ObservationId.ToString("D"));
                }

                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The ingest throws only on cancellation (PIP §6); anything else is retried with the same ids (deduplicated).
                IntegrationLog.SinkIngestFailed(_logger, ex, _connection.Value, batch.Count);
                await Task.Delay(_retryDelay, _time, ct).ConfigureAwait(false);
            }
        }

        List<TaskCompletionSource> done = [];
        lock (_lock)
        {
            _inFlight = [];
            var flushed = FlushedSeq();
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Seq <= flushed)
                {
                    done.Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }

        done.ForEach(d => d.TrySetResult());
    }

    private sealed class Entry(Observation observation, long seq, long enqueuedAt, (Guid, string)? coalesceKey)
    {
        public Observation Observation { get; set; } = observation;

        public long Seq { get; } = seq;

        public long EnqueuedAt { get; } = enqueuedAt;

        public (Guid, string)? CoalesceKey { get; } = coalesceKey;
    }
}
