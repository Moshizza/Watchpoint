using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>A server of the connection as the sink resolves it.</summary>
internal sealed record MappedServer(ServerId Id, string NativeId, bool IsRoot);

/// <summary>The registry view of a connection: what the sink resolves and what the connector sees.</summary>
internal sealed class DeviceMapSnapshot
{
    public static readonly DeviceMapSnapshot Empty = new([], []);

    public DeviceMapSnapshot(IEnumerable<MappedServer> servers, IEnumerable<(Device Device, string ServerNativeId)> devices)
    {
        Servers = servers.ToFrozenDictionary(s => s.NativeId, StringComparer.Ordinal);
        var list = devices.ToList();

        // The sink resolves every imported device that is not orphaned (the ingest drops disabled targets itself).
        Ids = list.Where(d => !d.Device.Orphaned).ToFrozenDictionary(d => new DeviceKey(d.ServerNativeId, d.Device.NativeId), d => d.Device.Id);

        // The connector sees the imported, enabled, not orphaned devices (INT §7.2).
        Handles = list.Where(d => d.Device is { Enabled: true, Orphaned: false })
            .Select(d => new DeviceHandle(new DeviceKey(d.ServerNativeId, d.Device.NativeId), d.Device.Type.Id.Value, d.Device.Type.Version.ToString(),
                d.Device.Attributes, [.. d.Device.UnsupportedCaps.Select(c => c.Value).Order(StringComparer.Ordinal)]))
            .ToFrozenDictionary(h => h.Key);
    }

    public FrozenDictionary<string, MappedServer> Servers { get; }

    public FrozenDictionary<DeviceKey, DeviceId> Ids { get; }

    public FrozenDictionary<DeviceKey, DeviceHandle> Handles { get; }

    /// <summary>Reads the servers and devices of a connection from the registry.</summary>
    public static DeviceMapSnapshot Load(IRegistryReader reader, ConnectionId connection)
    {
        var servers = reader.GetServersOfConnection(connection);
        var devices = new List<(Device, string)>();
        foreach (var server in servers)
        {
            string? cursor = null;
            do
            {
                var page = reader.QueryDevices(new DeviceQuery(Servers: [server.Id]), new PageRequest(PageRequest.MaxLimit, cursor));
                devices.AddRange(page.Items.Select(d => (d, server.NativeId)));
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }

        return new DeviceMapSnapshot(servers.Select(s => new MappedServer(s.Id, s.NativeId, s.ParentId is null)), devices);
    }
}

/// <summary>
/// The device map given to a connector (spec IRT §8): an immutable snapshot swapped on registry changes, with
/// <see cref="Changed"/> raised for the differences.
/// </summary>
internal sealed class DeviceMap(ILogger logger, Guid connectionId) : IDeviceMap
{
    private DeviceMapSnapshot _snapshot = DeviceMapSnapshot.Empty;

    public event EventHandler<DeviceMapChangedEventArgs>? Changed;

    public DeviceMapSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyCollection<DeviceHandle> All => Snapshot.Handles.Values;

    public bool TryGet(DeviceKey key, [NotNullWhen(true)] out DeviceHandle? handle) => Snapshot.Handles.TryGetValue(key, out handle);

    /// <summary>Swaps the snapshot and raises <see cref="Changed"/> when the connector's view changed.</summary>
    public void Update(DeviceMapSnapshot next)
    {
        var previous = Interlocked.Exchange(ref _snapshot, next);
        var added = new List<DeviceHandle>();
        var updated = new List<DeviceHandle>();
        foreach (var (key, handle) in next.Handles)
        {
            if (!previous.Handles.TryGetValue(key, out var old))
            {
                added.Add(handle);
            }
            else if (!SameHandle(old, handle))
            {
                updated.Add(handle);
            }
        }

        var removed = previous.Handles.Keys.Where(k => !next.Handles.ContainsKey(k)).ToList();
        if (added.Count == 0 && updated.Count == 0 && removed.Count == 0)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, new DeviceMapChangedEventArgs(added, updated, removed));
        }
        catch (Exception ex)
        {
            // A connector's handler must not break the runtime (L4 applies to calls into the connector only).
            IntegrationLog.DeviceMapFailed(logger, ex, connectionId);
        }
    }

    private static bool SameHandle(DeviceHandle a, DeviceHandle b) =>
        a.TypeId == b.TypeId && a.TypeVersion == b.TypeVersion
        && a.UnsupportedOptionalCapabilities.SequenceEqual(b.UnsupportedOptionalCapabilities, StringComparer.Ordinal)
        && a.Attributes.Count == b.Attributes.Count
        && a.Attributes.All(p => b.Attributes.TryGetValue(p.Key, out var v) && v.Equals(p.Value));
}
