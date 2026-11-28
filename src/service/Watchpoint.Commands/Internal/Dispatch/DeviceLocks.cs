using Watchpoint.Kernel.Ids;

namespace Watchpoint.Commands.Internal.Dispatch;

/// <summary>
/// The devices of a connection with a command in flight (spec CMD §7: one in-flight command per device). Not
/// thread-safe: the owning <see cref="ConnectionDispatcher"/> uses it under its lock.
/// </summary>
internal sealed class DeviceLocks
{
    private readonly HashSet<DeviceId> _locked = [];

    public int Count => _locked.Count;

    public bool IsLocked(DeviceId device) => _locked.Contains(device);

    /// <summary>Locks a device; <see langword="false"/> when it is already locked.</summary>
    public bool TryLock(DeviceId device) => _locked.Add(device);

    public void Unlock(DeviceId device) => _locked.Remove(device);
}
