namespace Watchpoint.Kernel.Values;

/// <summary>Connectivity of a device or server (shared by MOD, PIP, STA and the integration contract).</summary>
public enum Connectivity
{
    /// <summary>Connected and responding.</summary>
    Online,

    /// <summary>Connected with problems (partial communication).</summary>
    Degraded,

    /// <summary>Not connected.</summary>
    Offline,

    /// <summary>Not known yet.</summary>
    Unknown,
}
