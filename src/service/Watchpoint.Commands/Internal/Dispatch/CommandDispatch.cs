using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Watchpoint.Commands.Contracts;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal.Dispatch;

/// <summary>Receives the commands the processor moved to <c>Queued</c> and the end of a partition's recovery.</summary>
internal interface ICommandQueueSink
{
    /// <summary>A command became <c>Queued</c> (on the worker of its partition: must not block).</summary>
    void Queued(Command command);

    /// <summary>The partition is recovered (on its worker: must not block).</summary>
    void Recovered(int partition);
}

/// <summary>
/// Routes queued commands to the <see cref="ConnectionDispatcher"/> of their device's connection, one per connection,
/// created on first use (spec CMD §7), and applies the recovery rules of CMD §8 when a partition is recovered.
/// </summary>
internal sealed class CommandDispatch : ICommandQueueSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<ConnectionId, ConnectionDispatcher> _dispatchers = new();
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatchServices _s;
    private readonly ITypeCatalog _types;
    private int _disposed;

    public CommandDispatch(DispatchServices services, ITypeCatalog types, CommandMetrics metrics)
    {
        _s = services;
        _types = types;
        _s.Connections.StateChanged += OnStateChanged;
        _s.Processor.Sink = this;
        metrics.ObserveQueue(Measure);
    }

    /// <summary>The dispatcher of a connection, if it has one yet.</summary>
    public ConnectionDispatcher? Of(ConnectionId connection) => _dispatchers.GetValueOrDefault(connection);

    public void Queued(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_s.ConnectionOf(command.Target) is not { } connection)
        {
            // The device (or its server) was deleted: the command can never be dispatched.
            Run(Partitioning.Of(command.Target), emitter => FailMissing(emitter, command.Id));
            return;
        }

        _dispatchers.GetOrAdd(connection, c => new ConnectionDispatcher(c, _s)).Enqueue(command);
    }

    /// <summary>
    /// CMD §8 on a recovered partition: <c>Queued</c> commands are queued again in priority order; a <c>Dispatched</c>
    /// command without outcome is queued again as attempt 2 when its definition is idempotent and it was the first attempt,
    /// otherwise it fails with <c>commands.outcome_unknown</c>; <c>AwaitingApproval</c> commands keep waiting (expiry applies).
    /// </summary>
    public void Recovered(int partition) => Run(partition, emitter =>
    {
        var store = _s.Processor.Store(partition);
        var queued = new List<Command>();
        foreach (var command in store.Commands)
        {
            if (command.State == CommandState.Queued)
            {
                queued.Add(command);
            }
            else if (command.State == CommandState.Dispatched)
            {
                emitter.Emit(Idempotent(command) && command.Attempt == 1
                    ? _s.Codec.Envelope(CommandRecordKinds.Queued, command, new CommandQueuedBody(command.Id, command.Priority, 2), emitter.Now)
                    : _s.Codec.Envelope(CommandRecordKinds.Failed, command,
                        new CommandFailedBody(command.Id, DispatchServices.Failure(FailureCategory.Conflict, CommandErrors.OutcomeUnknown())), emitter.Now));
            }
        }

        foreach (var command in queued.OrderBy(c => c.Priority).ThenBy(c => c.QueuedAt ?? c.RequestedAt))
        {
            Queued(command);
        }

        // CMD §9: an expected effect that became visible while the node was down or during the replay.
        _s.Processor.CheckWatchers(emitter);
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _s.Connections.StateChanged -= OnStateChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_background.Keys).ConfigureAwait(false);
        foreach (var dispatcher in _dispatchers.Values)
        {
            await dispatcher.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private bool Idempotent(Command command) =>
        _s.Registry.GetDevice(command.Target).TryGetValue(out var device, out _)
        && _types.Get(device.Type).TryGetValue(out var type, out _)
        && type.Commands.TryGetValue(command.Path, out var definition)
        && definition.Idempotent;

    private void FailMissing(ICommandEmitter emitter, CommandId id)
    {
        if (_s.Processor.Find(emitter.Partition, id) is { State: CommandState.Queued } command)
        {
            var failure = DispatchServices.Failure(FailureCategory.InvalidState, CommandErrors.TargetNotFound(command.Target));
            emitter.Emit(_s.Codec.Envelope(CommandRecordKinds.Failed, command, new CommandFailedBody(command.Id, failure), emitter.Now));
        }
    }

    /// <summary>Posted work started from a partition worker: never awaited there, tracked until it ends.</summary>
    private void Run(int partition, Action<ICommandEmitter> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await _s.Partitions.RunAsync(partition, emitter =>
                {
                    work(emitter);
                    return true;
                }, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Node stopping.
            }
            catch (Exception ex)
            {
                CommandsLog.DispatchFailed(_s.Logger, ex, Guid.Empty);
            }
        });
        _background.TryAdd(task, 0);
        _ = task.ContinueWith(t => _background.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnStateChanged(object? sender, ConnectionId connection) => Of(connection)?.Signal();

    private IEnumerable<Measurement<int>> Measure()
    {
        foreach (var dispatcher in _dispatchers.Values)
        {
            foreach (var priority in Enum.GetValues<Contracts.CommandPriority>())
            {
                yield return new Measurement<int>(dispatcher.Queued(priority),
                    new KeyValuePair<string, object?>("connection", dispatcher.Connection.Value),
                    new KeyValuePair<string, object?>("priority", priority.ToString()));
            }
        }
    }
}
