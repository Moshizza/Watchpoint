using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Read-only in-memory query interface for registry data (REG §5).
/// </summary>
public interface IRegistryReader
{
    /// <summary>Gets the registry revision: increments with every applied batch.</summary>
    long Revision { get; }

    /// <summary>Gets the current site configuration.</summary>
    Site Site { get; }

    /// <summary>Gets a location by id.</summary>
    Result<Location> GetLocation(LocationId id);

    /// <summary>Gets every location, ordered by path then sort order.</summary>
    IReadOnlyList<Location> GetLocationTree();

    /// <summary>Gets the ancestors of a location, root first; empty when the location is the root or unknown.</summary>
    IReadOnlyList<Location> GetAncestors(LocationId id);

    /// <summary>Gets a server by id.</summary>
    Result<Server> GetServer(ServerId id);

    /// <summary>Gets a device by id.</summary>
    Result<Device> GetDevice(DeviceId id);

    /// <summary>Gets a device by server id and native id.</summary>
    Result<Device> FindDeviceByNativeId(ServerId server, string nativeId);

    /// <summary>Gets every server of a connection.</summary>
    IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection);

    /// <summary>Gets the direct children of a device.</summary>
    IReadOnlyList<Device> GetChildren(DeviceId parent);

    /// <summary>Gets the links of a device, optionally filtered by kind, in the given direction.</summary>
    IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both);

    /// <summary>Queries devices with paging.</summary>
    Page<Device> QueryDevices(DeviceQuery query, PageRequest page);

    /// <summary>Queries servers with paging.</summary>
    Page<Server> QueryServers(ServerQuery query, PageRequest page);

    /// <summary>Gets every tag.</summary>
    IReadOnlyList<Tag> GetTags();

    /// <summary>Gets every tag family.</summary>
    IReadOnlyList<TagFamily> GetTagFamilies();

    /// <summary>Gets the custom field definitions of a target kind.</summary>
    IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target);

    /// <summary>Gets the cached enrichment of a device (used by PIP on every observation, REG §7.2).</summary>
    /// <param name="id">The device id. The device must exist (callers resolve it first with <see cref="GetDevice"/>).</param>
    DeviceEnrichment GetEnrichment(DeviceId id);

    /// <summary>Raised after a change batch has been committed and the in-memory snapshot updated.</summary>
    event EventHandler<RegistryChangedEventArgs>? Changed;
}
