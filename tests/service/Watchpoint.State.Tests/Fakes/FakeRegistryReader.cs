using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.State.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IRegistryReader"/> for state module unit tests (spec STA §13: "fake registry"). Mutation
/// methods mirror what a real <c>IRegistryWriter.ApplyAsync</c> does: they change the snapshot, then raise
/// <see cref="Changed"/> with the batch that caused it, exactly as REG does after the snapshot swap.
/// </summary>
internal sealed class FakeRegistryReader : IRegistryReader
{
    // A real keyset-paged query seeks to the cursor in its index rather than rescanning every page from the start;
    // these two SortedSets of ids give QueryDevices/QueryServers the same O(log n) seek (GetViewBetween).
    private static readonly Guid _maxGuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private readonly Dictionary<Guid, Device> _devices = [];
    private readonly Dictionary<Guid, Server> _servers = [];
    private readonly SortedSet<Guid> _deviceIds = [];
    private readonly SortedSet<Guid> _serverIds = [];
    private readonly Dictionary<Guid, Location> _locations = [];

    public FakeRegistryReader() => Site = new Site(SiteId.New(), new LocalizedName("Site", new Dictionary<string, string>()), "UTC", 1);

    public long Revision { get; private set; }

    public Site Site { get; }

    public event EventHandler<RegistryChangedEventArgs>? Changed;

    public Result<Location> GetLocation(LocationId id) =>
        _locations.TryGetValue(id.Value, out var location) ? location : RegistryErrors.LocationNotFound(id.Value.ToString());

    public IReadOnlyList<Location> GetLocationTree() => [.. _locations.Values.OrderBy(l => l.Path, StringComparer.Ordinal).ThenBy(l => l.SortOrder)];

    public IReadOnlyList<Location> GetAncestors(LocationId id)
    {
        var ancestors = new List<Location>();
        var current = _locations.GetValueOrDefault(id.Value)?.ParentId;
        while (current is { } parentId && _locations.TryGetValue(parentId.Value, out var parent))
        {
            ancestors.Insert(0, parent);
            current = parent.ParentId;
        }

        return ancestors;
    }

    public Result<Server> GetServer(ServerId id) => _servers.TryGetValue(id.Value, out var server) ? server : RegistryErrors.ServerNotFound(id.Value.ToString());

    public Result<Device> GetDevice(DeviceId id) => _devices.TryGetValue(id.Value, out var device) ? device : RegistryErrors.DeviceNotFound(id.Value.ToString());

    public Result<Device> FindDeviceByNativeId(ServerId server, string nativeId)
    {
        foreach (var device in _devices.Values)
        {
            if (device.ServerId == server && device.NativeId == nativeId)
            {
                return device;
            }
        }

        return RegistryErrors.DeviceNotFound(nativeId);
    }

    public IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection) => [.. _servers.Values.Where(s => s.ConnectionId == connection)];

    public IReadOnlyList<Device> GetChildren(DeviceId parent) => [.. _devices.Values.Where(d => d.ParentId == parent)];

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both) => [];

    public Page<Device> QueryDevices(DeviceQuery query, PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        return Paginate(
            _deviceIds,
            id => _devices[id],
            d => d.Id.Value,
            d => (query.Servers is not { } servers || servers.Contains(d.ServerId))
                && (query.LocationsSubtree is not { } locations || locations.Contains(d.LocationId))
                && (query.Types is not { } types || types.Contains(d.Type.Id))
                && (query.Enabled is not { } enabled || d.Enabled == enabled)
                && (query.Maintenance is not { } maintenance || d.Maintenance == maintenance)
                && (query.Orphaned is not { } orphaned || d.Orphaned == orphaned)
                && (query.TagsAll is null || query.TagsAll.All(t => d.Tags.Contains(t))),
            page);
    }

    public Page<Server> QueryServers(ServerQuery query, PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        return Paginate(
            _serverIds,
            id => _servers[id],
            s => s.Id.Value,
            s => (query.ConnectionId is not { } connection || s.ConnectionId == connection)
                && (query.LocationId is not { } location || s.LocationId == location)
                && (query.Enabled is not { } enabled || s.Enabled == enabled),
            page);
    }

    public IReadOnlyList<Tag> GetTags() => [];

    public IReadOnlyList<TagFamily> GetTagFamilies() => [];

    public IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target) => [];

    public DeviceEnrichment GetEnrichment(DeviceId id)
    {
        var device = _devices[id.Value];
        return new DeviceEnrichment(id, device.VendorName, device.ServerId, device.LocationId, string.Empty, [], [], device.Type, [], device.Enabled, device.Maintenance);
    }

    // ---- Mutation helpers (mirror REG's ApplyAsync + Changed, spec REG §6) ----

    public void SeedLocation(Location location) => _locations[location.Id.Value] = location;

    public void CreateDevice(Device device) => Upsert(device, created: true);

    public void UpdateDevice(Device device) => Upsert(device, created: false);

    public void DeleteDevice(DeviceId id, bool deleteChildren = false)
    {
        _devices.Remove(id.Value);
        _deviceIds.Remove(id.Value);
        Raise([new RegistryChange.DeleteDevice(id, deleteChildren)]);
    }

    public void CreateServer(Server server) => Upsert(server, created: true);

    public void UpdateServer(Server server) => Upsert(server, created: false);

    public void DeleteServer(ServerId id, bool deleteDevices = false)
    {
        _servers.Remove(id.Value);
        _serverIds.Remove(id.Value);
        Raise([new RegistryChange.DeleteServer(id, deleteDevices)]);
    }

    /// <summary>
    /// Deletes a location and moves its devices and servers to <paramref name="reassignTo"/>, as REG does for
    /// <c>DeleteLocation(id, ReassignTo)</c>: the moved entities are not named in the batch.
    /// </summary>
    public void DeleteLocation(LocationId id, LocationId reassignTo)
    {
        foreach (var device in _devices.Values.Where(d => d.LocationId == id).ToList())
        {
            _devices[device.Id.Value] = device with { LocationId = reassignTo };
        }

        foreach (var server in _servers.Values.Where(sv => sv.LocationId == id).ToList())
        {
            _servers[server.Id.Value] = server with { LocationId = reassignTo };
        }

        _locations.Remove(id.Value);
        Raise([new RegistryChange.DeleteLocation(id, reassignTo)]);
    }

    /// <summary>Moves a location under another parent: the entities of its subtree get new ancestors without being named.</summary>
    public void MoveLocation(Location location, LocationId newParent)
    {
        var moved = location with { ParentId = newParent };
        _locations[location.Id.Value] = moved;
        Raise([new RegistryChange.UpsertLocation(moved)]);
    }

    /// <summary>Raises <see cref="Changed"/> directly, for batches the simple helpers above do not cover.</summary>
    public void Raise(IReadOnlyList<RegistryChange> changes, ChangeContext? context = null)
    {
        Revision++;
        Changed?.Invoke(this, new RegistryChangedEventArgs(new RegistryChangeBatch(changes), context ?? new ChangeContext(null, "test")));
    }

    private void Upsert(Device device, bool created)
    {
        _devices[device.Id.Value] = device;
        _deviceIds.Add(device.Id.Value);
        Raise([new RegistryChange.UpsertDevice(device, created ? null : device.Version)]);
    }

    private void Upsert(Server server, bool created)
    {
        _servers[server.Id.Value] = server;
        _serverIds.Add(server.Id.Value);
        Raise([new RegistryChange.UpsertServer(server, created ? null : server.Version)]);
    }

    /// <summary>
    /// Pages <paramref name="ids"/> in ascending order, seeking to the cursor with <see cref="SortedSet{T}.GetViewBetween"/>
    /// (O(log n)) instead of rescanning every item from the start on each page, like a real keyset query would with an
    /// id index.
    /// </summary>
    private static Page<T> Paginate<T>(SortedSet<Guid> ids, Func<Guid, T> lookup, Func<T, Guid> keyOf, Func<T, bool> predicate, PageRequest page)
    {
        var after = page.Cursor is { } cursor ? Guid.Parse(cursor) : Guid.Empty;
        var items = new List<T>();
        var hasMore = false;
        foreach (var id in ids.GetViewBetween(after, _maxGuid))
        {
            if (id == after)
            {
                continue;
            }

            var item = lookup(id);
            if (!predicate(item))
            {
                continue;
            }

            if (items.Count == page.Limit)
            {
                hasMore = true;
                break;
            }

            items.Add(item);
        }

        return new Page<T>(items, hasMore ? keyOf(items[^1]).ToString() : null);
    }
}
