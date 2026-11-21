using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.State.Tests.Support;

/// <summary>Builds <see cref="Device"/>s and <see cref="Server"/>s with sensible defaults for state module tests.</summary>
internal static class TestEntities
{
    public static Device Door(
        DeviceId? id = null,
        ServerId? serverId = null,
        LocationId? locationId = null,
        IReadOnlyList<TagId>? tags = null,
        bool enabled = true,
        bool maintenance = false,
        TypeRef? type = null,
        IReadOnlyDictionary<string, WpValue>? attributes = null) =>
        new(
            id ?? DeviceId.New(),
            type ?? TestTypes.DoorV1,
            serverId ?? ServerId.New(),
            null,
            "door-1",
            null,
            "Lobby door",
            locationId ?? LocationId.New(),
            tags ?? [],
            attributes ?? new Dictionary<string, WpValue>(),
            new Dictionary<CustomFieldId, WpValue>(),
            new HashSet<CapabilityId>(),
            null,
            enabled,
            maintenance,
            false,
            DateTimeOffset.UnixEpoch,
            1);

    public static Server Server(
        ServerId? id = null,
        ConnectionId? connectionId = null,
        ServerId? parentId = null,
        LocationId? locationId = null,
        bool enabled = true,
        bool maintenance = false) =>
        new(
            id ?? ServerId.New(),
            TestTypes.ServerType,
            connectionId ?? ConnectionId.New(),
            parentId,
            "server-1",
            null,
            "Server",
            locationId ?? LocationId.New(),
            [],
            new Dictionary<string, WpValue>(),
            enabled,
            maintenance,
            1);

    public static Location Location(LocationId? id = null, LocationId? parentId = null, string path = "/root/", int depth = 1) =>
        new(id ?? LocationId.New(), parentId, new LocalizedName("Location", new Dictionary<string, string>()), LocationKind.Building, path, depth, null, null, null, 0, 1);
}
