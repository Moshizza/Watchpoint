using Microsoft.Extensions.Logging;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal.Verification;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// Applies the command records of each partition to its <see cref="PendingCommandStore"/> (spec CMD §6): the state machine,
/// approval expiry and the 60 s retention of finished commands. Every method runs on the worker of its partition.
/// </summary>
internal sealed class CommandProcessor
{
    private readonly PendingCommandStore[] _stores = new PendingCommandStore[Partitioning.PartitionCount];
    private readonly CommandDirectory _directory;
    private readonly CommandRecordCodec _codec;
    private readonly CommandWaiters _waiters;
    private readonly CommandMetrics _metrics;
    private readonly ILogger<CommandProcessor> _logger;
    private readonly ICommandStateView? _state;

    public CommandProcessor(CommandDirectory directory, CommandRecordCodec codec, CommandWaiters waiters, CommandMetrics metrics, ILogger<CommandProcessor> logger,
        ICommandStateView? state = null)
    {
        _state = state;
        _directory = directory;
        _codec = codec;
        _waiters = waiters;
        _metrics = metrics;
        _logger = logger;
        for (var i = 0; i < _stores.Length; i++)
        {
            _stores[i] = new PendingCommandStore();
        }
    }

    public PendingCommandStore Store(int partition) => _stores[partition];

    /// <summary>Receives the commands that become <c>Queued</c> outside a recovery replay (the dispatch, CMD §7).</summary>
    public Dispatch.ICommandQueueSink? Sink { get; set; }

    /// <summary>Receives every change of a group's child and refused children (the group coordinator, CMD §10).</summary>
    public Groups.ICommandGroupSink? GroupSink { get; set; }

    /// <summary>The verification watchers (CMD §9).</summary>
    public Verifier Verifier { get; } = new();

    /// <summary>The command in its partition's store (call on that partition's worker).</summary>
    public Command? Find(int partition, CommandId id) => _stores[partition].Find(id);

    /// <summary>
    /// Applies the records of a batch. <paramref name="emitter"/> takes the verification records (CMD §9); without one
    /// (or while recovering) watchers are registered but nothing is emitted: the deadline tick and the end of the recovery
    /// settle them.
    /// </summary>
    public void OnRecords(int partition, IReadOnlyList<LogRecord> records, bool recovering = false, ICommandEmitter? emitter = null)
    {
        var store = _stores[partition];
        var live = recovering ? null : emitter;
        foreach (var record in records)
        {
            store.Sequence = record.Sequence;
            try
            {
                if (record.Envelope.Kind.StartsWith("command.", StringComparison.Ordinal) && _codec.Registry.CanRead(record))
                {
                    var changed = Apply(store, record);
                    if (changed is { State: CommandState.Queued } queued && !recovering)
                    {
                        Sink?.Queued(queued);
                    }

                    if (changed is not null)
                    {
                        Track(partition, changed, record.Envelope.Ts, live);
                    }
                }
                else if (record.Envelope.Kind == RecordKinds.Property && live is not null && record.Envelope.Dev is { } device
                    && Verifier.Watches(partition, device) && _codec.Registry.CanRead(record))
                {
                    var body = record.BodyAs<PropertyRecordBody>(_codec.Registry);
                    foreach (var watch in Verifier.TakeMatching(partition, device, body.Path, body.Value))
                    {
                        EmitVerification(store, watch, CommandRecordKinds.Verified, body.Value, live);
                    }
                }
            }
            finally
            {
                // A caller waits for its record even if applying it failed (a bug, logged by the pipeline as 1641).
                _waiters.Complete(record.Envelope.Id);
            }
        }
    }

    /// <summary>Approval expiry, retention of finished commands, pruning of the node-wide view.</summary>
    public void OnTick(ICommandEmitter emitter)
    {
        var store = _stores[emitter.Partition];
        var now = emitter.Now;
        foreach (var command in store.Commands)
        {
            if (command is { State: CommandState.AwaitingApproval, Approval: { } approval } && approval.RequestExpiresAt <= now
                && store.Expiring.Add(command.Id))
            {
                var body = new CommandRejectedBody(command.Id, CommandRecordCodec.ToRecord(CommandErrors.ApprovalExpired()));
                emitter.Emit(_codec.Envelope(CommandRecordKinds.Rejected, command, body, now));
            }
        }

        foreach (var watch in Verifier.TakeExpired(emitter.Partition, now))
        {
            EmitVerification(store, watch, CommandRecordKinds.Unverified, _state?.Current(watch.Device, watch.Effect.Property), emitter);
        }

        foreach (var finished in store.FinishedBefore(now - CommandsOptions.TerminalRetention))
        {
            store.Remove(finished.Id);
            Verifier.Remove(emitter.Partition, finished.Id, finished.Target);
            _directory.Retire(finished, now);
        }

        _directory.Prune(now);
        GroupSink?.Prune(now - CommandsOptions.ReadCacheRetention);
    }

    /// <summary>Loads the commands of a snapshot (recovery, before the replay).</summary>
    public void Restore(int partition, IEnumerable<Command> commands, DateTimeOffset now)
    {
        var store = _stores[partition];
        foreach (var command in commands)
        {
            store.Put(command, command.VerifiedAt ?? command.CompletedAt ?? now);
            _directory.Publish(command);
            Remember(command);
            if (command is { State: CommandState.Succeeded, Verification: not null })
            {
                Verifier.Register(partition, command, command.CompletedAt ?? now);
            }
        }

        store.TakeChanges();
    }

    /// <summary>
    /// After a recovery (CMD §8–§9): the effect of a watched command may have become visible while the node was down or
    /// during the replay, when no verification record is emitted; checks every watcher against the current state.
    /// </summary>
    public void CheckWatchers(ICommandEmitter emitter)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        var store = _stores[emitter.Partition];
        foreach (var watch in Verifier.All(emitter.Partition))
        {
            var current = _state?.Current(watch.Device, watch.Effect.Property);
            if (Verifier.Matches(watch.Effect, current))
            {
                Verifier.Remove(emitter.Partition, watch.Id, watch.Device);
                EmitVerification(store, watch, CommandRecordKinds.Verified, current, emitter);
            }
        }
    }

    /// <summary>Verification watchers and group progress after a command changed (CMD §9–§10).</summary>
    private void Track(int partition, Command changed, DateTimeOffset at, ICommandEmitter? live)
    {
        if (changed is { State: CommandState.Succeeded, Verification: not null })
        {
            var watch = Verifier.Register(partition, changed, at)!;

            // The effect may already be visible before the result arrived.
            var current = live is null ? null : _state?.Current(changed.Target, watch.Effect.Property);
            if (live is not null && Verifier.Matches(watch.Effect, current))
            {
                Verifier.Remove(partition, changed.Id, changed.Target);
                EmitVerification(_stores[partition], watch, CommandRecordKinds.Verified, current, live);
            }
        }
        else if (CommandStateMachine.IsTerminal(changed))
        {
            Verifier.Remove(partition, changed.Id, changed.Target);
        }

        if (changed.GroupId is not null)
        {
            GroupSink?.Changed(changed);
        }
    }

    private void EmitVerification(PendingCommandStore store, Verifier.Watch watch, string kind, Kernel.Values.WpValue? observed, ICommandEmitter emitter)
    {
        if (store.Find(watch.Id) is not { State: CommandState.Succeeded } command)
        {
            return;
        }

        var after = (long)Math.Max(0, (emitter.Now - watch.SucceededAt).TotalMilliseconds);
        emitter.Emit(_codec.Envelope(kind, command, new CommandVerificationBody(command.Id, observed, after), emitter.Now));
    }

    /// <summary>Applies one record; returns the command it changed, if any.</summary>
    private Command? Apply(PendingCommandStore store, LogRecord record)
    {
        var envelope = record.Envelope;
        var kind = envelope.Kind;
        var at = envelope.Ts;
        if (kind == CommandRecordKinds.Requested)
        {
            var requested = _codec.Body<Command>(record);
            if (store.Find(requested.Id) is not null)
            {
                return null; // the same record again (replay)
            }

            store.Put(requested, at);
            _directory.Publish(requested);
            Remember(requested);
            Log(requested);
            return null;
        }

        var id = envelope.Corr.Command;
        var command = id is { } commandId ? store.Find(commandId) : null;
        if (command is null)
        {
            if (kind == CommandRecordKinds.Rejected && _codec.Body<CommandRejectedBody>(record) is { Path: { } path } refused)
            {
                // A request refused by validation: there is no command to update.
                CommandsLog.Transition(_logger, refused.Id.Value, path.Value, envelope.Dev?.Value ?? Guid.Empty, new IssuerText(refused.Issuer), CommandState.Rejected);
                if (refused.GroupId is { } group)
                {
                    GroupSink?.ChildRefused(group, refused.Id, envelope.Dev);
                }

                return null;
            }

            Ignore(kind, id?.Value ?? Guid.Empty, "unknown");
            return null;
        }

        if (CommandStateMachine.Next(command, kind) is not { } next)
        {
            if (kind != CommandRecordKinds.Requested)
            {
                Ignore(kind, command.Id.Value, command.State.ToString());
            }

            return null;
        }

        var updated = Update(command, next, record) with { State = next };
        store.Expiring.Remove(updated.Id);
        store.Put(updated, at);
        _directory.Publish(updated);
        Log(updated);
        if (CommandStateMachine.IsTerminal(updated))
        {
            _metrics.Finished(updated.Path.Value, updated.State, at - updated.RequestedAt);
        }

        return updated;
    }

    private Command Update(Command command, CommandState next, LogRecord record)
    {
        var at = record.Envelope.Ts;
        switch (record.Envelope.Kind)
        {
            case CommandRecordKinds.Rejected:
                return command with { CompletedAt = at, Failure = CommandRecordCodec.ToFailure(_codec.Body<CommandRejectedBody>(record).Error) };
            case CommandRecordKinds.AwaitingApproval:
                var waiting = _codec.Body<CommandAwaitingApprovalBody>(record);
                return command with { Approval = new CommandApproval(null, null, waiting.RequestExpiresAt) };
            case CommandRecordKinds.Approved:
                var approved = _codec.Body<CommandApprovalBody>(record);
                return command with { Approval = Decided(command, approved.By, at), QueuedAt = at };
            case CommandRecordKinds.ApprovalRejected:
                var refused = _codec.Body<CommandApprovalBody>(record);
                var args = new Dictionary<string, string?>(StringComparer.Ordinal) { ["reason"] = refused.Reason };
                return command with
                {
                    Approval = Decided(command, refused.By, at),
                    CompletedAt = at,
                    Failure = new CommandFailure(FailureCategory.RefusedBySystem, "errors.commands.approval.rejected", args, null),
                };
            case CommandRecordKinds.Queued:
                var queued = _codec.Body<CommandQueuedBody>(record);
                return command with { QueuedAt = at, Priority = queued.Priority, Attempt = queued.Attempt };
            case CommandRecordKinds.Dispatched:
                return command with { DispatchedAt = at, Attempt = _codec.Body<CommandDispatchedBody>(record).Attempt };
            case CommandRecordKinds.Succeeded:
                return command with { CompletedAt = at };
            case CommandRecordKinds.Failed:
                var failed = _codec.Body<CommandFailedBody>(record);
                CommandsLog.Failed(_logger, command.Id.Value, failed.Failure.Category.ToString(), failed.Failure.VendorCode);
                return command with { CompletedAt = at, Failure = failed.Failure };
            case CommandRecordKinds.TimedOut:
                CommandsLog.TimedOut(_logger, command.Id.Value, (long)command.Timeout.TotalMilliseconds);
                return command with { CompletedAt = at };
            case CommandRecordKinds.Expired:
            case CommandRecordKinds.Cancelled:
                return command with { CompletedAt = at };
            case CommandRecordKinds.Verified:
                _metrics.Verification("verified");
                return command with { VerifiedAt = at };
            case CommandRecordKinds.Unverified:
                _metrics.Verification("unverified");
                CommandsLog.NotVerified(_logger, command.Id.Value);
                return command with { VerifiedAt = at };
            default:
                throw new InvalidOperationException($"No update for {record.Envelope.Kind} to {next}.");
        }
    }

    private static CommandApproval Decided(Command command, UserId by, DateTimeOffset at) =>
        new(by, at, command.Approval?.RequestExpiresAt ?? at);

    private void Remember(Command command)
    {
        if (command.ClientCommandId is { } key)
        {
            _directory.Reserve(command.Issuer.Identity, key, command.Id, command.RequestedAt);
        }
    }

    private void Ignore(string kind, Guid commandId, string state)
    {
        _metrics.Ignored(kind);
        CommandsLog.IgnoredTransition(_logger, kind, commandId, state);
    }

    private void Log(Command command) =>
        CommandsLog.Transition(_logger, command.Id.Value, command.Path.Value, command.Target.Value, new IssuerText(command.Issuer), command.State);

}

/// <summary>An issuer in log messages: <c>user:&lt;id&gt;</c>, <c>automation:&lt;id&gt;</c>… (formatted only when logged).</summary>
internal readonly record struct IssuerText(CommandIssuer? Issuer)
{
    public override string ToString() => Issuer switch
    {
        CommandIssuer.User u => "user:" + u.UserId.Value,
        CommandIssuer.Automation a => "automation:" + a.AutomationId.Value,
        CommandIssuer.Script s => "script:" + s.ScriptId.Value,
        CommandIssuer.AlarmAction a => "alarm:" + a.AlarmId.Value,
        CommandIssuer.Federation f => "federation:" + f.LinkId.Value,
        _ => "system",
    };
}
