namespace Watchpoint.State.Contracts;

/// <summary>Whether a <see cref="DeviceState"/> is about a device or a server (spec STA §4).</summary>
public enum EntityKind
{
    /// <summary>The entity is a device.</summary>
    Device,

    /// <summary>The entity is a server.</summary>
    Server,
}
