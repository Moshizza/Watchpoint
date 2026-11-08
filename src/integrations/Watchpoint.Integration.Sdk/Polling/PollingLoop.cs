using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Sdk.Polling;

/// <summary>
/// Execution loop for periodic connector polling (INT §10).
/// Non-overlapping iterations, jittered interval, exponential backoff on error doubling up to MaxBackoff,
/// awaits Sink.FlushAsync after each iteration, and pause/resume based on connection state.
/// </summary>
public sealed class PollingLoop : IDisposable
{
    private readonly string _name;
    private readonly TimeSpan _baseInterval;
    private readonly Func<CancellationToken, ValueTask> _work;
    private readonly IObservationSink _sink;
    private readonly TimeProvider _time;
    private readonly PollingOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private readonly object _stateLock = new();

    private bool _paused;
    private bool _disposed;
    private int _consecutiveErrors;

    /// <summary>Gets the name of the polling loop.</summary>
    public string Name => _name;

    /// <summary>Gets whether the loop is currently paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                return _paused;
            }
        }
    }

    /// <summary>Initializes and starts a polling loop.</summary>
    /// <param name="name">Name of the loop for diagnostics.</param>
    /// <param name="interval">Base interval between poll executions.</param>
    /// <param name="work">Async callback executed on each iteration.</param>
    /// <param name="sink">The observation sink flushed after each iteration.</param>
    /// <param name="time">Time provider for delays.</param>
    /// <param name="options">Polling options.</param>
    public PollingLoop(
        string name,
        TimeSpan interval,
        Func<CancellationToken, ValueTask> work,
        IObservationSink sink,
        TimeProvider time,
        PollingOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);

        _name = name;
        _baseInterval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : interval;
        _work = work;
        _sink = sink;
        _time = time;
        _options = options ?? new PollingOptions();

        _loopTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Pauses the polling loop (e.g. when connection is lost).</summary>
    public void Pause()
    {
        lock (_stateLock)
        {
            _paused = true;
        }
    }

    /// <summary>Resumes the polling loop (e.g. when connection is established).</summary>
    public void Resume()
    {
        lock (_stateLock)
        {
            _paused = false;
        }
    }

    /// <summary>Calculates interval with jitter (e.g. ±10% by default).</summary>
    internal static TimeSpan ComputeNextDelay(TimeSpan baseDelay, double jitter)
    {
        if (jitter <= 0.0)
        {
            return baseDelay;
        }

        // Jitter between (1 - jitter) and (1 + jitter)
        var factor = (1.0 - jitter) + (Random.Shared.NextDouble() * (2.0 * jitter));
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    /// <summary>Calculates error back-off delay doubling each consecutive failure up to MaxBackoff.</summary>
    internal TimeSpan ComputeErrorBackoff(int errorCount)
    {
        var maxBackoff = _options.MaxBackoff ?? TimeSpan.FromMinutes(5);
        if (errorCount <= 0)
        {
            return _baseInterval;
        }

        // Doubling: baseInterval * 2^(errorCount - 1)
        var multiplier = Math.Pow(2, Math.Min(errorCount - 1, 30));
        var backoffMs = _baseInterval.TotalMilliseconds * multiplier;

        if (backoffMs > maxBackoff.TotalMilliseconds)
        {
            return maxBackoff;
        }

        return TimeSpan.FromMilliseconds(backoffMs);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var shouldRun = false;
            lock (_stateLock)
            {
                shouldRun = !_paused || _options.RunWhenDisconnected;
            }

            if (shouldRun)
            {
                var succeeded = false;
                try
                {
                    await _work(ct).ConfigureAwait(false);
                    await _sink.FlushAsync(ct).ConfigureAwait(false);
                    succeeded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Exceptions inside polling callbacks do not stop the loop
                    succeeded = false;
                }

                if (succeeded)
                {
                    _consecutiveErrors = 0;
                }
                else
                {
                    _consecutiveErrors++;
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            TimeSpan delay;
            if (_consecutiveErrors > 0)
            {
                delay = ComputeErrorBackoff(_consecutiveErrors);
            }
            else
            {
                delay = ComputeNextDelay(_baseInterval, _options.Jitter);
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
