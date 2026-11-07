namespace Watchpoint.Kernel.Values;

/// <summary>Quality of a property value (shared by MOD, PIP, STA and the integration contract).</summary>
public enum Quality
{
    /// <summary>The value is reliable.</summary>
    Good,

    /// <summary>The value may be wrong (e.g. sensor reports a fault).</summary>
    Uncertain,

    /// <summary>The value is wrong or unavailable.</summary>
    Bad,

    /// <summary>The value has not been refreshed in time.</summary>
    Stale,
}
