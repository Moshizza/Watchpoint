using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Commands.Contracts;

/// <summary>The kinds of the group records, appended to <c>WP_SYS</c> (spec CMD §10). Schema version 1.</summary>
public static class CommandGroupRecordKinds
{
    /// <summary>Body: <see cref="CommandGroupRequestedBody"/>; written before the children are requested.</summary>
    public const string Requested = "commandGroup.requested";

    /// <summary>Body: <see cref="CommandGroupProgressBody"/>; at most every 500 ms per group.</summary>
    public const string Progress = "commandGroup.progress";

    /// <summary>Body: <see cref="CommandGroupProgressBody"/>; once, when every child is terminal.</summary>
    public const string Completed = "commandGroup.completed";
}

/// <summary><c>commandGroup.requested</c>.</summary>
/// <param name="GroupId">The group id.</param>
/// <param name="Path">The command.</param>
/// <param name="Args">The arguments as requested (each child validates them).</param>
/// <param name="Issuer">The issuer.</param>
/// <param name="Count">The number of targets.</param>
/// <param name="Targets">The targets, in request order.</param>
/// <param name="RetryOf">The group whose failed children this group retries, if any.</param>
public sealed record CommandGroupRequestedBody(CommandId GroupId, CommandPath Path, JsonElement Args, CommandIssuer Issuer, int Count,
    IReadOnlyList<DeviceId> Targets, CommandId? RetryOf = null);

/// <summary><c>commandGroup.progress</c> / <c>commandGroup.completed</c>.</summary>
/// <param name="GroupId">The group id.</param>
/// <param name="Counters">The counters.</param>
public sealed record CommandGroupProgressBody(CommandId GroupId, CommandGroupCounters Counters);

/// <summary>
/// The counters of a group (spec CMD §10). <see cref="Succeeded"/> counts the children that succeeded, verified or not;
/// <see cref="Verified"/> and <see cref="Unverified"/> are parts of it. <see cref="Failed"/> also counts expired and
/// cancelled children. <see cref="Pending"/> = children not terminal yet.
/// </summary>
/// <param name="Total">The targets.</param>
/// <param name="Succeeded">Children that succeeded.</param>
/// <param name="Failed">Children that failed, expired or were cancelled.</param>
/// <param name="TimedOut">Children that timed out.</param>
/// <param name="Verified">Children whose effect was verified.</param>
/// <param name="Unverified">Children whose effect was not seen in time.</param>
/// <param name="Rejected">Children refused by validation or approval.</param>
/// <param name="Pending">Children not terminal yet.</param>
public sealed record CommandGroupCounters(int Total, int Succeeded, int Failed, int TimedOut, int Verified, int Unverified, int Rejected, int Pending);

/// <summary>A group as <see cref="ICommandService.GetGroup"/> returns it.</summary>
/// <param name="Id">The group id.</param>
/// <param name="Path">The command.</param>
/// <param name="Issuer">The issuer.</param>
/// <param name="RequestedAt">When it was requested.</param>
/// <param name="Counters">The counters.</param>
/// <param name="Children">One entry per target, in request order.</param>
/// <param name="RetryOf">The group it retries, if any.</param>
/// <param name="CompletedAt">When every child became terminal.</param>
public sealed record CommandGroup(CommandId Id, CommandPath Path, CommandIssuer Issuer, DateTimeOffset RequestedAt, CommandGroupCounters Counters,
    IReadOnlyList<CommandGroupChild> Children, CommandId? RetryOf, DateTimeOffset? CompletedAt);

/// <summary>A child of a group.</summary>
/// <param name="Target">The device.</param>
/// <param name="CommandId">The child command (also for a refused child).</param>
/// <param name="State">Its state.</param>
public sealed record CommandGroupChild(DeviceId Target, CommandId? CommandId, CommandState State);
