namespace Watchpoint.Audit.Contracts;

/// <summary>What kind of actor performed an audited action.</summary>
public enum ActorKind
{
    /// <summary>A signed-in user.</summary>
    User,

    /// <summary>A script.</summary>
    Script,

    /// <summary>An automation.</summary>
    Automation,

    /// <summary>An integration.</summary>
    Integration,

    /// <summary>The system itself (scheduler, start-up, retention).</summary>
    System,

    /// <summary>A user of another site acting through a federation link.</summary>
    Federation,
}
