using Watchpoint.Commands.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The lifecycle state machine (spec CMD §6): which record kind moves a command from which state to which. Anything
/// else is an invalid (late) transition: ignored, logged 2420 and counted; the first terminal outcome wins.
/// </summary>
internal static class CommandStateMachine
{
    private static readonly Dictionary<(CommandState From, string Kind), CommandState> _transitions = new()
    {
        [(CommandState.Requested, CommandRecordKinds.Rejected)] = CommandState.Rejected,
        [(CommandState.Requested, CommandRecordKinds.AwaitingApproval)] = CommandState.AwaitingApproval,
        [(CommandState.Requested, CommandRecordKinds.Queued)] = CommandState.Queued,
        [(CommandState.AwaitingApproval, CommandRecordKinds.Approved)] = CommandState.Queued,
        [(CommandState.AwaitingApproval, CommandRecordKinds.ApprovalRejected)] = CommandState.Rejected,
        [(CommandState.AwaitingApproval, CommandRecordKinds.Rejected)] = CommandState.Rejected,
        [(CommandState.AwaitingApproval, CommandRecordKinds.Cancelled)] = CommandState.Cancelled,
        [(CommandState.Queued, CommandRecordKinds.Dispatched)] = CommandState.Dispatched,
        [(CommandState.Queued, CommandRecordKinds.Expired)] = CommandState.Expired,
        [(CommandState.Queued, CommandRecordKinds.Cancelled)] = CommandState.Cancelled,
        [(CommandState.Queued, CommandRecordKinds.Failed)] = CommandState.Failed,           // fail fast when disconnected (CMD §7 step 3)
        [(CommandState.Dispatched, CommandRecordKinds.Succeeded)] = CommandState.Succeeded,
        [(CommandState.Dispatched, CommandRecordKinds.Failed)] = CommandState.Failed,
        [(CommandState.Dispatched, CommandRecordKinds.TimedOut)] = CommandState.TimedOut,
        [(CommandState.Dispatched, CommandRecordKinds.Queued)] = CommandState.Queued,       // recovery requeue, attempt 2 (CMD §8)
        [(CommandState.Succeeded, CommandRecordKinds.Verified)] = CommandState.Verified,
        [(CommandState.Succeeded, CommandRecordKinds.Unverified)] = CommandState.Unverified,
    };

    /// <summary>Every allowed transition.</summary>
    public static IEnumerable<(CommandState From, string Kind, CommandState To)> All =>
        _transitions.Select(t => (t.Key.From, t.Key.Kind, t.Value));

    /// <summary>The state a record moves a command to, or <see langword="null"/> when the transition is not allowed.</summary>
    /// <param name="command">The command.</param>
    /// <param name="kind">The record kind.</param>
    public static CommandState? Next(Command command, string kind)
    {
        if (IsTerminal(command))
        {
            return null;
        }

        return _transitions.TryGetValue((command.State, kind), out var next) ? next : null;
    }

    /// <summary>Whether no record can change the command any more.</summary>
    public static bool IsTerminal(Command command) => IsTerminal(command.State, command.Verification is not null);

    /// <summary>Whether <paramref name="state"/> is terminal; <see cref="CommandState.Succeeded"/> is terminal without an expected effect.</summary>
    public static bool IsTerminal(CommandState state, bool hasVerification) => state switch
    {
        CommandState.Rejected or CommandState.Failed or CommandState.TimedOut or CommandState.Expired or CommandState.Cancelled
            or CommandState.Verified or CommandState.Unverified => true,
        CommandState.Succeeded => !hasVerification,
        _ => false,
    };
}
