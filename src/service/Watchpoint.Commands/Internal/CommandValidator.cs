using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>The result of validating one target: the command to record, or why it is rejected.</summary>
internal sealed record CommandValidation(Command? Command, bool RequiresApproval, Error? Error)
{
    public static CommandValidation Reject(Error error) => new(null, false, error);
}

/// <summary>What a group decided once for all its children (CMD §10): the children skip the reason and step-up checks.</summary>
internal sealed record GroupDecision(CommandId GroupId);

/// <summary>
/// The checks of a request for one target device, in the order of spec CMD §5 (steps 2–10; step 1, idempotency, is the
/// service's). The first failure rejects. Culture-invariant: arguments are JSON and validated by MOD.
/// </summary>
internal sealed class CommandValidator(
    IRegistryReader registry,
    ITypeCatalog types,
    IValueValidator values,
    ICommandAuthorizer authorizer,
    StepUpGuard stepUp,
    ICommandConnections connections,
    CommandsOptions options)
{
    /// <summary>The definition of a command on a device (steps 2–3).</summary>
    public Result<(Device Device, CommandDefinition Definition)> Resolve(DeviceId target, CommandPath path)
    {
        if (!registry.GetDevice(target).TryGetValue(out var device, out _))
        {
            return CommandErrors.TargetNotFound(target);
        }

        if (!device.Enabled)
        {
            return CommandErrors.TargetDisabled(target);
        }

        if (device.Orphaned)
        {
            return CommandErrors.TargetOrphaned(target);
        }

        if (!types.Get(device.Type).TryGetValue(out var type, out var typeError))
        {
            return typeError;
        }

        if (!type.Commands.TryGetValue(path, out var definition))
        {
            return Error.NotFound(ModelErrors.CommandNotFound.Value, ("path", path.Value), ("typeId", device.Type.Id.Value));
        }

        if (device.UnsupportedCaps.Contains(path.Capability))
        {
            return CommandErrors.NotSupported(path.Value);
        }

        return (device, definition);
    }

    public CommandValidation Validate(CommandRequest request, CommandId id, DeviceId target, DateTimeOffset now, GroupDecision? group)
    {
        // Steps 2–3: target and definition.
        if (!Resolve(target, request.Path).TryGetValue(out var resolved, out var error))
        {
            return CommandValidation.Reject(error);
        }

        var (device, definition) = resolved;

        // Step 4: arguments against the command's parameter schema (MOD), defaults applied; undeclared members are dropped,
        // so only declared parameters reach the log.
        if (!values.ValidateArguments(definition, request.Arguments).TryGetValue(out var arguments, out var argumentsError))
        {
            return CommandValidation.Reject(argumentsError with { FieldErrors = [.. argumentsError.FieldErrors.Select(f => f with { Path = "/arguments" + f.Path })] });
        }

        // Step 5: effective risk (site overrides by type, location and tag come with CFG in M3).
        var risk = definition.EffectiveRisk(arguments);

        // Step 6: authorisation and constraints.
        var decision = authorizer.Authorize(request.Issuer, device, definition, risk, arguments, now);
        if (!decision.Allowed)
        {
            return CommandValidation.Reject(decision.Denial!);
        }

        var constraints = decision.Constraints;
        if (request.Issuer is CommandIssuer.User user && constraints.AllowedClientKinds is { } kinds && !kinds.Contains(user.ClientKind))
        {
            return CommandValidation.Reject(CommandErrors.ClientKindNotAllowed(user.ClientKind.ToString()));
        }

        if (group is null)
        {
            // Step 7: reason.
            if (CheckReason(request.Reason, risk == Risk.Critical || constraints.ReasonRequired) is { } reasonError)
            {
                return CommandValidation.Reject(reasonError);
            }

            // Step 8: step-up, for interactive users.
            if (request.Issuer is CommandIssuer.User stepUser && RequiresStepUp(risk, constraints)
                && stepUp.Consume(request.StepUpToken, stepUser.SessionId, now) is { IsSuccess: false } stepUpFailure)
            {
                return CommandValidation.Reject(stepUpFailure.Error);
            }
        }
        else if (CheckReason(request.Reason, required: false) is { } lengthError)
        {
            return CommandValidation.Reject(lengthError);
        }

        // Step 9: a connection serves the device's server.
        if (!registry.GetServer(device.ServerId).TryGetValue(out var server, out _) || !connections.Exists(server.ConnectionId))
        {
            return CommandValidation.Reject(CommandErrors.ConnectionMissing(target));
        }

        // Step 10: NotBefore at most 24 h ahead.
        if (request.NotBefore is { } notBefore && notBefore > now + CommandsOptions.MaxNotBeforeAhead)
        {
            return CommandValidation.Reject(CommandErrors.NotBeforeTooFar((int)CommandsOptions.MaxNotBeforeAhead.TotalHours));
        }

        if (request.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            return CommandValidation.Reject(CommonErrors.ValidationFailed([CommonErrors.OutOfRangeAt("/timeout", 1, long.MaxValue)]));
        }

        var approval = constraints.ApprovalRequired;
        var start = request.NotBefore is { } nb && nb > now ? nb : now;
        var command = new Command
        {
            Id = id,
            ClientCommandId = request.ClientCommandId,
            GroupId = group?.GroupId,
            Target = target,
            Path = request.Path,
            Arguments = arguments,
            Issuer = request.Issuer,
            Reason = request.Reason,
            EffectiveRisk = risk,
            Priority = Priority(request, risk, constraints),
            Timeout = request.Timeout ?? definition.Timeout,
            NotBefore = request.NotBefore,
            ExpiresAt = start + options.DefaultExpiry + (approval ? options.ApprovalWindow : TimeSpan.Zero),
            Verification = definition.ExpectedEffect,
            Approval = approval ? new CommandApproval(null, null, now + options.ApprovalWindow) : null,
            Correlation = (request.Correlation ?? RecordCorrelation.None) with { Command = id },
            State = CommandState.Requested,
            RequestedAt = now,
        };
        return new CommandValidation(command, approval, null);
    }

    public bool RequiresStepUp(Risk risk, CommandConstraints constraints) =>
        (risk == Risk.Critical && options.StepUpForCritical) || constraints.StepUpRequired;

    /// <summary>The reason rule (step 7): required when asked, at most 500 characters.</summary>
    public static Error? CheckReason(string? reason, bool required)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return required ? CommandErrors.ReasonRequired() : null;
        }

        return reason.Length > CommandsOptions.MaxReasonLength
            ? CommonErrors.ValidationFailed([CommonErrors.TooLongAt("/reason", CommandsOptions.MaxReasonLength)])
            : null;
    }

    /// <summary>
    /// The priority (CMD §4): critical risk or a life-safety automation → critical; users → high; automations, scripts
    /// and alarm actions → normal. A requested priority may always lower it; raising to critical needs
    /// <c>commands.priority.critical</c> (<see cref="CommandConstraints.MayRaiseToCritical"/>) or a life-safety
    /// automation; any other raise is ignored (the default applies).
    /// </summary>
    public static CommandPriority Priority(CommandRequest request, Risk risk, CommandConstraints constraints)
    {
        var lifeSafety = request.Issuer is CommandIssuer.Automation { LifeSafety: true };
        var standard = risk == Risk.Critical || lifeSafety
            ? CommandPriority.Critical
            : request.Issuer is CommandIssuer.User or CommandIssuer.Federation ? CommandPriority.High : CommandPriority.Normal;
        if (request.Priority is not { } asked || asked >= standard)
        {
            return request.Priority ?? standard;
        }

        return asked == CommandPriority.Critical && (constraints.MayRaiseToCritical || lifeSafety) ? CommandPriority.Critical : standard;
    }
}
