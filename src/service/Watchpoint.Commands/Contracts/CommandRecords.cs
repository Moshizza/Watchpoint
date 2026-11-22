using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Commands.Contracts;

/// <summary>
/// The kinds of the command lifecycle records (spec CMD §6), appended to the target device's partition of <c>WP_LOG</c>
/// with <c>dev</c> = the target and <c>corr.command</c> = the command id. Schema version 1.
/// </summary>
public static class CommandRecordKinds
{
    /// <summary>Body: the full <see cref="Command"/> at request time.</summary>
    public const string Requested = "command.requested";

    /// <summary>Body: <see cref="CommandRejectedBody"/>.</summary>
    public const string Rejected = "command.rejected";

    /// <summary>Body: <see cref="CommandAwaitingApprovalBody"/>.</summary>
    public const string AwaitingApproval = "command.awaitingApproval";

    /// <summary>Body: <see cref="CommandApprovalBody"/>; the command is queued.</summary>
    public const string Approved = "command.approved";

    /// <summary>Body: <see cref="CommandApprovalBody"/>; the command is rejected.</summary>
    public const string ApprovalRejected = "command.approvalRejected";

    /// <summary>Body: <see cref="CommandQueuedBody"/>.</summary>
    public const string Queued = "command.queued";

    /// <summary>Body: <see cref="CommandDispatchedBody"/> (T2.02).</summary>
    public const string Dispatched = "command.dispatched";

    /// <summary>Body: <see cref="CommandSucceededBody"/> (T2.02).</summary>
    public const string Succeeded = "command.succeeded";

    /// <summary>Body: <see cref="CommandFailedBody"/> (T2.02).</summary>
    public const string Failed = "command.failed";

    /// <summary>Body: <see cref="CommandIdBody"/> (T2.02).</summary>
    public const string TimedOut = "command.timedOut";

    /// <summary>Body: <see cref="CommandByBody"/> (T2.02).</summary>
    public const string Expired = "command.expired";

    /// <summary>Body: <see cref="CommandByBody"/>.</summary>
    public const string Cancelled = "command.cancelled";

    /// <summary>Body: <see cref="CommandVerificationBody"/> (T2.03).</summary>
    public const string Verified = "command.verified";

    /// <summary>Body: <see cref="CommandVerificationBody"/> (T2.03).</summary>
    public const string Unverified = "command.unverified";

    /// <summary>Every kind, in the order above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Requested, Rejected, AwaitingApproval, Approved, ApprovalRejected, Queued, Dispatched, Succeeded, Failed, TimedOut,
        Expired, Cancelled, Verified, Unverified,
    ];
}

/// <summary>An error in a record body: code, invariant-culture arguments and field problems.</summary>
/// <param name="Code">The error code.</param>
/// <param name="Args">The arguments as text.</param>
/// <param name="Fields">The field problems (argument validation), if any.</param>
public sealed record CommandRecordError(string Code, IReadOnlyDictionary<string, string?> Args, IReadOnlyList<CommandRecordFieldError>? Fields);

/// <summary>A field problem in a record body.</summary>
/// <param name="Path">The JSON pointer, e.g. <c>/arguments/duration</c>.</param>
/// <param name="Code">The error code.</param>
public sealed record CommandRecordFieldError(string Path, string Code);

/// <summary>
/// <c>command.rejected</c>. A request refused by validation has no <c>command.requested</c> record, so the body names
/// what was requested (<paramref name="Path"/>, <paramref name="Issuer"/>, <paramref name="Reason"/>); a rejection of a
/// recorded command (approval expired) carries only the id and the error.
/// </summary>
/// <param name="Id">The command id.</param>
/// <param name="Error">Why.</param>
/// <param name="Path">The requested command, for a refused request.</param>
/// <param name="Issuer">The issuer, for a refused request.</param>
/// <param name="Reason">The reason given, for a refused request.</param>
/// <param name="GroupId">The group of a refused child (T2.03).</param>
public sealed record CommandRejectedBody(CommandId Id, CommandRecordError Error, CommandPath? Path = null, CommandIssuer? Issuer = null,
    string? Reason = null, CommandId? GroupId = null);

/// <summary><c>command.awaitingApproval</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="RequestExpiresAt">The end of the approval window.</param>
public sealed record CommandAwaitingApprovalBody(CommandId Id, DateTimeOffset RequestExpiresAt);

/// <summary><c>command.approved</c> / <c>command.approvalRejected</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="By">The approver.</param>
/// <param name="Reason">The approver's reason.</param>
public sealed record CommandApprovalBody(CommandId Id, UserId By, string? Reason);

/// <summary><c>command.queued</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="Priority">The priority.</param>
/// <param name="Attempt">The attempt (2 for a recovery requeue, CMD §8).</param>
public sealed record CommandQueuedBody(CommandId Id, CommandPriority Priority, int Attempt = 1);

/// <summary><c>command.dispatched</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="NodeId">The dispatching node.</param>
public sealed record CommandDispatchedBody(CommandId Id, int Attempt, NodeId? NodeId);

/// <summary><c>command.succeeded</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="DurationMs">The execution time.</param>
public sealed record CommandSucceededBody(CommandId Id, long DurationMs);

/// <summary><c>command.failed</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="Failure">Why.</param>
public sealed record CommandFailedBody(CommandId Id, CommandFailure Failure);

/// <summary><c>command.timedOut</c>.</summary>
/// <param name="Id">The command id.</param>
public sealed record CommandIdBody(CommandId Id);

/// <summary><c>command.expired</c> / <c>command.cancelled</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="By">Who cancelled (the identity), if a person or an identity did.</param>
public sealed record CommandByBody(CommandId Id, UserId? By);

/// <summary><c>command.verified</c> / <c>command.unverified</c>.</summary>
/// <param name="Id">The command id.</param>
/// <param name="Observed">The observed value, if any.</param>
/// <param name="AfterMs">The time from success to the decision.</param>
public sealed record CommandVerificationBody(CommandId Id, WpValue? Observed, long AfterMs);
