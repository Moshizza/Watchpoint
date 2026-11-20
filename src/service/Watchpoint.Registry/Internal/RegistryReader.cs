using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal;

internal sealed class RegistryReader : IRegistryReader
{
    private readonly Func<RegistrySnapshot> _snapshotProvider;
    private readonly DeviceEnrichmentCache? _enrichmentCache;
    private readonly RegistryChangeFeed? _feed;

    public RegistryReader(Func<RegistrySnapshot> snapshotProvider, DeviceEnrichmentCache? enrichmentCache = null, RegistryChangeFeed? feed = null)
    {
        _snapshotProvider = snapshotProvider;
        _enrichmentCache = enrichmentCache;
        _feed = feed;
    }

    private RegistrySnapshot Snapshot => _snapshotProvider();

    public long Revision => Snapshot.Revision;

    public Site Site => Snapshot.Site;

    public event EventHandler<RegistryChangedEventArgs>? Changed
    {
        add { if (_feed != null) _feed.Changed += value; }
        remove { if (_feed != null) _feed.Changed -= value; }
    }

    public Result<Location> GetLocation(LocationId id) =>
        Snapshot.Locations.TryGetValue(id, out var loc) ? loc : RegistryErrors.LocationNotFound(id.Value.ToString());

    public IReadOnlyList<Location> GetLocationTree() => Snapshot.LocationTree;

    public IReadOnlyList<Location> GetAncestors(LocationId id)
    {
        var snapshot = Snapshot;
        if (!snapshot.Locations.TryGetValue(id, out var location))
        {
            return [];
        }

        var ancestors = new List<Location>();
        var current = location;
        while (current.ParentId != null && snapshot.Locations.TryGetValue(current.ParentId.Value, out var parent))
        {
            ancestors.Add(parent);
            current = parent;
        }

        ancestors.Reverse();
        return ancestors;
    }

    public Result<Server> GetServer(ServerId id) =>
        Snapshot.Servers.TryGetValue(id, out var s) ? s : RegistryErrors.ServerNotFound(id.Value.ToString());

    public Result<Device> GetDevice(DeviceId id) =>
        Snapshot.Devices.TryGetValue(id, out var d) ? d : RegistryErrors.DeviceNotFound(id.Value.ToString());

    public Result<Device> FindDeviceByNativeId(ServerId server, string nativeId) =>
        Snapshot.DevicesByServerAndNative.TryGetValue((server, nativeId), out var d) ? d : RegistryErrors.DeviceNotFound(nativeId);

    public IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection) =>
        Snapshot.ServersByConnection.TryGetValue(connection, out var list) ? list : [];

    public IReadOnlyList<Device> GetChildren(DeviceId parent) =>
        Snapshot.DevicesByParent.TryGetValue(parent, out var list) ? list : [];

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both)
    {
        var snapshot = Snapshot;
        IEnumerable<DeviceLink> links = direction switch
        {
            LinkDirection.Outgoing => snapshot.LinksOutgoing.TryGetValue(device, out var outList) ? outList : [],
            LinkDirection.Incoming => snapshot.LinksIncoming.TryGetValue(device, out var inList) ? inList : [],
            _ => (snapshot.LinksOutgoing.TryGetValue(device, out var o) ? o : [])
                .Concat(snapshot.LinksIncoming.TryGetValue(device, out var i) ? i : []),
        };

        if (kind != null)
        {
            links = links.Where(l => l.Kind == kind.Value);
        }

        return links.ToList();
    }

    public Page<Server> QueryServers(ServerQuery query, PageRequest page)
    {
        var items = Snapshot.Servers.Values.AsEnumerable();

        if (query.ConnectionId != null)
            items = items.Where(s => s.ConnectionId == query.ConnectionId);

        if (query.LocationId != null)
            items = items.Where(s => s.LocationId == query.LocationId);

        if (query.Enabled != null)
            items = items.Where(s => s.Enabled == query.Enabled);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var norm = TextSearchNormalizer.Normalize(query.SearchText);
            items = items.Where(s =>
                TextSearchNormalizer.Normalize(s.VendorName).Contains(norm, StringComparison.Ordinal)
                || (s.DisplayName != null && (TextSearchNormalizer.Normalize(s.DisplayName.Default).Contains(norm, StringComparison.Ordinal)
                    || s.DisplayName.Translations.Values.Any(v => TextSearchNormalizer.Normalize(v).Contains(norm, StringComparison.Ordinal))))
                || TextSearchNormalizer.Normalize(s.NativeId).Contains(norm, StringComparison.Ordinal));
        }

        var ordered = items.OrderBy(s => s.DisplayName?.Default ?? s.VendorName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id).ToList();
        return Paginate(ordered, page, s => s.Id.Value.ToString());
    }

    public Page<Device> QueryDevices(DeviceQuery query, PageRequest page)
    {
        var items = Snapshot.Devices.Values.AsEnumerable();

        if (query.ServerId != null)
            items = items.Where(d => d.ServerId == query.ServerId);

        if (query.LocationId != null)
        {
            var loc = Snapshot.Locations.GetValueOrDefault(query.LocationId.Value);
            if (loc != null)
            {
                var prefix = loc.Path;
                items = items.Where(d =>
                {
                    var dLoc = Snapshot.Locations.GetValueOrDefault(d.LocationId);
                    return dLoc != null && (dLoc.Id == loc.Id || dLoc.Path.StartsWith(prefix + "/", StringComparison.Ordinal));
                });
            }
            else
            {
                items = items.Where(d => d.LocationId == query.LocationId);
            }
        }

        if (query.TypeId != null)
            items = items.Where(d => d.Type.Id == query.TypeId);

        if (query.Tags != null && query.Tags.Count > 0)
            items = items.Where(d => query.Tags.All(t => d.Tags.Contains(t)));

        if (query.Enabled != null)
            items = items.Where(d => d.Enabled == query.Enabled);

        if (query.Maintenance != null)
            items = items.Where(d => d.Maintenance == query.Maintenance);

        if (query.Orphaned != null)
            items = items.Where(d => d.Orphaned == query.Orphaned);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var norm = TextSearchNormalizer.Normalize(query.SearchText);
            items = items.Where(d =>
                TextSearchNormalizer.Normalize(d.VendorName).Contains(norm, StringComparison.Ordinal)
                || (d.DisplayName != null && (TextSearchNormalizer.Normalize(d.DisplayName.Default).Contains(norm, StringComparison.Ordinal)
                    || d.DisplayName.Translations.Values.Any(v => TextSearchNormalizer.Normalize(v).Contains(norm, StringComparison.Ordinal))))
                || TextSearchNormalizer.Normalize(d.NativeId).Contains(norm, StringComparison.Ordinal));
        }

        var ordered = items.OrderBy(d => d.DisplayName?.Default ?? d.VendorName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id).ToList();
        return Paginate(ordered, page, d => d.Id.Value.ToString());
    }

    public IReadOnlyList<Tag> GetTags() =>
        Snapshot.Tags.Values.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<TagFamily> GetTagFamilies() =>
        Snapshot.TagFamilies.Values.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target) =>
        Snapshot.CustomFieldsByTarget.TryGetValue(target, out var list) ? list : [];

    public DeviceEnrichment GetEnrichment(DeviceId id)
    {
        var cached = _enrichmentCache?.Get(id);
        if (cached != null)
        {
            return cached;
        }

        var enrichment = Compute(id);
        _enrichmentCache?.Put(enrichment);
        return enrichment;
    }

    /// <summary>Computes the enrichment of a device from the current snapshot (REG §7.2).</summary>
    internal DeviceEnrichment Compute(DeviceId id)
    {
        var snapshot = Snapshot;
        if (!snapshot.Devices.TryGetValue(id, out var device))
        {
            throw new InvalidOperationException($"Device {id} does not exist; callers must resolve it with GetDevice first.");
        }

        var locationNames = new List<string>();
        var locationPath = string.Empty;
        if (snapshot.Locations.TryGetValue(device.LocationId, out var location))
        {
            locationPath = location.Path;
            locationNames.AddRange(GetAncestors(device.LocationId).Select(l => l.Name.Default));
            locationNames.Add(location.Name.Default);
        }

        var tagKeys = device.Tags
            .Select(t => snapshot.Tags.TryGetValue(t, out var tag) ? tag.Key : null)
            .Where(k => k != null)
            .Select(k => k!)
            .ToList();

        var linkedCameras = GetLinks(id, LinkKind.CameraViews, LinkDirection.Incoming).Select(l => l.From).ToList();

        return new DeviceEnrichment(
            device.Id,
            device.DisplayName?.Default ?? device.VendorName,
            device.ServerId,
            device.LocationId,
            locationPath,
            locationNames,
            tagKeys,
            device.Type,
            linkedCameras,
            device.Enabled,
            device.Maintenance);
    }

    private static Page<T> Paginate<T>(List<T> allItems, PageRequest page, Func<T, string> idSelector)
    {
        var filtered = allItems.AsEnumerable();

        if (!string.IsNullOrEmpty(page.Cursor))
        {
            var skipIndex = allItems.FindIndex(item => idSelector(item) == page.Cursor);
            if (skipIndex >= 0)
                filtered = allItems.Skip(skipIndex + 1);
        }

        var limit = Math.Max(1, page.Limit);
        var pageItems = filtered.Take(limit).ToList();
        string? nextCursor = null;

        if (pageItems.Count == limit)
        {
            var lastItem = pageItems[^1];
            var lastIndex = allItems.FindIndex(item => idSelector(item) == idSelector(lastItem));
            if (lastIndex >= 0 && lastIndex < allItems.Count - 1)
            {
                nextCursor = idSelector(lastItem);
            }
        }

        return new Page<T>(pageItems, nextCursor);
    }
}
