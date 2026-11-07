using System.Diagnostics.CodeAnalysis;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>The imported, enabled devices of the connection (INT §7.2).</summary>
public interface IDeviceMap
{
    /// <summary>Gets all imported, enabled devices.</summary>
    IReadOnlyCollection<DeviceHandle> All { get; }

    /// <summary>Gets a device by key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="handle">The device, when found.</param>
    /// <returns><see langword="true"/> when the device is imported and enabled.</returns>
    bool TryGet(DeviceKey key, [NotNullWhen(true)] out DeviceHandle? handle);

    /// <summary>Raised on imports, deletions, enable/disable and attribute edits.</summary>
    event EventHandler<DeviceMapChangedEventArgs>? Changed;
}

/// <summary>A change of the device map.</summary>
/// <param name="added">Devices imported or enabled.</param>
/// <param name="updated">Devices whose attributes or type changed.</param>
/// <param name="removed">Devices deleted or disabled.</param>
public sealed class DeviceMapChangedEventArgs(IReadOnlyList<DeviceHandle> added, IReadOnlyList<DeviceHandle> updated,
    IReadOnlyList<DeviceKey> removed) : EventArgs
{
    /// <summary>Gets the devices imported or enabled.</summary>
    public IReadOnlyList<DeviceHandle> Added { get; } = added;

    /// <summary>Gets the devices whose attributes or type changed.</summary>
    public IReadOnlyList<DeviceHandle> Updated { get; } = updated;

    /// <summary>Gets the devices deleted or disabled.</summary>
    public IReadOnlyList<DeviceKey> Removed { get; } = removed;
}
