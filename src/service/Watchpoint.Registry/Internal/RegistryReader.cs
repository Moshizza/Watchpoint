using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal;

internal sealed class RegistryReader : IRegistryReader
{
    private readonly Func<RegistrySnapshot> _snapshotProvider;

    public RegistryReader(Func<RegistrySnapshot> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    private RegistrySnapshot Snapshot => _snapshotProvider();

    public Site GetSite() => Snapshot.Site;

    public Result<Location> GetLocation(LocationId id) =>
        Snapshot.Locations.TryGetValue(id, out var loc) ? loc : RegistryErrors.EntityNotFound("location", id.Value.ToString());

    public Result<Location> GetRootLocation() =>
        Snapshot.RootLocation != null ? Snapshot.RootLocation : RegistryErrors.EntityNotFound("location", "root");

    public IReadOnlyList<Location> GetLocationChildren(LocationId parentId) =>
        Snapshot.LocationsByParent.TryGetValue(parentId, out var children) ? children : [];

    public Page<Location> QueryLocations(LocationQuery query, PageRequest page)
    {
        var items = Snapshot.Locations.Values.AsEnumerable();

        if (query.ParentId != null)
            items = items.Where(l => l.ParentId == query.ParentId);

        if (!string.IsNullOrEmpty(query.PathPrefix))
            items = items.Where(l => l.Path.StartsWith(query.PathPrefix, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var norm = TextSearchNormalizer.Normalize(query.SearchText);
            items = items.Where(l => TextSearchNormalizer.Normalize(l.Name.Default).Contains(norm, StringComparison.Ordinal)
                || l.Name.Translations.Values.Any(v => TextSearchNormalizer.Normalize(v).Contains(norm, StringComparison.Ordinal)));
        }

        var ordered = items.OrderBy(l => l.SortOrder).ThenBy(l => l.Name.Default, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Id).ToList();
        return Paginate(ordered, page, l => l.Id.Value.ToString());
    }

    public Result<TagFamily> GetTagFamily(TagFamilyId id) =>
        Snapshot.TagFamilies.TryGetValue(id, out var tf) ? tf : RegistryErrors.EntityNotFound("tag_family", id.Value.ToString());

    public Result<TagFamily> GetTagFamilyByKey(string key) =>
        Snapshot.TagFamiliesByKey.TryGetValue(key, out var tf) ? tf : RegistryErrors.EntityNotFound("tag_family", key);

    public IReadOnlyList<TagFamily> ListTagFamilies() =>
        Snapshot.TagFamilies.Values.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public Result<Tag> GetTag(TagId id) =>
        Snapshot.Tags.TryGetValue(id, out var t) ? t : RegistryErrors.EntityNotFound("tag", id.Value.ToString());

    public Result<Tag> GetTagByKey(TagFamilyId familyId, string key) =>
        Snapshot.TagsByFamilyAndKey.TryGetValue((familyId, key), out var t) ? t : RegistryErrors.EntityNotFound("tag", key);

    public IReadOnlyList<Tag> ListTags(TagFamilyId? familyId = null)
    {
        if (familyId != null)
            return Snapshot.TagsByFamily.TryGetValue(familyId.Value, out var list) ? list : [];

        return Snapshot.Tags.Values.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Result<Server> GetServer(ServerId id) =>
        Snapshot.Servers.TryGetValue(id, out var s) ? s : RegistryErrors.EntityNotFound("server", id.Value.ToString());

    public Result<Server> GetServerByNativeId(ConnectionId connectionId, string nativeId) =>
        Snapshot.ServersByConnectionAndNative.TryGetValue((connectionId, nativeId), out var s) ? s : RegistryErrors.EntityNotFound("server", nativeId);

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

    public Result<Device> GetDevice(DeviceId id) =>
        Snapshot.Devices.TryGetValue(id, out var d) ? d : RegistryErrors.EntityNotFound("device", id.Value.ToString());

    public Result<Device> GetDeviceByNativeId(ServerId serverId, string nativeId) =>
        Snapshot.DevicesByServerAndNative.TryGetValue((serverId, nativeId), out var d) ? d : RegistryErrors.EntityNotFound("device", nativeId);

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

    public IReadOnlyList<DeviceLink> GetOutgoingLinks(DeviceId sourceId) =>
        Snapshot.LinksOutgoing.TryGetValue(sourceId, out var links) ? links : [];

    public IReadOnlyList<DeviceLink> GetIncomingLinks(DeviceId targetId) =>
        Snapshot.LinksIncoming.TryGetValue(targetId, out var links) ? links : [];

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId? sourceId = null, string? role = null, DeviceId? targetId = null)
    {
        var links = Snapshot.Links.AsEnumerable();

        if (sourceId != null)
            links = links.Where(l => l.SourceDeviceId == sourceId);

        if (!string.IsNullOrEmpty(role))
            links = links.Where(l => string.Equals(l.Role, role, StringComparison.OrdinalIgnoreCase));

        if (targetId != null)
            links = links.Where(l => l.TargetDeviceId == targetId);

        return links.ToList();
    }

    public Result<CustomFieldDefinition> GetCustomField(CustomFieldId id) =>
        Snapshot.CustomFields.TryGetValue(id, out var cf) ? cf : RegistryErrors.EntityNotFound("custom_field", id.Value.ToString());

    public IReadOnlyList<CustomFieldDefinition> ListCustomFields(CustomFieldTarget? target = null)
    {
        if (target != null)
            return Snapshot.CustomFieldsByTarget.TryGetValue(target.Value, out var list) ? list : [];

        return Snapshot.CustomFields.Values.OrderBy(cf => cf.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Page<T> Paginate<T>(List<T> allItems, PageRequest page, Func<T, string> idSelector)
    {
        var totalCount = allItems.Count;
        var filtered = allItems.AsEnumerable();

        if (!string.IsNullOrEmpty(page.Cursor))
        {
            var skipIndex = allItems.FindIndex(item => idSelector(item) == page.Cursor);
            if (skipIndex >= 0)
                filtered = allItems.Skip(skipIndex + 1);
        }

        var pageSize = Math.Max(1, page.PageSize);
        var pageItems = filtered.Take(pageSize).ToList();
        string? nextCursor = null;

        if (pageItems.Count == pageSize)
        {
            var lastItem = pageItems[^1];
            var lastIndex = allItems.FindIndex(item => idSelector(item) == idSelector(lastItem));
            if (lastIndex >= 0 && lastIndex < allItems.Count - 1)
            {
                nextCursor = idSelector(lastItem);
            }
        }

        return new Page<T>(pageItems, nextCursor, totalCount);
    }
}
