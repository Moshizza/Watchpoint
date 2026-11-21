using Microsoft.Extensions.Logging;
using Watchpoint.Commands.Contracts;
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

    public CommandProcessor(CommandDirectory directory, CommandRecordCodec codec, CommandWaiters waiters, CommandMetrics metrics, ILogger<CommandProcessor> logger)
    {
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

    /// <summary>The command in its partition's store (call on that partition's worker).</summary>
    public Command? Find(int partition, CommandId id) => _stores[partition].Find(id);

    public void OnRecords(int partition, IReadOnlyList<LogRecord> records)
    {
        var store = _stores[partition];
        foreach (var record in records)
        {
            store.Sequence = record.Sequence;
            if (record.Envelope.Kind.StartsWith("command.", StringComparison.Ordinal) && _codec.Registry.CanRead(record))
            {
                Apply(store, record);
            }

            _waiters.Complete(record.Envelope.Id);
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

        foreach (var finished in store.FinishedBefore(now - CommandsOptions.TerminalRetention))
        {
            store.Remove(finished.Id);
            _directory.Retire(finished, now);
        }

        _directory.Prune(now);
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
        }

        store.TakeChanges();
    }

    private void Apply(PendingCommandStore store, LogRecord record)
    {
        var envelope = record.Envelope;
        var kind = envelope.Kind;
        var at = envelope.Ts;
        if (kind == CommandRecordKinds.Requested)
        {
            var requested = _codec.Body<Command>(record);
            if (store.Find(requested.Id) is not null)
            {
                return; // the same record again (replay)
            }

            store.Put(requested, at);
            _directory.Publish(requested);
            Remember(requested);
            Log(requested);
            return;
        }

        var id = envelope.Corr.Command;
        var command = id is { } commandId ? store.Find(commandId) : null;
        if (command is null)
        {
            if (kind == CommandRecordKinds.Rejected && _codec.Body<CommandRejectedBody>(record) is { Path: { } path } refused)
            {
                // A request refused by validation: there is no command to update.
                CommandsLog.Transition(_logger, refused.Id.Value, path.Value, envelope.Dev?.Value ?? Guid.Empty, new IssuerText(refused.Issuer), CommandState.Rejected);

                return;
            }

            Ignore(kind, id?.Value ?? Guid.Empty, "unknown");
            return;
        }

        if (CommandStateMachine.Next(command, kind) is not { } next)
        {
            if (kind != CommandRecordKinds.Requested)
            {
                Ignore(kind, command.Id.Value, command.State.ToString());
            }

            return;
        }

        var updated = Update(command, next, record) with { State = next };
        store.Expiring.Remove(updated.Id);
        store.Put(updated, at);
        _directory.Publish(updated);
        Log(updated);
        if (CommandStateMachine.IsTerminal(updated))
        {
            _metrics.Finished(updated.Path.Value, at - updated.RequestedAt);
        }
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
