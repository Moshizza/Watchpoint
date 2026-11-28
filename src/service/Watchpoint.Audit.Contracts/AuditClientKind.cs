namespace Watchpoint.Audit.Contracts;

/// <summary>Client software an actor used.</summary>
public enum AuditClientKind
{
    /// <summary>Desktop application.</summary>
    Desktop,

    /// <summary>Web browser client.</summary>
    Web,

    /// <summary>Mobile application.</summary>
    Mobile,

    /// <summary>Direct API caller.</summary>
    Api,
}
