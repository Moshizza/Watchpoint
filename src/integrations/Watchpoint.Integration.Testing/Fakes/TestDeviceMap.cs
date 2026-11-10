using System.Diagnostics.CodeAnalysis;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// In-memory implementation of <see cref="IDeviceMap"/> for test environments.
/// </summary>
public sealed class TestDeviceMap : IDeviceMap
{
    private readonly Dictionary<DeviceKey, DeviceHandle> _devices = new();

    /// <inheritdoc />
    public IReadOnlyCollection<DeviceHandle> All
    {
        get
        {
            lock (_devices)
            {
                return _devices.Values.ToList();
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(DeviceKey key, [NotNullWhen(true)] out DeviceHandle? handle)
    {
        lock (_devices)
        {
            return _devices.TryGetValue(key, out handle);
        }
    }

    /// <inheritdoc />
    public event EventHandler<DeviceMapChangedEventArgs>? Changed;

    /// <summary>Adds or updates a device handle in the map.</summary>
    public void Add(DeviceHandle handle)
    {
        lock (_devices)
        {
            bool exists = _devices.ContainsKey(handle.Key);
            _devices[handle.Key] = handle;
            if (exists)
            {
                Changed?.Invoke(this, new DeviceMapChangedEventArgs([], [handle], []));
            }
            else
            {
                Changed?.Invoke(this, new DeviceMapChangedEventArgs([handle], [], []));
            }
        }
    }

    /// <summary>Removes a device key from the map.</summary>
    public bool Remove(DeviceKey key)
    {
        lock (_devices)
        {
            if (_devices.Remove(key))
            {
                Changed?.Invoke(this, new DeviceMapChangedEventArgs([], [], [key]));
                return true;
            }
            return false;
        }
    }

    /// <summary>Clears all devices from the map.</summary>
    public void Clear()
    {
        lock (_devices)
        {
            var keys = _devices.Keys.ToList();
            _devices.Clear();
            if (keys.Count > 0)
            {
                Changed?.Invoke(this, new DeviceMapChangedEventArgs([], [], keys));
            }
        }
    }
}
