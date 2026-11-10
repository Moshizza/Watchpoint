using System.Collections.Frozen;
using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal;

internal sealed class RegistrySnapshot
{
    public RegistrySnapshot(
        Site site,
        IReadOnlyDictionary<LocationId, Location> locations,
        IReadOnlyDictionary<TagFamilyId, TagFamily> tagFamilies,
        IReadOnlyDictionary<TagId, Tag> tags,
        IReadOnlyDictionary<ServerId, Server> servers,
        IReadOnlyDictionary<DeviceId, Device> devices,
        IReadOnlyList<DeviceLink> links,
        IReadOnlyDictionary<CustomFieldId, CustomFieldDefinition> customFields)
    {
        Site = site;
        Locations = locations.ToFrozenDictionary();
        TagFamilies = tagFamilies.ToFrozenDictionary();
        Tags = tags.ToFrozenDictionary();
        Servers = servers.ToFrozenDictionary();
        Devices = devices.ToFrozenDictionary();
        Links = links;
        CustomFields = customFields.ToFrozenDictionary();

        // Index: Root location
        RootLocation = Locations.Values.FirstOrDefault(l => l.ParentId == null);

        // Index: Locations by parent
        LocationsByParent = Locations.Values
            .Where(l => l.ParentId != null)
            .GroupBy(l => l.ParentId!.Value)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<Location>)g.OrderBy(l => l.SortOrder).ThenBy(l => l.Name.Default, StringComparer.OrdinalIgnoreCase).ToList());

        // Index: Tag families by key
        TagFamiliesByKey = TagFamilies.Values
            .ToFrozenDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

        // Index: Tags by family and key
        TagsByFamilyAndKey = Tags.Values
            .ToFrozenDictionary(t => (t.FamilyId, t.Key));

        // Index: Tags by family
        TagsByFamily = Tags.Values
            .GroupBy(t => t.FamilyId)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<Tag>)g.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList());

        // Index: Servers by (ConnectionId, NativeId)
        ServersByConnectionAndNative = Servers.Values
            .ToFrozenDictionary(s => (s.ConnectionId, s.NativeId));

        // Index: Devices by (ServerId, NativeId)
        DevicesByServerAndNative = Devices.Values
            .ToFrozenDictionary(d => (d.ServerId, d.NativeId));

        // Index: Links outgoing
        LinksOutgoing = Links
            .GroupBy(l => l.SourceDeviceId)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<DeviceLink>)g.ToList());

        // Index: Links incoming
        LinksIncoming = Links
            .GroupBy(l => l.TargetDeviceId)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<DeviceLink>)g.ToList());

        // Index: Custom fields by Target
        CustomFieldsByTarget = CustomFields.Values
            .GroupBy(cf => cf.Target)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<CustomFieldDefinition>)g.OrderBy(cf => cf.Key, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Site Site { get; }
    public Location? RootLocation { get; }
    public FrozenDictionary<LocationId, Location> Locations { get; }
    public FrozenDictionary<LocationId, IReadOnlyList<Location>> LocationsByParent { get; }
    public FrozenDictionary<TagFamilyId, TagFamily> TagFamilies { get; }
    public FrozenDictionary<string, TagFamily> TagFamiliesByKey { get; }
    public FrozenDictionary<TagId, Tag> Tags { get; }
    public FrozenDictionary<(TagFamilyId, string), Tag> TagsByFamilyAndKey { get; }
    public FrozenDictionary<TagFamilyId, IReadOnlyList<Tag>> TagsByFamily { get; }
    public FrozenDictionary<ServerId, Server> Servers { get; }
    public FrozenDictionary<(ConnectionId, string), Server> ServersByConnectionAndNative { get; }
    public FrozenDictionary<DeviceId, Device> Devices { get; }
    public FrozenDictionary<(ServerId, string), Device> DevicesByServerAndNative { get; }
    public IReadOnlyList<DeviceLink> Links { get; }
    public FrozenDictionary<DeviceId, IReadOnlyList<DeviceLink>> LinksOutgoing { get; }
    public FrozenDictionary<DeviceId, IReadOnlyList<DeviceLink>> LinksIncoming { get; }
    public FrozenDictionary<CustomFieldId, CustomFieldDefinition> CustomFields { get; }
    public FrozenDictionary<CustomFieldTarget, IReadOnlyList<CustomFieldDefinition>> CustomFieldsByTarget { get; }
}
