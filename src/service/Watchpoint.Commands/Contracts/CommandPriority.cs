namespace Watchpoint.Commands.Contracts;

/// <summary>Dispatch priority of a command (spec CMD §4); a lower value is dispatched first.</summary>
public enum CommandPriority
{
    /// <summary>Life safety and commands of critical risk.</summary>
    Critical = 0,

    /// <summary>Interactive user commands (the default for users).</summary>
    High = 1,

    /// <summary>Automations, scripts and alarm actions.</summary>
    Normal = 2,

    /// <summary>Housekeeping.</summary>
    Background = 3,
}
