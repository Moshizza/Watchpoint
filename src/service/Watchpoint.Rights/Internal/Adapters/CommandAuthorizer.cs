using Watchpoint.Commands.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Adapters;

/// <summary>The CMD authorizer backed by the rights evaluator (spec RGT §6.1): scripts and automations are evaluated like users.</summary>
internal sealed class CommandAuthorizer(IRightsEvaluator evaluator, TimeProvider time) : ICommandAuthorizer
{
    private static readonly PermissionCode Approve = PermissionCode.FromTrusted("commands.approve");
    private static readonly PermissionCode Critical = PermissionCode.FromTrusted("commands.priority.critical");

    public CommandAuthorization Authorize(CommandIssuer issuer, Device device, CommandDefinition command, Risk effectiveRisk,
        IReadOnlyDictionary<string, WpValue> arguments, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(command);
        var permission = PermissionCode.FromTrusted("commands." + command.Path.Value);
        ClientKind? client = issuer is CommandIssuer.User u ? u.ClientKind : null;
        var decision = evaluator.Authorize(issuer.Identity, permission, new AuthorizationTarget.Device(device.Id), now, client);
        if (!decision.Allowed)
        {
            return CommandAuthorization.Deny(decision.DenialCode switch
            {
                RightsErrors.OutsideScheduleCode => CommandErrors.OutsideSchedule(),
                RightsErrors.ClientKindCode => Error.Forbidden("commands.client_kind_not_allowed"),
                _ => CommonErrors.Forbidden(permission),
            });
        }

        var c = decision.EffectiveConstraints;
        return CommandAuthorization.Allow(new CommandConstraints(c.RequireReason, c.RequireStepUp, c.RequireFourEyes, c.ClientKinds,
            evaluator.HasPermission(issuer.Identity, Critical, now)));
    }

    public bool CanApprove(UserId approver, Command command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        return evaluator.Authorize(approver, Approve, new AuthorizationTarget.Device(command.Target), now).Allowed;
    }

    public IReadOnlySet<DeviceId>? VisibleDevices(CommandIssuer issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        var scope = evaluator.GetVisibleScope(issuer.Identity, PermissionCode.FromTrusted("operate.devices.view"), time.GetUtcNow());
        return scope.Everything ? null : scope.Devices;
    }
}
