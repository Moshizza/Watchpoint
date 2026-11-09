namespace Watchpoint.Model.Contracts;

/// <summary>Severity classification for states, events, and property values (spec MOD §4.1).</summary>
public enum Severity
{
    /// <summary>Unknown severity level.</summary>
    Unknown = 0,

    /// <summary>Normal or informational severity.</summary>
    Normal = 1,

    /// <summary>Normal active severity (e.g. door held open normally or unlocked).</summary>
    NormalActive = 2,

    /// <summary>Warning condition.</summary>
    Warning = 3,

    /// <summary>Device offline condition.</summary>
    Offline = 4,

    /// <summary>Fault condition.</summary>
    Fault = 5,

    /// <summary>Alarm condition requiring immediate operator action.</summary>
    Alarm = 6,
}

/// <summary>Overall state summary of a device (spec MOD §4.1).</summary>
public enum OverallState
{
    /// <summary>State cannot be determined.</summary>
    Unknown,

    /// <summary>Normal idle condition.</summary>
    Normal,

    /// <summary>Normal active condition.</summary>
    NormalActive,

    /// <summary>Warning condition.</summary>
    Warning,

    /// <summary>Device is offline.</summary>
    Offline,

    /// <summary>Device is experiencing a fault.</summary>
    Fault,

    /// <summary>Device is in alarm condition.</summary>
    Alarm,

    /// <summary>Device is undergoing maintenance.</summary>
    Maintenance,

    /// <summary>Device is administratively disabled.</summary>
    Disabled,
}

/// <summary>Risk level of a command execution (spec MOD §4.1).</summary>
public enum Risk
{
    /// <summary>Routine command with standard authorization.</summary>
    Normal,

    /// <summary>Sensitive command requiring elevated operator rights.</summary>
    Sensitive,

    /// <summary>Critical command impacting safety or site security.</summary>
    Critical,
}
