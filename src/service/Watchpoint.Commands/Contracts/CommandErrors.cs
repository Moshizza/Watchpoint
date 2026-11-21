using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Commands.Contracts;

/// <summary>The error codes of the commands module (spec CMD §13).</summary>
public static class CommandErrors
{
    /// <summary><c>commands.target.not_found</c>: no such device.</summary>
    /// <param name="id">The device.</param>
    /// <returns>The error.</returns>
    public static Error TargetNotFound(DeviceId id) => Error.NotFound("commands.target.not_found", ("deviceId", id.Value));

    /// <summary><c>commands.target.disabled</c>: the device is disabled.</summary>
    /// <param name="id">The device.</param>
    /// <returns>The error.</returns>
    public static Error TargetDisabled(DeviceId id) => Error.Unprocessable("commands.target.disabled", ("deviceId", id.Value));

    /// <summary><c>commands.target.orphaned</c>: the device is no longer reported by its system.</summary>
    /// <param name="id">The device.</param>
    /// <returns>The error.</returns>
    public static Error TargetOrphaned(DeviceId id) => Error.Unprocessable("commands.target.orphaned", ("deviceId", id.Value));

    /// <summary><c>commands.not_supported</c>: the command's capability is unsupported by this device.</summary>
    /// <param name="path">The command path.</param>
    /// <returns>The error.</returns>
    public static Error NotSupported(string path) => Error.Unprocessable("commands.not_supported", ("path", path));

    /// <summary><c>commands.client_kind_not_allowed</c>: the user's client may not run the command.</summary>
    /// <param name="clientKind">The client kind.</param>
    /// <returns>The error.</returns>
    public static Error ClientKindNotAllowed(string clientKind) => Error.Forbidden("commands.client_kind_not_allowed", ("clientKind", clientKind));

    /// <summary><c>commands.outside_schedule</c>: the grant does not apply now (returned by the authorizer).</summary>
    /// <returns>The error.</returns>
    public static Error OutsideSchedule() => Error.Forbidden("commands.outside_schedule");

    /// <summary><c>commands.reason.required</c>.</summary>
    /// <returns>The error.</returns>
    public static Error ReasonRequired() => Error.Validation("commands.reason.required", [FieldError.Create("/reason", ErrorCode.FromTrusted("commands.reason.required"))]);

    /// <summary><c>commands.connection.missing</c>: the device's server has no connection.</summary>
    /// <param name="id">The device.</param>
    /// <returns>The error.</returns>
    public static Error ConnectionMissing(DeviceId id) => Error.Unprocessable("commands.connection.missing", ("deviceId", id.Value));

    /// <summary><c>commands.not_before.too_far</c>.</summary>
    /// <param name="maxHours">The limit in hours.</param>
    /// <returns>The error.</returns>
    public static Error NotBeforeTooFar(int maxHours) => Error.Create(ErrorKind.Validation, "commands.not_before.too_far", ("maxHours", maxHours));

    /// <summary><c>commands.group.too_large</c>.</summary>
    /// <param name="max">The limit.</param>
    /// <param name="count">The number of targets.</param>
    /// <returns>The error.</returns>
    public static Error GroupTooLarge(int max, int count) => Error.Create(ErrorKind.Validation, "commands.group.too_large", ("max", max), ("count", count));

    /// <summary><c>commands.cancel.too_late</c>: the command is dispatched or finished.</summary>
    /// <param name="state">Its state.</param>
    /// <returns>The error.</returns>
    public static Error CancelTooLate(CommandState state) => Error.Conflict("commands.cancel.too_late", ("state", state.ToString()));

    /// <summary><c>commands.approval.self_forbidden</c>: the requester cannot approve their own command.</summary>
    /// <returns>The error.</returns>
    public static Error ApprovalSelfForbidden() => Error.Forbidden("commands.approval.self_forbidden");

    /// <summary><c>commands.approval.expired</c>: the approval window passed.</summary>
    /// <returns>The error.</returns>
    public static Error ApprovalExpired() => Error.Conflict("commands.approval.expired");

    /// <summary><c>commands.approval.not_pending</c>: the command is not awaiting approval.</summary>
    /// <param name="state">Its state.</param>
    /// <returns>The error.</returns>
    public static Error ApprovalNotPending(CommandState state) => Error.Conflict("commands.approval.not_pending", ("state", state.ToString()));

    /// <summary><c>commands.command.not_found</c>: no active command and none finished in the last 24 h.</summary>
    /// <param name="id">The command.</param>
    /// <returns>The error.</returns>
    public static Error CommandNotFound(CommandId id) => Error.NotFound("commands.command.not_found", ("commandId", id.Value));
}
