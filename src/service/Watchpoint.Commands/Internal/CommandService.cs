using System.Collections.Concurrent;
using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The command service (spec CMD §5–§6). Requests are validated in the order of CMD §5, then recorded in the target's
/// partition by posted work, so every update of one command (request, cancel, approval, expiry, dispatch outcomes) is
/// serialised by the partition worker. Queues, dispatch and timeouts (T2.02), verification and group coordination
/// (T2.03) build on the records and the pending store.
/// </summary>
internal sealed class CommandService(
    CommandValidator validator,
    CommandProcessor processor,
    CommandDirectory directory,
    ICommandPartitions partitions,
    CommandRecordCodec codec,
    IRegistryReader registry,
    ITypeCatalog types,
    IValueValidator values,
    ICommandAuthorizer authorizer,
    StepUpGuard stepUp,
    TimeProvider time,
    CommandMetrics metrics) : ICommandService
{
    private readonly ConcurrentDictionary<CommandId, CommandAccepted> _groups = new();

    public async Task<Result<CommandAccepted>> RequestAsync(CommandRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Issuer);
        var now = time.UtcNowMs();
        if (request.ClientCommandId is { Length: > CommandsOptions.MaxClientCommandIdLength })
        {
            return CommonErrors.ValidationFailed([CommonErrors.TooLongAt("/clientCommandId", CommandsOptions.MaxClientCommandIdLength)]);
        }

        return request.Target switch
        {
            CommandTargetSpec.Device one => await RequestOneAsync(request, one.Id, null, now, ct).ConfigureAwait(false),
            CommandTargetSpec.Devices many => await RequestGroupAsync(request, many.Ids, now, ct).ConfigureAwait(false),
            CommandTargetSpec.Selector selector => await RequestGroupAsync(request, Select(selector.Query), now, ct).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown target.", nameof(request)),
        };
    }

    public async Task<Result> CancelAsync(CommandId id, CommandIssuer actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (directory.Find(id, time.GetUtcNow()) is not { } known)
        {
            return CommandErrors.CommandNotFound(id);
        }

        var partition = Partitioning.Of(known.Target);
        return await partitions.RunAsync(partition, emitter =>
        {
            if (processor.Find(partition, id) is not { State: CommandState.Queued or CommandState.AwaitingApproval } command)
            {
                return Result.Failure(CommandErrors.CancelTooLate(processor.Find(partition, id)?.State ?? known.State));
            }

            emitter.Emit(codec.Envelope(CommandRecordKinds.Cancelled, command, new CommandByBody(id, actor.Identity), emitter.Now));
            return Result.Success();
        }, ct).ConfigureAwait(false);
    }

    public Task<Result> ApproveAsync(CommandId id, UserId approver, SessionId session, CancellationToken ct) =>
        DecideAsync(id, approver, approve: true, reason: null, ct);

    public Task<Result> RejectAsync(CommandId id, UserId approver, string reason, CancellationToken ct)
    {
        if (CommandValidator.CheckReason(reason, required: true) is { } reasonError)
        {
            return Task.FromResult(Result.Failure(reasonError));
        }

        return DecideAsync(id, approver, approve: false, reason, ct);
    }

    public Result<Command> Get(CommandId id) =>
        directory.Find(id, time.GetUtcNow()) is { } command ? command : CommandErrors.CommandNotFound(id);

    public IReadOnlyList<AvailableCommand> GetAvailable(DeviceId device, CommandIssuer who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (!registry.GetDevice(device).TryGetValue(out var target, out _) || !target.Enabled || target.Orphaned
            || !types.Get(target.Type).TryGetValue(out var type, out _))
        {
            return [];
        }

        var now = time.GetUtcNow();
        var available = new List<AvailableCommand>();
        foreach (var definition in type.Commands.Values.OrderBy(d => d.Path.Value, StringComparer.Ordinal))
        {
            if (target.UnsupportedCaps.Contains(definition.Path.Capability))
            {
                continue;
            }

            var decision = authorizer.Authorize(who, target, definition, definition.Risk, new Dictionary<string, WpValue>(), now);
            if (!decision.Allowed
                || (who is CommandIssuer.User user && decision.Constraints.AllowedClientKinds is { } kinds && !kinds.Contains(user.ClientKind)))
            {
                continue;
            }

            var constraints = decision.Constraints;
            available.Add(new AvailableCommand(
                definition.Path,
                definition.Parameters,
                definition.Risk,
                RequiresReason: definition.Risk == Risk.Critical || constraints.ReasonRequired,
                RequiresStepUp: who is CommandIssuer.User && validator.RequiresStepUp(definition.Risk, constraints),
                RequiresApproval: constraints.ApprovalRequired,
                definition.Batchable));
        }

        return available;
    }

    private async Task<Result<CommandAccepted>> RequestOneAsync(CommandRequest request, DeviceId target, GroupDecision? group, DateTimeOffset now, CancellationToken ct)
    {
        var path = request.Path.Value;

        // Step 1: an idempotent repeat returns the existing command and writes nothing.
        var key = group is null ? request.ClientCommandId : null;
        if (key is not null && Duplicate(request.Issuer.Identity, key, now) is { } existing)
        {
            metrics.Request(path, "duplicate");
            return existing;
        }

        var id = CommandId.New();
        var validation = validator.Validate(request, id, target, now, group);
        var outcome = await partitions.RunAsync<Result<CommandAccepted>>(Partitioning.Of(target), emitter =>
        {
            if (validation.Command is not { } command)
            {
                var body = new CommandRejectedBody(id, CommandRecordCodec.ToRecord(validation.Error!), request.Path, request.Issuer, request.Reason, group?.GroupId);
                emitter.Emit(codec.Rejection(body, target, request.Correlation, now));
                return validation.Error!;
            }

            if (key is not null && directory.Reserve(request.Issuer.Identity, key, id, now) is { } holder)
            {
                return Accepted(holder);
            }

            // command.requested and the first state record in one batch (CMD §6).
            emitter.Emit(codec.Envelope(CommandRecordKinds.Requested, command, command, now));
            var state = validation.RequiresApproval ? CommandState.AwaitingApproval : CommandState.Queued;
            emitter.Emit(validation.RequiresApproval
                ? codec.Envelope(CommandRecordKinds.AwaitingApproval, command, new CommandAwaitingApprovalBody(id, command.Approval!.RequestExpiresAt), now)
                : codec.Envelope(CommandRecordKinds.Queued, command, new CommandQueuedBody(id, command.Priority), now));
            return new CommandAccepted(id, false, state, 1);
        }, ct).ConfigureAwait(false);

        metrics.Request(path, !outcome.IsSuccess ? "rejected" : outcome.Value.Id == id ? "accepted" : "duplicate");
        return outcome;
    }

    /// <summary>
    /// A group (CMD §10): the group's risk is one level above the highest child risk, and the reason and step-up are
    /// decided once for the group — a failure rejects the whole request and creates no child. Each child is then
    /// validated and recorded on its own (a refused child is rejected, the others continue). The <c>commandGroup.*</c>
    /// records, batching and progress are T2.03.
    /// </summary>
    private async Task<Result<CommandAccepted>> RequestGroupAsync(CommandRequest request, IReadOnlyList<DeviceId> targets, DateTimeOffset now, CancellationToken ct)
    {
        var path = request.Path.Value;
        var distinct = targets.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return CommonErrors.ValidationFailed([CommonErrors.RequiredAt("/target")]);
        }

        if (distinct.Count > CommandsOptions.MaxGroupSize)
        {
            metrics.Request(path, "rejected");
            return CommandErrors.GroupTooLarge(CommandsOptions.MaxGroupSize, distinct.Count);
        }

        var key = request.ClientCommandId;
        if (key is not null && directory.FindKey(request.Issuer.Identity, key, now) is { } knownGroup && _groups.TryGetValue(knownGroup, out var accepted))
        {
            metrics.Request(path, "duplicate");
            return accepted;
        }

        var highest = Risk.Normal;
        var constraints = CommandConstraints.None;
        foreach (var target in distinct)
        {
            if (validator.Resolve(target, request.Path).TryGetValue(out var resolved, out _)
                && values.ValidateArguments(resolved.Definition, request.Arguments).TryGetValue(out var arguments, out _))
            {
                var risk = resolved.Definition.EffectiveRisk(arguments);
                highest = risk > highest ? risk : highest;
                var decision = authorizer.Authorize(request.Issuer, resolved.Device, resolved.Definition, risk, arguments, now);
                if (decision.Allowed)
                {
                    constraints = constraints with
                    {
                        ReasonRequired = constraints.ReasonRequired || decision.Constraints.ReasonRequired,
                        StepUpRequired = constraints.StepUpRequired || decision.Constraints.StepUpRequired,
                    };
                }
            }
        }

        var groupRisk = highest == Risk.Normal ? Risk.Sensitive : Risk.Critical;
        if (CommandValidator.CheckReason(request.Reason, groupRisk == Risk.Critical || constraints.ReasonRequired) is { } reasonError)
        {
            metrics.Request(path, "rejected");
            return reasonError;
        }

        if (request.Issuer is CommandIssuer.User user && validator.RequiresStepUp(groupRisk, constraints)
            && stepUp.Consume(request.StepUpToken, user.SessionId, now) is { IsSuccess: false } stepUpFailure)
        {
            metrics.Request(path, "rejected");
            return stepUpFailure.Error;
        }

        var groupId = CommandId.New();
        if (key is not null && directory.Reserve(request.Issuer.Identity, key, groupId, now) is { } holder && _groups.TryGetValue(holder, out var raced))
        {
            return raced;
        }

        var decided = new GroupDecision(groupId);
        var state = CommandState.Rejected;
        foreach (var target in distinct)
        {
            var child = await RequestOneAsync(request with { ClientCommandId = null }, target, decided, now, ct).ConfigureAwait(false);
            if (child.IsSuccess && state != CommandState.Queued)
            {
                state = child.Value.State;
            }
        }

        var result = new CommandAccepted(groupId, true, state, distinct.Count);
        _groups[groupId] = result;
        return result;
    }

    private async Task<Result> DecideAsync(CommandId id, UserId approver, bool approve, string? reason, CancellationToken ct)
    {
        if (directory.Find(id, time.GetUtcNow()) is not { } known)
        {
            return CommandErrors.CommandNotFound(id);
        }

        var partition = Partitioning.Of(known.Target);
        return await partitions.RunAsync(partition, emitter =>
        {
            var command = processor.Find(partition, id);
            if (command is not { State: CommandState.AwaitingApproval, Approval: { } approval })
            {
                return Result.Failure(CommandErrors.ApprovalNotPending(command?.State ?? known.State));
            }

            if (command.Issuer.Identity == approver)
            {
                return CommandErrors.ApprovalSelfForbidden();
            }

            if (emitter.Now >= approval.RequestExpiresAt)
            {
                return CommandErrors.ApprovalExpired();
            }

            if (!authorizer.CanApprove(approver, command, emitter.Now))
            {
                return CommonErrors.Forbidden(PermissionCode.FromTrusted("commands.approve"));
            }

            var kind = approve ? CommandRecordKinds.Approved : CommandRecordKinds.ApprovalRejected;
            emitter.Emit(codec.Envelope(kind, command, new CommandApprovalBody(id, approver, reason), emitter.Now));
            return Result.Success();
        }, ct).ConfigureAwait(false);
    }

    private CommandAccepted? Duplicate(UserId identity, string key, DateTimeOffset now) =>
        directory.FindKey(identity, key, now) is { } existing ? Accepted(existing) : null;

    private CommandAccepted Accepted(CommandId existing) =>
        new(existing, false, directory.Find(existing, time.GetUtcNow())?.State ?? CommandState.Requested, 1);

    /// <summary>The devices a selector selects now (all pages). The issuer's visible scope is applied with T2.03 (RGT).</summary>
    private List<DeviceId> Select(DeviceQuery query)
    {
        var ids = new List<DeviceId>();
        string? cursor = null;
        do
        {
            var page = registry.QueryDevices(query, new PageRequest(1_000, cursor));
            ids.AddRange(page.Items.Select(d => d.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null && ids.Count <= CommandsOptions.MaxGroupSize);

        return ids;
    }
}
