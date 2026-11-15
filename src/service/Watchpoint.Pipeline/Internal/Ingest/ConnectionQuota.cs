using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Ingest;

/// <summary>
/// The quota of one connection (spec PIP §8): a token bucket, the coalescing buffer for what exceeds it, the task that
/// drains the buffer as tokens refill, and the flooding state. All state is guarded by one lock; observations of a
/// connection reach the partitions in the order they were ingested, whether they waited in the buffer or not.
/// </summary>
internal sealed class ConnectionQuota
{
    private const int DrainChunk = 500;

    private readonly Lock _gate = new();
    private readonly ConnectionId _connection;
    private readonly IngestOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<IngestItem, CancellationToken, ValueTask> _route;
    private readonly IReadOnlyList<IIngestFloodingListener> _listeners;
    private readonly ILogger _logger;
    private readonly CancellationToken _stop;
    private readonly CoalescingBuffer _buffer = new();
    private double _tokens;
    private long _refilled;
    private bool _draining;
    private long _coalescingSince;
    private long _coalesced;
    private bool _flooding;
    private TaskCompletionSource? _floodGate;

    public ConnectionQuota(
        ConnectionId connection,
        IngestOptions options,
        TimeProvider time,
        Func<IngestItem, CancellationToken, ValueTask> route,
        IReadOnlyList<IIngestFloodingListener> listeners,
        ILogger logger,
        CancellationToken stop)
    {
        _connection = connection;
        _options = options;
        _time = time;
        _route = route;
        _listeners = listeners;
        _logger = logger;
        _stop = stop;
        _tokens = options.ConnectionBurst;
        _refilled = time.GetTimestamp();
    }

    /// <summary>Whether the connection is flooding (for tests and diagnostics).</summary>
    public bool Flooding
    {
        get
        {
            lock (_gate)
            {
                return _flooding;
            }
        }
    }

    /// <summary>
    /// Takes a token for an observation. Returns <see langword="true"/> when the caller routes it itself; otherwise the
    /// observation waits in the buffer and <paramref name="replaced"/> is the property observation it replaced, if any.
    /// </summary>
    public bool TryPass(IngestItem item, out IngestItem? replaced)
    {
        replaced = null;
        lock (_gate)
        {
            // While something waits, everything waits behind it: the order of the connection is kept.
            if (!_draining)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return true;
                }

                _draining = true;
                _coalescingSince = _time.GetTimestamp();
                _coalesced = 0;
                PipelineLog.OverQuota(_logger, _connection.Value);
                _ = Task.Run(DrainAsync, CancellationToken.None);
            }

            replaced = _buffer.Add(item);
            if (replaced is not null)
            {
                _coalesced++;
            }

            EvaluateFlooding();
            return false;
        }
    }

    /// <summary>Waits while the connection is flooding and too many events wait (spec PIP §8).</summary>
    public Task WaitWhileFloodedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_flooding || _buffer.FifoCount < _options.FloodingLow)
            {
                return Task.CompletedTask;
            }

            _floodGate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _floodGate.Task.WaitAsync(ct);
        }
    }

    /// <summary>Routes what waits, as fast as tokens refill; ends when the buffer is empty.</summary>
    private async Task DrainAsync()
    {
        var chunk = new List<IngestItem>(DrainChunk);
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_gate)
                {
                    if (_buffer.Count == 0)
                    {
                        EndCoalescing();
                        return;
                    }

                    Refill();
                    var take = (int)Math.Min(Math.Min(Math.Floor(_tokens), _buffer.Count), DrainChunk);
                    for (var i = 0; i < take && _buffer.TryTake(out var item); i++)
                    {
                        chunk.Add(item);
                    }

                    _tokens -= chunk.Count;
                    EvaluateFlooding();
                    wait = chunk.Count > 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(Math.Max((1 - _tokens) / _options.ConnectionRate, 0.001));
                }

                if (chunk.Count == 0)
                {
                    await Task.Delay(wait, _time, _stop).ConfigureAwait(false);
                    continue;
                }

                foreach (var item in chunk)
                {
                    await _route(item, _stop).ConfigureAwait(false);
                }

                chunk.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // The pipeline stops: what still waits is rejected, never dropped silently.
            var error = PipelineErrors.LogUnavailable("The pipeline stopped before the observation was processed.");
            foreach (var item in chunk)
            {
                item.Reject(error);
            }

            lock (_gate)
            {
                while (_buffer.TryTake(out var item))
                {
                    item.Reject(error);
                }

                EndCoalescing();
            }
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_refilled, now).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_options.ConnectionBurst, _tokens + (elapsed * _options.ConnectionRate));
            _refilled = now;
        }
    }

    private void EvaluateFlooding()
    {
        var pending = _buffer.FifoCount;
        if (!_flooding && (pending > _options.FloodingHigh || _time.GetElapsedTime(_coalescingSince) > _options.FloodingAfter))
        {
            _flooding = true;
            PipelineLog.Flooding(_logger, _connection.Value, pending);
            Notify(true, pending);
        }

        if (_floodGate is not null && pending < _options.FloodingLow)
        {
            _floodGate.TrySetResult();
            _floodGate = null;
        }
    }

    private void EndCoalescing()
    {
        _draining = false;
        PipelineLog.WithinQuota(_logger, _connection.Value, _coalesced);
        if (_flooding)
        {
            _flooding = false;
            Notify(false, 0);
        }

        _floodGate?.TrySetResult();
        _floodGate = null;
    }

    /// <summary>Called under the lock, so that listeners see the changes of a connection in order.</summary>
    private void Notify(bool flooding, int pending)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                listener.OnFloodingChanged(_connection, flooding, pending);
            }
#pragma warning disable CA1031 // A faulty listener must not break the ingest.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                PipelineLog.FloodingListenerFailed(_logger, ex, listener.GetType().Name);
            }
        }
    }
}
