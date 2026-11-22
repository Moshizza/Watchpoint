using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Commands.Contracts;

/// <summary>Requests, cancels, approves and reads commands (spec CMD §5).</summary>
public interface ICommandService
{
    /// <summary>
    /// Validates a request (CMD §5, in order) and records it in the target device's partition: <c>command.requested</c> and
    /// <c>command.queued</c> or <c>command.awaitingApproval</c> in one batch, or <c>command.rejected</c> when a check fails.
    /// Completes once the records are in the log and applied. A repeated <see cref="CommandRequest.ClientCommandId"/> of
    /// the same identity within the idempotency window returns the existing command and writes nothing.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The accepted command or group, or the rejection error.</returns>
    Task<Result<CommandAccepted>> RequestAsync(CommandRequest request, CancellationToken ct);

    /// <summary>Cancels a queued or awaiting command (<c>command.cancelled</c>); a dispatched or finished one is too late.</summary>
    /// <param name="id">The command.</param>
    /// <param name="actor">Who cancels.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, <c>commands.command.not_found</c> or <c>commands.cancel.too_late</c>.</returns>
    Task<Result> CancelAsync(CommandId id, CommandIssuer actor, CancellationToken ct);

    /// <summary>Approves a command awaiting approval (<c>command.approved</c>): it is queued.</summary>
    /// <param name="id">The command.</param>
    /// <param name="approver">The approver (not the requester).</param>
    /// <param name="session">The approver's session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, or <c>commands.command.not_found</c>, <c>commands.approval.not_pending</c>, <c>commands.approval.expired</c>, <c>commands.approval.self_forbidden</c>, <c>common.forbidden</c>.</returns>
    Task<Result> ApproveAsync(CommandId id, UserId approver, SessionId session, CancellationToken ct);

    /// <summary>Refuses a command awaiting approval (<c>command.approvalRejected</c>): it is rejected with the reason.</summary>
    /// <param name="id">The command.</param>
    /// <param name="approver">The approver (not the requester).</param>
    /// <param name="reason">Why (at most 500 characters).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, or the errors of <see cref="ApproveAsync"/>.</returns>
    Task<Result> RejectAsync(CommandId id, UserId approver, string reason, CancellationToken ct);

    /// <summary>Gets an active command, or a finished one for 24 h.</summary>
    /// <param name="id">The command.</param>
    /// <returns>The command, or <c>commands.command.not_found</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec CMD §5.")]
    Result<Command> Get(CommandId id);

    /// <summary>The commands of a device the issuer may run, with what running them requires.</summary>
    /// <param name="device">The device.</param>
    /// <param name="who">The issuer.</param>
    /// <returns>The commands, in path order; empty for an unknown device.</returns>
    IReadOnlyList<AvailableCommand> GetAvailable(DeviceId device, CommandIssuer who);
}
