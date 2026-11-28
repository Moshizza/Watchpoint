namespace Watchpoint.Audit.Contracts;

/// <summary>Severity of an audited action.</summary>
public enum AuditSeverity
{
    /// <summary>Routine.</summary>
    Normal,

    /// <summary>Sensitive (rights changes, unlocks, exports).</summary>
    Sensitive,

    /// <summary>Critical (break-glass use, chain verification failure): flushed immediately.</summary>
    Critical,
}
