using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>What command code sees of a partition worker: the batch time and follow-up records.</summary>
internal interface ICommandEmitter
{
    /// <summary>The partition.</summary>
    int Partition { get; }

    /// <summary>The time of the batch, posted work or tick (node clock).</summary>
    DateTimeOffset Now { get; }

    /// <summary>Emits a record of the partition: appended after the current work, then given to the subscribers.</summary>
    void Emit(LogEnvelope record);
}

/// <summary>
/// Runs command work on the partition of the target device (spec PIP §7.3 posted work), so every update of one command
/// is serialised with the others and with the partition's records. The records the work emits are appended to the log
/// and applied by the command subscriber before the returned task completes.
/// </summary>
internal interface ICommandPartitions
{
    Task<T> RunAsync<T>(int partition, Func<ICommandEmitter, T> work, CancellationToken ct);
}

/// <summary>Callers waiting for their records to be applied by the subscriber (by record id).</summary>
internal sealed class CommandWaiters
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> _waiting = new();

    public Task Register(Guid recordId) =>
        _waiting.GetOrAdd(recordId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Complete(Guid recordId)
    {
        if (_waiting.TryRemove(recordId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }
}

/// <summary>The production <see cref="ICommandPartitions"/>: posted work of the pipeline (<see cref="IPartitionDispatcher"/>).</summary>
internal sealed class PipelineCommandPartitions(IPartitionDispatcher dispatcher, CommandWaiters waiters) : ICommandPartitions
{
    public async Task<T> RunAsync<T>(int partition, Func<ICommandEmitter, T> work, CancellationToken ct)
    {
        Task applied = Task.CompletedTask;
        var result = await dispatcher.InvokeAsync(
            partition,
            context =>
            {
                var emitter = new PartitionEmitter(context);
                var value = work(emitter);
                if (emitter.Last is { } last)
                {
                    // Registered before the worker appends the follow-ups, so the subscriber's completion cannot be missed.
                    applied = waiters.Register(last);
                }

                return ValueTask.FromResult(value);
            },
            ct).ConfigureAwait(false);
        await applied.WaitAsync(ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>A <see cref="PartitionContext"/> seen as an <see cref="ICommandEmitter"/>.</summary>
internal sealed class PartitionEmitter(PartitionContext context) : ICommandEmitter
{
    public Guid? Last { get; private set; }

    public int Partition => context.Partition;

    public DateTimeOffset Now => context.Now;

    public void Emit(LogEnvelope record)
    {
        context.Emit(record);
        Last = record.Id;
    }
}
