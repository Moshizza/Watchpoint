using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>
/// The connector's outbound gate (spec IRT §8): at most <see cref="MaxConcurrency"/> requests run at once; waiting
/// requests start in priority order Critical &gt; High &gt; Normal &gt; Background, first come first served within a priority.
/// </summary>
internal sealed class RequestGate : IRequestGate, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Queue<Waiter>[] _queues = [new(), new(), new(), new()];
    private int _running;
    private bool _disposed;

    public RequestGate(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        MaxConcurrency = maxConcurrency;
    }

    public int MaxConcurrency { get; }

    public async ValueTask<T> RunAsync<T>(CommandPriority priority, Func<CancellationToken, ValueTask<T>> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        Waiter? waiter = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running < MaxConcurrency)
            {
                _running++;
            }
            else
            {
                waiter = new Waiter();
                _queues[Level(priority)].Enqueue(waiter);
            }
        }

        if (waiter is not null)
        {
            using (ct.Register(static w => ((Waiter)w!).Turn.TrySetCanceled(), waiter))
            {
                try
                {
                    await waiter.Turn.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled while waiting: it never held a slot (a turn given to a cancelled waiter goes to the next one).
                    throw new OperationCanceledException(ct);
                }
            }
        }

        try
        {
            return await request(ct).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    public void Dispose()
    {
        List<Waiter> waiting;
        lock (_lock)
        {
            _disposed = true;
            waiting = [.. _queues.SelectMany(q => q)];
            Array.ForEach(_queues, q => q.Clear());
        }

        waiting.ForEach(w => w.Turn.TrySetCanceled());
    }

    private static int Level(CommandPriority priority) => priority switch
    {
        CommandPriority.Critical => 0,
        CommandPriority.High => 1,
        CommandPriority.Normal => 2,
        _ => 3,
    };

    private void Release()
    {
        lock (_lock)
        {
            foreach (var queue in _queues)
            {
                while (queue.TryDequeue(out var next))
                {
                    if (next.Turn.TrySetResult())
                    {
                        // The slot passes to the next request: _running is unchanged.
                        return;
                    }
                }
            }

            _running--;
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
