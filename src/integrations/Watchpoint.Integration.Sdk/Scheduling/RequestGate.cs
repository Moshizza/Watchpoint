using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Sdk.Scheduling;

/// <summary>
/// Outbound concurrency gate prioritizing requests by <see cref="CommandPriority"/> (INT §7.3, §10).
/// Critical requests preempt lower-priority requests in the queue.
/// </summary>
public sealed class RequestGate : IRequestGate, IDisposable
{
    private readonly int _maxConcurrency;
    private int _runningCount;
    private readonly object _lock = new();
    private readonly PriorityQueue<QueuedWaiter, int> _waiters = new();
    private bool _disposed;

    /// <inheritdoc />
    public int MaxConcurrency => _maxConcurrency;

    /// <summary>Initializes a new instance of <see cref="RequestGate"/>.</summary>
    /// <param name="maxConcurrency">Maximum concurrent requests allowed.</param>
    public RequestGate(int maxConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _maxConcurrency = maxConcurrency;
    }

    private static int PriorityToOrder(CommandPriority priority)
    {
        // Lower number in PriorityQueue dequeues first
        return priority switch
        {
            CommandPriority.Critical => 0,
            CommandPriority.Normal => 1,
            CommandPriority.Background => 2,
            _ => 1,
        };
    }

    /// <inheritdoc />
    public async ValueTask<T> RunAsync<T>(CommandPriority priority, Func<CancellationToken, ValueTask<T>> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        TaskCompletionSource? tcs = null;

        lock (_lock)
        {
            if (_runningCount < _maxConcurrency)
            {
                _runningCount++;
            }
            else
            {
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(new QueuedWaiter(tcs, ct), PriorityToOrder(priority));
            }
        }

        if (tcs is not null)
        {
            // Await our turn or cancellation
            using var reg = ct.Register(() =>
            {
                lock (_lock)
                {
                    tcs.TrySetCanceled(ct);
                }
            });

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // If we got canceled while in queue, propagate
                throw;
            }
        }

        try
        {
            return await request(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private void ReleaseSlot()
    {
        TaskCompletionSource? nextTcs = null;

        lock (_lock)
        {
            while (_waiters.Count > 0)
            {
                var waiter = _waiters.Dequeue();
                if (!waiter.Cancellation.IsCancellationRequested && waiter.Tcs.TrySetResult())
                {
                    nextTcs = waiter.Tcs;
                    break;
                }
            }

            if (nextTcs is null)
            {
                _runningCount--;
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
        lock (_lock)
        {
            while (_waiters.Count > 0)
            {
                var waiter = _waiters.Dequeue();
                waiter.Tcs.TrySetCanceled();
            }
        }
    }

    private sealed record QueuedWaiter(TaskCompletionSource Tcs, CancellationToken Cancellation);
}
