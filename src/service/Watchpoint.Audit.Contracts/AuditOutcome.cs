namespace Watchpoint.Audit.Contracts;

/// <summary>Outcome of an audited action.</summary>
public enum AuditOutcome
{
    /// <summary>The action succeeded.</summary>
    Success,

    /// <summary>The action failed.</summary>
    Failure,

    /// <summary>The action was refused by a rule (permission, policy).</summary>
    Denied,
}
