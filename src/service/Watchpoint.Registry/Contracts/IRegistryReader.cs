using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Read-only in-memory query interface for registry data.
/// </summary>
public interface IRegistryReader
{
    /// <summary>Gets the current site configuration.</summary>
    Site GetSite();

    /// <summary>Gets a location by ID.</summary>
    Result<Location> GetLocation(LocationId id);
    /// <summary>Queries locations with paging.</summary>
    Page<Location> QueryLocations(LocationQuery query, PageRequest page);
    /// <summary>Gets the root location node.</summary>
    Result<Location> GetRootLocation();
    /// <summary>Gets immediate children of a location.</summary>
    IReadOnlyList<Location> GetLocationChildren(LocationId parentId);

    /// <summary>Gets a tag family by ID.</summary>
    Result<TagFamily> GetTagFamily(TagFamilyId id);
    /// <summary>Gets a tag family by natural key.</summary>
    Result<TagFamily> GetTagFamilyByKey(string key);
    /// <summary>Lists all tag families.</summary>
    IReadOnlyList<TagFamily> ListTagFamilies();

    /// <summary>Gets a tag by ID.</summary>
    Result<Tag> GetTag(TagId id);
    /// <summary>Gets a tag by family ID and key.</summary>
    Result<Tag> GetTagByKey(TagFamilyId familyId, string key);
    /// <summary>Lists tags in a family.</summary>
    IReadOnlyList<Tag> ListTags(TagFamilyId? familyId = null);

    /// <summary>Gets a server by ID.</summary>
    Result<Server> GetServer(ServerId id);
    /// <summary>Gets a server by connection ID and native ID.</summary>
    Result<Server> GetServerByNativeId(ConnectionId connectionId, string nativeId);
    /// <summary>Queries servers with paging.</summary>
    Page<Server> QueryServers(ServerQuery query, PageRequest page);

    /// <summary>Gets a device by ID.</summary>
    Result<Device> GetDevice(DeviceId id);
    /// <summary>Gets a device by server ID and native ID.</summary>
    Result<Device> GetDeviceByNativeId(ServerId serverId, string nativeId);
    /// <summary>Queries devices with paging.</summary>
    Page<Device> QueryDevices(DeviceQuery query, PageRequest page);

    /// <summary>Gets outgoing links from a device.</summary>
    IReadOnlyList<DeviceLink> GetOutgoingLinks(DeviceId sourceId);
    /// <summary>Gets incoming links to a device.</summary>
    IReadOnlyList<DeviceLink> GetIncomingLinks(DeviceId targetId);
    /// <summary>Gets links matching source, role, and target.</summary>
    IReadOnlyList<DeviceLink> GetLinks(DeviceId? sourceId = null, string? role = null, DeviceId? targetId = null);

    /// <summary>Gets a custom field definition by ID.</summary>
    Result<CustomFieldDefinition> GetCustomField(CustomFieldId id);
    /// <summary>Lists custom field definitions.</summary>
    IReadOnlyList<CustomFieldDefinition> ListCustomFields(CustomFieldTarget? target = null);
}
