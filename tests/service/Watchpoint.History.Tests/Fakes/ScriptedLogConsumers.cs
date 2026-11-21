using System.Runtime.CompilerServices;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>
/// A consumer factory whose consumers hand out scripted batches like the real consumer: a batch that was not acknowledged comes
/// again (counted as a new delivery), and nothing newer is delivered before it is acknowledged.
/// </summary>
internal sealed class ScriptedLogConsumers : ILogConsumerFactory
{
    private readonly Lock _gate = new();
    private readonly Queue<IReadOnlyList<LogRecord>> _pending = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(string Name, LogStream Stream, IReadOnlyList<string> Kinds, LogConsumerOptions Options)> Created { get; } = [];

    /// <summary>Every delivery of a batch: the number of the batch and its delivery count.</summary>
    public List<(int Batch, int Delivery)> Deliveries { get; } = [];

    public List<int> Acked { get; } = [];

    /// <summary>The acknowledgement fails this many times with a <see cref="PipelineException"/> before it works.</summary>
    public int AckFailures { get; set; }

    public int AckAttempts { get; private set; }

    public Action<int>? OnAcked { get; set; }

    public void Enqueue(params LogRecord[] records)
    {
        lock (_gate)
        {
            _pending.Enqueue(records);
            _arrived.TrySetResult();
        }
    }

    public ILogConsumer CreateDurable(string name, LogStream stream, IReadOnlyList<string> kindFilter, LogConsumerOptions options)
    {
        Created.Add((name, stream, kindFilter, options));
        return new Consumer(this);
    }

    private sealed class Consumer(ScriptedLogConsumers owner) : ILogConsumer
    {
        public async IAsyncEnumerable<LogRecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var number = 0;
            while (true)
            {
                IReadOnlyList<LogRecord>? records;
                Task wait;
                lock (owner._gate)
                {
                    wait = owner._arrived.Task;
                    records = owner._pending.TryPeek(out var next) ? next : null;
                }

                if (records is null)
                {
                    await wait.WaitAsync(ct).ConfigureAwait(false);
                    lock (owner._gate)
                    {
                        owner._arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (owner._pending.Count > 0)
                        {
                            owner._arrived.TrySetResult();
                        }
                    }

                    continue;
                }

                number++;
                var acked = false;
                var delivery = 0;
                while (!acked)
                {
                    ct.ThrowIfCancellationRequested();
                    delivery++;
                    owner.Deliveries.Add((number, delivery));
                    var captured = number;
                    yield return new LogRecordBatch(records, token =>
                    {
                        owner.AckAttempts++;
                        if (owner.AckFailures > 0)
                        {
                            owner.AckFailures--;
                            throw new PipelineException(PipelineErrors.LogUnavailable("test"));
                        }

                        acked = true;
                        owner.Acked.Add(captured);
                        owner.OnAcked?.Invoke(captured);
                        return ValueTask.CompletedTask;
                    })
                    { Delivery = delivery };
                }

                lock (owner._gate)
                {
                    owner._pending.Dequeue();
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
