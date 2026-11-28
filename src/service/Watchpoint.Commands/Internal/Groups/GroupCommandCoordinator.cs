using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal.Groups;

/// <summary>Receives the changes of group children from the partition workers (must not block).</summary>
internal interface ICommandGroupSink
{
    /// <summary>A child changed state.</summary>
    void Changed(Command child);

    /// <summary>A child was refused by validation (no <c>command.requested</c>).</summary>
    void ChildRefused(CommandId group, CommandId child, DeviceId? target);

    /// <summary>Forgets the groups completed before <paramref name="before"/>.</summary>
    void Prune(DateTimeOffset before);
}

/// <summary>Appends records to <c>WP_SYS</c> (group records, CMD §10).</summary>
internal interface ICommandSysRecords
{
    ValueTask AppendAsync(LogEnvelope record, CancellationToken ct);
}

/// <summary>The production <see cref="ICommandSysRecords"/>: the event log writer.</summary>
internal sealed class LogSysRecords(IEventLogWriter log) : ICommandSysRecords
{
    public async ValueTask AppendAsync(LogEnvelope record, CancellationToken ct) =>
        await log.AppendGlobalAsync(LogStream.Sys, record, ct).ConfigureAwait(false);
}

/// <summary>What a group needs to be shown and retried: the request as given, without the step-up proof.</summary>
/// <param name="Id">The group id.</param>
/// <param name="Request">The request (step-up proof and idempotency key removed).</param>
/// <param name="Targets">The resolved targets, in order.</param>
/// <param name="RequestedAt">When it was requested.</param>
/// <param name="RetryOf">The group it retries.</param>
internal sealed record GroupRequest(CommandId Id, CommandRequest Request, IReadOnlyList<DeviceId> Targets, DateTimeOffset RequestedAt, CommandId? RetryOf);

/// <summary>
/// The counters of the groups (spec CMD §10): fed by the processor with every change of a child, publishes
/// <c>commandGroup.progress</c> at most every 500 ms per group and <c>commandGroup.completed</c> once every child is
/// terminal (log 2430). Groups are kept in memory for the read cache's 24 h after completion.
/// </summary>
internal sealed class GroupCommandCoordinator : ICommandGroupSink, IAsyncDisposable
{
    /// <summary>The shortest interval between two progress records of a group.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<CommandId, GroupState> _groups = new();
    private readonly ConcurrentDictionary<Task, byte> _appends = new();
    private readonly ICommandSysRecords _sys;
    private readonly ILogRecordTypeRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<GroupCommandCoordinator> _logger;
    private readonly CancellationTokenSource _stop = new();

    public GroupCommandCoordinator(ICommandSysRecords sys, ILogRecordTypeRegistry registry, TimeProvider time, ILogger<GroupCommandCoordinator> logger)
    {
        _sys = sys;
        _registry = registry;
        _time = time;
        _logger = logger;
        CommandRecordCodec.RegisterGroups(registry);
    }

    /// <summary>Records the group (<c>commandGroup.requested</c>, awaited) before its children are requested.</summary>
    public async Task BeginAsync(GroupRequest group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        _groups[group.Id] = new GroupState(group);
        var request = group.Request;
        var body = new CommandGroupRequestedBody(group.Id, request.Path, request.Arguments, request.Issuer, group.Targets.Count, group.Targets, group.RetryOf);
        await _sys.AppendAsync(Envelope(CommandGroupRecordKinds.Requested, group.Request, group.Id, body, group.RequestedAt), ct).ConfigureAwait(false);
    }

    /// <summary>A group, if it is known (in memory since its request).</summary>
    public GroupRequest? Find(CommandId id) => _groups.TryGetValue(id, out var group) ? group.Request : null;

    /// <summary>The group with its counters and children.</summary>
    public CommandGroup? View(CommandId id)
    {
        if (!_groups.TryGetValue(id, out var group))
        {
            return null;
        }

        lock (group)
        {
            var request = group.Request;
            return new CommandGroup(id, request.Request.Path, request.Request.Issuer, request.RequestedAt, group.Counters(),
                [.. request.Targets.Select(t => group.Children.TryGetValue(t, out var c) ? new CommandGroupChild(t, c.Id, c.State) : new CommandGroupChild(t, null, CommandState.Requested))],
                request.RetryOf, group.CompletedAt);
        }
    }

    /// <summary>The targets whose child failed, timed out or expired (what "retry failed" sends again).</summary>
    public IReadOnlyList<DeviceId> FailedTargets(CommandId id)
    {
        if (!_groups.TryGetValue(id, out var group))
        {
            return [];
        }

        lock (group)
        {
            return [.. group.Request.Targets.Where(t => group.Children.TryGetValue(t, out var c)
                && c.State is CommandState.Failed or CommandState.TimedOut or CommandState.Expired)];
        }
    }

    public void Changed(Command child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.GroupId is { } id && _groups.TryGetValue(id, out var group))
        {
            Update(group, child.Target, new Child(child.Id, child.State, CommandStateMachine.IsTerminal(child)));
        }
    }

    public void ChildRefused(CommandId group, CommandId child, DeviceId? target)
    {
        if (target is { } device && _groups.TryGetValue(group, out var state))
        {
            Update(state, device, new Child(child, CommandState.Rejected, Terminal: true));
        }
    }

    /// <summary>Forgets the groups completed before <paramref name="before"/>.</summary>
    public void Prune(DateTimeOffset before)
    {
        foreach (var (id, group) in _groups)
        {
            if (group.CompletedAt is { } done && done <= before)
            {
                _groups.TryRemove(id, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var group in _groups.Values)
        {
            lock (group)
            {
                group.Timer?.Dispose();
            }
        }

        await Task.WhenAll(_appends.Keys).ConfigureAwait(false);
        _stop.Dispose();
    }

    private void Update(GroupState group, DeviceId target, Child child)
    {
        lock (group)
        {
            if (group.CompletedAt is not null || (group.Children.TryGetValue(target, out var previous) && previous.Terminal))
            {
                return;
            }

            group.Children[target] = child;
            group.Dirty = true;
            if (group.Children.Count == group.Request.Targets.Count && group.Children.Values.All(c => c.Terminal))
            {
                Complete(group);
                return;
            }

            var now = _time.GetUtcNow();
            if (group.LastProgress is not { } last || now - last >= ProgressInterval)
            {
                Publish(group, CommandGroupRecordKinds.Progress, now);
            }
            else
            {
                group.Timer ??= _time.CreateTimer(_ => Flush(group), null, last + ProgressInterval - now, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Flush(GroupState group)
    {
        lock (group)
        {
            group.Timer?.Dispose();
            group.Timer = null;
            if (group.Dirty && group.CompletedAt is null)
            {
                Publish(group, CommandGroupRecordKinds.Progress, _time.GetUtcNow());
            }
        }
    }

    private void Complete(GroupState group)
    {
        var now = _time.GetUtcNow();
        group.Timer?.Dispose();
        group.Timer = null;
        group.CompletedAt = now;
        var counters = group.Counters();
        Publish(group, CommandGroupRecordKinds.Completed, now);
        CommandsLog.GroupCompleted(_logger, group.Request.Id.Value, counters);
    }

    /// <summary>Appends a progress or completion record (in the background; called under the group's lock).</summary>
    private void Publish(GroupState group, string kind, DateTimeOffset now)
    {
        group.LastProgress = now;
        group.Dirty = false;
        var envelope = Envelope(kind, group.Request.Request, group.Request.Id, new CommandGroupProgressBody(group.Request.Id, group.Counters()), now);
        var previous = group.LastAppend;
        var append = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false); // in order per group
                await _sys.AppendAsync(envelope, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Node stopping.
            }
            catch (Exception ex)
            {
                CommandsLog.GroupRecordFailed(_logger, ex, group.Request.Id.Value);
            }
        });
        group.LastAppend = append;
        _appends.TryAdd(append, 0);
        _ = append.ContinueWith(t => _appends.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private LogEnvelope Envelope<TBody>(string kind, CommandRequest request, CommandId group, TBody body, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now), kind, now, Src: null, P: 0, Dev: null, Srv: null, CommandRecordCodec.Origin(request.Issuer),
            (request.Correlation ?? RecordCorrelation.None) with { Command = group }, _registry.SerializeBody(kind, body));

    private readonly record struct Child(CommandId Id, CommandState State, bool Terminal);

    private sealed class GroupState(GroupRequest request)
    {
        public GroupRequest Request { get; } = request;

        public Dictionary<DeviceId, Child> Children { get; } = [];

        public DateTimeOffset? LastProgress { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public bool Dirty { get; set; }

        public ITimer? Timer { get; set; }

        public Task LastAppend { get; set; } = Task.CompletedTask;

        public CommandGroupCounters Counters()
        {
            int succeeded = 0, failed = 0, timedOut = 0, verified = 0, unverified = 0, rejected = 0, terminal = 0;
            foreach (var child in Children.Values)
            {
                terminal += child.Terminal ? 1 : 0;
                switch (child.State)
                {
                    case CommandState.Succeeded:
                        succeeded++;
                        break;
                    case CommandState.Verified:
                        succeeded++;
                        verified++;
                        break;
                    case CommandState.Unverified:
                        succeeded++;
                        unverified++;
                        break;
                    case CommandState.Failed or CommandState.Expired or CommandState.Cancelled:
                        failed++;
                        break;
                    case CommandState.TimedOut:
                        timedOut++;
                        break;
                    case CommandState.Rejected:
                        rejected++;
                        break;
                }
            }

            var total = Request.Targets.Count;
            return new CommandGroupCounters(total, succeeded, failed, timedOut, verified, unverified, rejected, total - terminal);
        }
    }
}
