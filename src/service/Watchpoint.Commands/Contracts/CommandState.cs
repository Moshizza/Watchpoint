namespace Watchpoint.Commands.Contracts;

/// <summary>Lifecycle state of a command (spec CMD §4, §6).</summary>
public enum CommandState
{
    /// <summary>Accepted for validation; the first state of every command.</summary>
    Requested,

    /// <summary>Refused by validation, by an approver, or because the approval window passed (terminal).</summary>
    Rejected,

    /// <summary>Waiting for a second person's approval (four-eyes).</summary>
    AwaitingApproval,

    /// <summary>Waiting in the connection's queue for dispatch.</summary>
    Queued,

    /// <summary>Handed to the connector; the outcome is pending.</summary>
    Dispatched,

    /// <summary>The connector reported success; terminal unless the command has an expected effect.</summary>
    Succeeded,

    /// <summary>The connector reported a failure (terminal).</summary>
    Failed,

    /// <summary>No outcome before the deadline (terminal).</summary>
    TimedOut,

    /// <summary>Not dispatched before <see cref="Command.ExpiresAt"/> (terminal).</summary>
    Expired,

    /// <summary>Cancelled before dispatch (terminal).</summary>
    Cancelled,

    /// <summary>The expected effect was observed (terminal).</summary>
    Verified,

    /// <summary>The expected effect was not observed in time (terminal).</summary>
    Unverified,
}
