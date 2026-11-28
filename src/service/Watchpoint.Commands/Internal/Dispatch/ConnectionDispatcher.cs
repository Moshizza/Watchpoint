using Microsoft.Extensions.Logging;
using Watchpoint.Commands.Contracts;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using ConnectorPriority = Watchpoint.Integration.Abstractions.Model.CommandPriority;

namespace Watchpoint.Commands.Internal.Dispatch;

/// <summary>
/// Dispatches the queued commands of one connection (spec CMD §7): highest priority first, one in-flight command per
/// device, at most <c>Commands:MaxInFlightPerConnection</c> in flight, <c>NotBefore</c> and expiry respected, fail fast
/// while the connection is down. Every lifecycle record is appended by posted work on the target's partition, which
/// re-reads the command there, so a command cancelled or expired meanwhile is never dispatched.
/// </summary>
internal sealed class ConnectionDispatcher : IAsyncDisposable
{
    private readonly DispatchServices _s;
    private readonly Lock _gate = new();
    private readonly PriorityCommandQueue _queue = new();
    private readonly DeviceLocks _locks = new();
    private readonly HashSet<Task> _running = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _inFlight;

    public ConnectionDispatcher(ConnectionId connection, DispatchServices services)
    {
        Connection = connection;
        _s = services;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public ConnectionId Connection { get; }

    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>The number of queued commands of a priority (metric <c>watchpoint.commands.queue</c>).</summary>
    public int Queued(Contracts.CommandPriority priority)
    {
        lock (_gate)
        {
            return _queue.CountOf(priority);
        }
    }

    /// <summary>Queues a command (a <c>command.queued</c> or <c>command.approved</c> record was applied, or recovery).</summary>
    public void Enqueue(Command command)
    {
        lock (_gate)
        {
            _queue.Enqueue(command);
        }

        Signal();
    }

    /// <summary>Wakes the loop: the queue, a lock or the connection state changed.</summary>
    public void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
            // Stopped.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        Task[] running;
        lock (_gate)
        {
            running = [.. _running];
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        _stop.Dispose();
        _signal.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? wake;
            try
            {
                wake = Pump(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CommandsLog.DispatchFailed(_s.Logger, ex, Connection.Value);
                wake = _s.Time.GetUtcNow() + TimeSpan.FromSeconds(1);
            }

            try
            {
                if (wake is { } at)
                {
                    var delay = at - _s.Time.GetUtcNow();
                    if (delay > TimeSpan.Zero)
                    {
                        await WaitSignalAsync(_s.Time, delay, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Waits for a signal or for <paramref name="delay"/> on <paramref name="time"/>, whichever comes first.</summary>
    private async Task WaitSignalAsync(TimeProvider time, TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signalled = _signal.WaitAsync(linked.Token);
        var elapsed = Task.Delay(delay, time, linked.Token);
        await Task.WhenAny(signalled, elapsed).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(signalled, elapsed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The other wait, cancelled.
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Starts what can start now; returns when to look again if nothing signals before (a <c>NotBefore</c> or an expiry).</summary>
    private DateTimeOffset? Pump(CancellationToken ct)
    {
        var now = _s.Time.GetUtcNow();
        var state = _s.Connections.State(Connection);
        var connected = state is ConnectionState.Connected or ConnectionState.Degraded;
        DateTimeOffset? wake = null;
        var starts = new List<Func<Task>>();
        lock (_gate)
        {
            if (!connected)
            {
                // CMD §7 steps 2–3 while the connection is down: expire, fail fast, or keep waiting until expiry.
                foreach (var command in _queue.ToList())
                {
                    if (command.ExpiresAt <= now)
                    {
                        _queue.Remove(command.Id);
                        starts.Add(() => FinishQueuedAsync(command, CommandRecordKinds.Expired, null, ct));
                    }
                    else if (_s.FailsFast(command, state))
                    {
                        _queue.Remove(command.Id);
                        var failure = DispatchServices.Failure(FailureCategory.ConnectionDown, CommandErrors.ConnectionDown(state.ToString()));
                        starts.Add(() => FinishQueuedAsync(command, CommandRecordKinds.Failed, failure, ct));
                    }
                    else
                    {
                        wake = Earliest(wake, command.ExpiresAt);
                    }
                }
            }
            else
            {
                Start(now, starts, ct);
                wake = NextNotBefore(now);
            }
        }

        // Started outside the lock: posted work may complete synchronously and call back into this dispatcher.
        foreach (var start in starts)
        {
            Track(Task.Run(start, CancellationToken.None));
        }

        return wake;
    }

    private void Start(DateTimeOffset now, List<Func<Task>> starts, CancellationToken ct)
    {
        bool Ready(Command c) => !_locks.IsLocked(c.Target) && (c.NotBefore is not { } notBefore || notBefore <= now);
        while (_inFlight < _s.Options.MaxInFlightPerConnection && _queue.TakeFirst(Ready) is { } next)
        {
            _locks.TryLock(next.Target);
            _inFlight++;

            // CMD §10: the queued children of the same group go in one batch (one in-flight slot) when the command is
            // batchable and the connector executes batches.
            if (next.GroupId is { } group && _s.Options.BatchSize > 1 && _s.Batchable(next) && _s.Channel.SupportsBatch(Connection))
            {
                var batch = new List<Command> { next };
                while (batch.Count < _s.Options.BatchSize
                    && _queue.TakeFirst(c => c.GroupId == group && c.Path == next.Path && Ready(c)) is { } mate)
                {
                    _locks.TryLock(mate.Target);
                    batch.Add(mate);
                }

                if (batch.Count > 1)
                {
                    starts.Add(() => DispatchBatchAsync(batch, ct));
                    continue;
                }
            }

            starts.Add(() => DispatchAsync(next, ct));
        }
    }

    private DateTimeOffset? NextNotBefore(DateTimeOffset now)
    {
        DateTimeOffset? wake = null;
        foreach (var waiting in _queue.ToList())
        {
            if (waiting.NotBefore is { } notBefore && notBefore > now)
            {
                wake = Earliest(wake, notBefore);
            }
        }

        return wake;
    }

    private static DateTimeOffset Earliest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } c && c <= candidate ? c : candidate;

    private void Track(Task task)
    {
        lock (_gate)
        {
            _running.Add(task);
        }

        _ = task.ContinueWith(
            t =>
            {
                lock (_gate)
                {
                    _running.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Expires or fails a command that never left the queue (still <c>Queued</c> in its partition).</summary>
    private async Task FinishQueuedAsync(Command queued, string kind, CommandFailure? failure, CancellationToken ct)
    {
        try
        {
            var partition = Partitioning.Of(queued.Target);
            await _s.Partitions.RunAsync(partition, emitter =>
            {
                if (_s.Processor.Find(partition, queued.Id) is { State: CommandState.Queued } command)
                {
                    emitter.Emit(kind == CommandRecordKinds.Expired
                        ? _s.Codec.Envelope(kind, command, new CommandByBody(command.Id, null), emitter.Now)
                        : _s.Codec.Envelope(kind, command, new CommandFailedBody(command.Id, failure!), emitter.Now));
                }

                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Node stopping: the command stays queued for the recovery (CMD §8).
        }
        catch (Exception ex)
        {
            CommandsLog.DispatchFailed(_s.Logger, ex, Connection.Value);
        }
    }

    /// <summary>CMD §7 steps 2 and 4–6 for one command whose device is locked for it.</summary>
    private async Task DispatchAsync(Command queued, CancellationToken ct)
    {
        try
        {
            var partition = Partitioning.Of(queued.Target);
            var start = await _s.Partitions.RunAsync(partition, emitter => Begin(partition, queued.Id, emitter), ct).ConfigureAwait(false);
            if (start is not { } dispatched)
            {
                return;
            }

            await RecordAsync(dispatched, await ExecuteAsync(dispatched, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Node stopping: a dispatched command without outcome is settled by the recovery (CMD §8).
        }
        catch (Exception ex)
        {
            CommandsLog.DispatchFailed(_s.Logger, ex, Connection.Value);
        }
        finally
        {
            lock (_gate)
            {
                _locks.Unlock(queued.Target);
                _inFlight--;
            }

            Signal();
        }
    }

    /// <summary>A batch of children of one group (CMD §10): each is begun on its partition, then executed in one call.</summary>
    private async Task DispatchBatchAsync(List<Command> batch, CancellationToken ct)
    {
        try
        {
            var begun = await Task.WhenAll(batch.Select(c =>
            {
                var partition = Partitioning.Of(c.Target);
                return _s.Partitions.RunAsync(partition, emitter => Begin(partition, c.Id, emitter), ct);
            })).ConfigureAwait(false);
            var started = begun.OfType<Dispatched>().ToList();
            if (started.Count == 0)
            {
                return;
            }

            var outcomes = await ExecuteBatchAsync(started, ct).ConfigureAwait(false);
            await Task.WhenAll(started.Select((dispatched, i) => RecordAsync(dispatched, outcomes[i], ct))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Node stopping: dispatched children without outcome are settled by the recovery (CMD §8).
        }
        catch (Exception ex)
        {
            CommandsLog.DispatchFailed(_s.Logger, ex, Connection.Value);
        }
        finally
        {
            lock (_gate)
            {
                batch.ForEach(c => _locks.Unlock(c.Target));
                _inFlight--;
            }

            Signal();
        }
    }

    /// <summary>Appends the outcome of a dispatched command on its partition.</summary>
    private Task<bool> RecordAsync(Dispatched dispatched, (string Kind, object Body) outcome, CancellationToken ct)
    {
        var partition = Partitioning.Of(dispatched.Command.Target);
        return _s.Partitions.RunAsync(partition, emitter =>
        {
            // A late outcome (the command left Dispatched meanwhile) is ignored by the state machine and counted.
            var current = _s.Processor.Find(partition, dispatched.Command.Id) ?? dispatched.Command;
            emitter.Emit(outcome.Body switch
            {
                CommandSucceededBody b => _s.Codec.Envelope(outcome.Kind, current, b, emitter.Now),
                CommandFailedBody b => _s.Codec.Envelope(outcome.Kind, current, b, emitter.Now),
                CommandIdBody b => _s.Codec.Envelope(outcome.Kind, current, b, emitter.Now),
                _ => throw new InvalidOperationException("Unknown outcome body."),
            });
            return true;
        }, ct);
    }

    /// <summary>One connector call for a batch; the deadline is the earliest of the children's.</summary>
    private async Task<IReadOnlyList<(string Kind, object Body)>> ExecuteBatchAsync(List<Dispatched> batch, CancellationToken ct)
    {
        var started = _s.Time.GetTimestamp();
        var requests = batch.Select(Request).ToList();
        var timeout = batch.Min(d => d.Command.Timeout);
        using var deadline = new CancellationTokenSource(timeout, _s.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct);
        IReadOnlyList<ConnectorCommandResult> results;
        try
        {
            var call = _s.Channel.ExecuteBatchAsync(Connection, requests, linked.Token).AsTask();
            ObserveLate(call);
            results = await call.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [.. batch.Select(d => (CommandRecordKinds.TimedOut, (object)new CommandIdBody(d.Command.Id)))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommandsLog.ConnectorThrew(_s.Logger, ex, batch[0].Command.Id.Value);
            return [.. batch.Select(d => ConnectorThrew(d.Command))];
        }

        if (results.Count != batch.Count)
        {
            CommandsLog.ConnectorThrew(_s.Logger, new InvalidOperationException($"{results.Count} results for {batch.Count} commands"), batch[0].Command.Id.Value);
            return [.. batch.Select(d => ConnectorThrew(d.Command))];
        }

        var duration = (long)_s.Time.GetElapsedTime(started).TotalMilliseconds;
        return [.. batch.Select((d, i) => Map(d.Command, results[i], duration))];
    }

    private ConnectorCommand Request(Dispatched dispatched)
    {
        var command = dispatched.Command;
        return new ConnectorCommand(command.Id.Value, dispatched.Handle, command.Path.Value, command.Arguments,
            dispatched.At + command.Timeout, command.Attempt, (ConnectorPriority)(int)command.Priority);
    }

    private static (string Kind, object Body) ConnectorThrew(Command command) =>
        (CommandRecordKinds.Failed, new CommandFailedBody(command.Id, DispatchServices.Failure(FailureCategory.InternalError, CommandErrors.ConnectorFailed())));

    /// <summary>On the partition: re-reads the command, then expires it, fails it (target gone) or appends <c>command.dispatched</c>.</summary>
    private Dispatched? Begin(int partition, CommandId id, ICommandEmitter emitter)
    {
        if (_s.Processor.Find(partition, id) is not { State: CommandState.Queued } command)
        {
            return null; // cancelled, expired or already dispatched meanwhile
        }

        var now = emitter.Now;
        if (command.ExpiresAt <= now)
        {
            emitter.Emit(_s.Codec.Envelope(CommandRecordKinds.Expired, command, new CommandByBody(command.Id, null), now));
            return null;
        }

        if (_s.Handle(command.Target) is not { } handle)
        {
            var failure = DispatchServices.Failure(FailureCategory.InvalidState, CommandErrors.TargetNotFound(command.Target));
            emitter.Emit(_s.Codec.Envelope(CommandRecordKinds.Failed, command, new CommandFailedBody(command.Id, failure), now));
            return null;
        }

        emitter.Emit(_s.Codec.Envelope(CommandRecordKinds.Dispatched, command, new CommandDispatchedBody(command.Id, command.Attempt, DispatchServices.Node), now));
        return new Dispatched(command, handle, now);
    }

    /// <summary>Calls the connector with the command's deadline and maps the result to the outcome record (CMD §7 step 5).</summary>
    private async Task<(string Kind, object Body)> ExecuteAsync(Dispatched dispatched, CancellationToken ct)
    {
        var command = dispatched.Command;
        var started = _s.Time.GetTimestamp();
        var request = Request(dispatched);
        using var deadline = new CancellationTokenSource(command.Timeout, _s.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct);
        ConnectorCommandResult result;
        try
        {
            var call = _s.Channel.ExecuteAsync(Connection, request, linked.Token).AsTask();
            ObserveLate(call);

            // The deadline also ends a connector call that ignores its token; its late result is dropped.
            result = await call.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (CommandRecordKinds.TimedOut, new CommandIdBody(command.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommandsLog.ConnectorThrew(_s.Logger, ex, command.Id.Value);
            return ConnectorThrew(command);
        }

        return Map(command, result, (long)_s.Time.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>CMD §7 step 5: a connector result as the outcome record.</summary>
    private (string Kind, object Body) Map(Command command, ConnectorCommandResult result, long duration) =>
        result.Outcome switch
        {
            CommandOutcome.Succeeded => (CommandRecordKinds.Succeeded, new CommandSucceededBody(command.Id, duration)),
            CommandOutcome.NotSupported => (CommandRecordKinds.Failed,
                new CommandFailedBody(command.Id, DispatchServices.Failure(FailureCategory.NotSupported, CommandErrors.NotSupported(command.Path.Value)))),
            _ when result.Failure == FailureCategory.Timeout => (CommandRecordKinds.TimedOut, new CommandIdBody(command.Id)),
            _ => (CommandRecordKinds.Failed, new CommandFailedBody(command.Id, ConnectorFailure(result))),
        };

    private CommandFailure ConnectorFailure(ConnectorCommandResult result)
    {
        var message = result.Message!;
        var key = _s.Connections.PackageOf(Connection) is { } package
            ? message.ToMessage(package).Key
            : "packages.connector." + message.Key;
        var args = message.Args.ToDictionary(a => a.Key, a => CommandRecordCodec.Text(a.Value), StringComparer.Ordinal);
        return new CommandFailure(result.Failure!.Value, key, args, result.VendorCode);
    }

    /// <summary>A connector call abandoned at its deadline may still fail later: observe it.</summary>
    private static void ObserveLate(Task call) =>
        _ = call.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private sealed record Dispatched(Command Command, DeviceHandle Handle, DateTimeOffset At);
}

/// <summary>What the dispatchers share: the partitions, the pending stores, the connector channel and the settings.</summary>
internal sealed class DispatchServices(
    CommandProcessor processor,
    ICommandPartitions partitions,
    CommandRecordCodec codec,
    IConnectionCommandChannel channel,
    ICommandConnections connections,
    IRegistryReader registry,
    Model.Contracts.ITypeCatalog types,
    CommandsOptions options,
    TimeProvider time,
    ILogger<CommandDispatch> logger)
{
    public CommandProcessor Processor => processor;

    public ICommandPartitions Partitions => partitions;

    public CommandRecordCodec Codec => codec;

    public IConnectionCommandChannel Channel => channel;

    public ICommandConnections Connections => connections;

    public IRegistryReader Registry => registry;

    public CommandsOptions Options => options;

    public TimeProvider Time => time;

    public ILogger Logger => logger;

    /// <summary>The node in <c>command.dispatched</c> records (M1–M8: one node, not recorded).</summary>
    public static NodeId? Node => null;

    /// <summary>
    /// Whether a command fails at once while its connection is down (CMD §7 step 3): <c>Commands:FailFastWhenDisconnected</c>
    /// applies to commands of people (users, federation users); automations, scripts and alarm actions wait until expiry.
    /// </summary>
    /// <remarks>
    /// A connection that is about to run (<see cref="ConnectionState.Configured"/> before the <c>Connections</c> start-up
    /// phase, <see cref="ConnectionState.Starting"/>) is not down: its commands wait, so commands requeued by the recovery
    /// (which runs before the connections start) are dispatched once the connection is up.
    /// </remarks>
    public bool FailsFast(Command command, ConnectionState state) =>
        options.FailFastWhenDisconnected
        && command.Issuer is CommandIssuer.User or CommandIssuer.Federation
        && state is not (ConnectionState.Configured or ConnectionState.Starting);

    /// <summary>Whether the command's definition is batchable (CMD §10).</summary>
    public bool Batchable(Command command) =>
        registry.GetDevice(command.Target).TryGetValue(out var device, out _)
        && types.Get(device.Type).TryGetValue(out var type, out _)
        && type.Commands.TryGetValue(command.Path, out var definition)
        && definition.Batchable;

    /// <summary>The connection of a device's server, or <see langword="null"/> when the device or its server is gone.</summary>
    public ConnectionId? ConnectionOf(DeviceId device) =>
        registry.GetDevice(device).TryGetValue(out var found, out _) && registry.GetServer(found.ServerId).TryGetValue(out var server, out _)
            ? server.ConnectionId
            : null;

    /// <summary>The connector's view of a device, or <see langword="null"/> when the device or its server is gone.</summary>
    public DeviceHandle? Handle(DeviceId device)
    {
        if (!registry.GetDevice(device).TryGetValue(out var found, out _) || !registry.GetServer(found.ServerId).TryGetValue(out var server, out _))
        {
            return null;
        }

        return new DeviceHandle(new DeviceKey(server.NativeId, found.NativeId), found.Type.Id.Value,
            found.Type.Version.ToString(), found.Attributes, [.. found.UnsupportedCaps.Select(c => c.Value)]);
    }

    /// <summary>A failure from a commands error: key <c>errors.&lt;code&gt;</c>, arguments as invariant-culture text.</summary>
    public static CommandFailure Failure(FailureCategory category, Error error)
    {
        var record = CommandRecordCodec.ToRecord(error);
        return new CommandFailure(category, "errors." + record.Code, record.Args, VendorCode: null);
    }
}
