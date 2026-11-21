using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>A registry reader that knows tags only (the one thing the queries ask it).</summary>
internal sealed class FakeRegistryReader : IRegistryReader
{
    public List<Tag> Tags { get; } = [];

    public long Revision => 1;

    public Site Site => throw new NotSupportedException();

    public event EventHandler<RegistryChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<Tag> GetTags() => Tags;

    public Result<Location> GetLocation(LocationId id) => throw new NotSupportedException();

    public IReadOnlyList<Location> GetLocationTree() => throw new NotSupportedException();

    public IReadOnlyList<Location> GetAncestors(LocationId id) => throw new NotSupportedException();

    public Result<Server> GetServer(ServerId id) => throw new NotSupportedException();

    public Result<Device> GetDevice(DeviceId id) => throw new NotSupportedException();

    public Result<Device> FindDeviceByNativeId(ServerId server, string nativeId) => throw new NotSupportedException();

    public IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection) => throw new NotSupportedException();

    public IReadOnlyList<Device> GetChildren(DeviceId parent) => throw new NotSupportedException();

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both) => throw new NotSupportedException();

    public Page<Device> QueryDevices(DeviceQuery query, PageRequest page) => throw new NotSupportedException();

    public Page<Server> QueryServers(ServerQuery query, PageRequest page) => throw new NotSupportedException();

    public IReadOnlyList<TagFamily> GetTagFamilies() => throw new NotSupportedException();

    public IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target) => throw new NotSupportedException();

    public DeviceEnrichment GetEnrichment(DeviceId id) => throw new NotSupportedException();
}
