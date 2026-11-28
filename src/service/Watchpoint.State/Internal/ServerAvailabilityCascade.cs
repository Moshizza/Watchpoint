using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// When a server's connectivity crosses the online/unavailable boundary, marks every device hosted on it or on a
/// child server unavailable or available again (spec STA §6.1). Devices may live in any partition, so each one is
/// updated through <see cref="IPartitionWork"/> (the pipeline's dispatcher in production), never directly.
/// </summary>
internal sealed class ServerAvailabilityCascade(IRegistryReader registry, IPartitionWork work, StateStore store, TimeProvider time)
{
    /// <summary>Marks the devices of a server and its child servers unavailable or available again.</summary>
    /// <param name="server">The server whose connectivity changed.</param>
    /// <param name="unavailable">The new unavailable classification.</param>
    /// <param name="ct">The cancellation token.</param>
    public void OnServerHealthChanged(ServerId server, bool unavailable, CancellationToken ct = default)
    {
        if (!registry.GetServer(server).TryGetValue(out var root, out _))
        {
            return;
        }

        var servers = DescendantServers(root);
        var byPartition = new Dictionary<int, List<Guid>>();
        foreach (var serverId in servers)
        {
            foreach (var deviceId in DevicesOf(serverId))
            {
                var partition = Partitioning.Of(deviceId);
                (byPartition.TryGetValue(partition, out var list) ? list : byPartition[partition] = []).Add(deviceId);
            }
        }

        var now = time.GetUtcNow();
        foreach (var (partition, devices) in byPartition)
        {
            _ = work.RunAsync(
                partition,
                () =>
                {
                    foreach (var deviceId in devices)
                    {
                        store.SetServerUnavailable(partition, deviceId, unavailable, now);
                    }
                },
                ct);
        }
    }

    private List<ServerId> DescendantServers(Server root)
    {
        var all = registry.GetServersOfConnection(root.ConnectionId);
        var children = new Dictionary<ServerId, List<ServerId>>();
        foreach (var server in all)
        {
            if (server.ParentId is { } parent)
            {
                (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(server.Id);
            }
        }

        var result = new List<ServerId> { root.Id };
        var queue = new Queue<ServerId>();
        queue.Enqueue(root.Id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!children.TryGetValue(current, out var kids))
            {
                continue;
            }

            foreach (var kid in kids)
            {
                result.Add(kid);
                queue.Enqueue(kid);
            }
        }

        return result;
    }

    private IEnumerable<Guid> DevicesOf(ServerId server)
    {
        string? cursor = null;
        do
        {
            var page = registry.QueryDevices(new DeviceQuery(Servers: [server]), new Kernel.Paging.PageRequest(Kernel.Paging.PageRequest.MaxLimit, cursor));
            foreach (var device in page.Items)
            {
                yield return device.Id.Value;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }
}
