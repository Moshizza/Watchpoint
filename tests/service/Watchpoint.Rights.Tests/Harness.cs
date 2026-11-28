using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Catalogue;
using Watchpoint.Rights.Internal.Evaluation;
using Watchpoint.Rights.Internal.Storage;

namespace Watchpoint.Rights.Tests;

/// <summary>A registry, calendars, users and an evaluator wired together for the unit tests.</summary>
internal sealed class Harness : IDisposable
{
    public static readonly DateTimeOffset Noon = new(2026, 11, 30, 12, 0, 0, TimeSpan.Zero);

    public Harness()
    {
        Time = new FakeTimeProvider(Noon);
        Registry = new FakeRegistry();
        Calendars = new FakeCalendars();
        Users = new FakeUsers();
        Types = new FakeTypes();
        Catalogue = new PermissionCatalogue();
        Persistence = new InMemoryRightsPersistence();
        Store = new GrantStore(Persistence);
        Sink = new RecordingSink();
        Evaluator = new RightsEvaluator(Store, Users, Catalogue, Registry, Types, Calendars, Time, Filters, Sink, NullLogger<RightsEvaluator>.Instance);

        Root = Registry.AddLocation(null, "Site");
        BuildingA = Registry.AddLocation(Root, "A");
        Floor1 = Registry.AddLocation(BuildingA, "A1");
        BuildingB = Registry.AddLocation(Root, "B");
        Server1 = TestIds.Server(1);
        Server2 = TestIds.Server(2);
        Door = Types.Add("ac.door", Category.AccessControl);
        SpecialDoor = Types.Add("ac.door.special", Category.AccessControl, parent: Door);
        Camera = Types.Add("video.camera", Category.Video);
        TagFront = TestIds.Of<TagId>(1);
        TagRear = TestIds.Of<TagId>(2);
        DoorA1 = Registry.AddDevice(1, Floor1, Server1, Door, TagFront);
        CameraA = Registry.AddDevice(2, BuildingA, Server1, Camera, TagRear);
        DoorB = Registry.AddDevice(3, BuildingB, Server2, SpecialDoor, TagFront, TagRear);
        CameraB = Registry.AddDevice(4, BuildingB, Server2, Camera);
    }

    public FakeTimeProvider Time { get; }

    public FakeRegistry Registry { get; }

    public FakeCalendars Calendars { get; }

    public FakeUsers Users { get; }

    public FakeTypes Types { get; }

    public PermissionCatalogue Catalogue { get; }

    public InMemoryRightsPersistence Persistence { get; }

    public GrantStore Store { get; }

    public RecordingSink Sink { get; }

    public List<IVisibleScopePostFilter> Filters { get; } = [];

    public RightsEvaluator Evaluator { get; }

    public LocationId Root { get; }

    public LocationId BuildingA { get; }

    public LocationId Floor1 { get; }

    public LocationId BuildingB { get; }

    public ServerId Server1 { get; }

    public ServerId Server2 { get; }

    public TypeId Door { get; }

    public TypeId SpecialDoor { get; }

    public TypeId Camera { get; }

    public TagId TagFront { get; }

    public TagId TagRear { get; }

    public DeviceId DoorA1 { get; }

    public DeviceId CameraA { get; }

    public DeviceId DoorB { get; }

    public DeviceId CameraB { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public UserId NewUser(UserKind kind = UserKind.Person, bool admin = false, bool enabled = true, DateTimeOffset? validUntil = null)
    {
        var id = UserId.New();
        Users.Set(id, new RightsUser(kind, enabled, admin, null, validUntil));
        return id;
    }

    public static RightGrant Grant(UserId user, string permission, GrantScope? scope = null, string? schedule = null, GrantConstraints? constraints = null) =>
        new(RightGrantId.New(), user, PermissionCode.FromTrusted(permission), scope ?? GrantScope.All, schedule, constraints ?? GrantConstraints.None,
            new GrantSource.Manual(), Noon, user);

    public async Task Give(UserId user, params RightGrant[] grants) => await Store.ReplaceAsync(user, grants, CancellationToken.None);

    public static AuthorizationTarget.Device On(DeviceId d) => new(d);

    public AuthorizationDecision Authorize(UserId user, string permission, DeviceId device, ClientKind? client = null) =>
        Evaluator.Authorize(user, PermissionCode.FromTrusted(permission), On(device), Now, client);

    public bool Allowed(UserId user, string permission, DeviceId device) => Authorize(user, permission, device).Allowed;

    public IVisibleScope Scope(UserId user, string permission) => Evaluator.GetVisibleScope(user, PermissionCode.FromTrusted(permission), Now);

    public void Dispose() => Evaluator.Dispose();
}

internal sealed class RecordingSink : IRightsChangedSink
{
    public List<IReadOnlyCollection<UserId>> Calls { get; } = [];

    public void RightsChanged(IReadOnlyCollection<UserId> users) => Calls.Add(users);
}

internal sealed class FakeUsers : IRightsUserLookup
{
    private readonly Dictionary<UserId, RightsUser> _users = [];

    public event EventHandler<UserId>? UserChanged;

    public void Set(UserId id, RightsUser user)
    {
        _users[id] = user;
        UserChanged?.Invoke(this, id);
    }

    public RightsUser? Find(UserId id) => _users.GetValueOrDefault(id);
}

/// <summary>Calendars that are active daily between two hours (UTC).</summary>
internal sealed class FakeCalendars : IRightsCalendars
{
    private readonly Dictionary<string, (int From, int To)> _hours = [];

    public event EventHandler? Changed;

    public void Daily(string key, int fromHour, int toHour)
    {
        _hours[key] = (fromHour, toHour);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Exists(string calendarKey) => _hours.ContainsKey(calendarKey);

    public bool IsActive(string calendarKey, DateTimeOffset instant) =>
        _hours.TryGetValue(calendarKey, out var h) && instant.Hour >= h.From && instant.Hour < h.To;

    public DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after)
    {
        if (!_hours.TryGetValue(calendarKey, out var h))
        {
            return null;
        }

        var best = DateTimeOffset.MaxValue;
        foreach (var hour in new[] { h.From, h.To })
        {
            var t = new DateTimeOffset(after.Year, after.Month, after.Day, hour % 24, 0, 0, TimeSpan.Zero);
            if (t <= after)
            {
                t = t.AddDays(1);
            }

            if (t < best)
            {
                best = t;
            }
        }

        return best;
    }
}

internal sealed class FakeTypes : ITypeCatalog
{
    private readonly Dictionary<TypeId, DeviceTypeDefinition> _types = [];

    public long Revision => 1;

    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public TypeId Add(string id, Category category, TypeId? parent = null, params string[] commands)
    {
        var typeId = TypeId.FromTrusted(id);
        var reference = new TypeRef(typeId, new SemVersion(1, 0, 0));
        var lineage = new List<TypeRef> { reference };
        if (parent is { } p)
        {
            lineage.AddRange(_types[p].Lineage);
        }

        var none = new ParameterSchema(System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());
        _types[typeId] = new DeviceTypeDefinition
        {
            Ref = reference,
            Role = TypeRole.Device,
            Category = category,
            Source = "standard",
            Lineage = lineage,
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>(),
            Properties = new Dictionary<PropertyPath, PropertyDefinition>(),
            Commands = commands.Select(c => CommandPath.FromTrusted(c)).ToDictionary(c => c, c => new CommandDefinition(c, none, Risk.Normal,
                new Dictionary<string, IReadOnlyDictionary<string, Risk>>(), TimeSpan.FromSeconds(5), false, false, null)),
            Events = new Dictionary<EventPath, EventDefinition>(),
            Attributes = none,
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("x", id, "health.connectivity"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
        return typeId;
    }

    public void RaiseChanged() => Changed?.Invoke(this, new CatalogChangedEventArgs(new CatalogChange([], []), 2));

    public Result<DeviceTypeDefinition> Get(TypeRef type) => _types.TryGetValue(type.Id, out var d) ? d : Error.NotFound("model.type.not_found");

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotSupportedException();

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => [.. _types.Values];

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException();

    public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<LocationId, Location> _locations = [];
    private readonly Dictionary<DeviceId, Device> _devices = [];

    public long Revision => 1;

    public Site Site { get; } = new(SiteId.New(), new LocalizedName("Site", new Dictionary<string, string>()), "UTC", 1);

    public event EventHandler<RegistryChangedEventArgs>? Changed;

    public LocationId AddLocation(LocationId? parent, string name)
    {
        var id = LocationId.New();
        _locations[id] = new Location(id, parent, new LocalizedName(name, new Dictionary<string, string>()), LocationKind.Zone, "/" + name, 0, null, null, null, 0, 1);
        return id;
    }

    public DeviceId AddDevice(long n, LocationId location, ServerId server, TypeId type, params TagId[] tags)
    {
        var id = TestIds.Device(n);
        _devices[id] = Make(id, location, server, type, tags);
        return id;
    }

    public void Move(DeviceId device, LocationId location, bool raise = true)
    {
        var d = _devices[device];
        _devices[device] = d with { LocationId = location };
        if (raise)
        {
            Raise();
        }
    }

    public void Retag(DeviceId device, params TagId[] tags)
    {
        _devices[device] = _devices[device] with { Tags = tags };
        Raise();
    }

    public void Raise() => Changed?.Invoke(this, new RegistryChangedEventArgs(new RegistryChangeBatch([]), new Watchpoint.Registry.Contracts.ChangeContext(null, "test")));

    public Result<Location> GetLocation(LocationId id) => _locations.TryGetValue(id, out var l) ? l : Error.NotFound("registry.location.not_found");

    public IReadOnlyList<Location> GetLocationTree() => [.. _locations.Values];

    public IReadOnlyList<Location> GetAncestors(LocationId id) => [];

    public Result<Server> GetServer(ServerId id) => throw new NotSupportedException();

    public Result<Device> GetDevice(DeviceId id) => _devices.TryGetValue(id, out var d) ? d : Error.NotFound("registry.device.not_found");

    public Result<Device> FindDeviceByNativeId(ServerId server, string nativeId) => throw new NotSupportedException();

    public IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection) => [];

    public IReadOnlyList<Device> GetChildren(DeviceId parent) => [];

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both) => [];

    public Page<Device> QueryDevices(DeviceQuery query, PageRequest page) => new([.. _devices.Values.OrderBy(d => d.Id.Value)], null);

    public Page<Server> QueryServers(ServerQuery query, PageRequest page) => new([], null);

    public IReadOnlyList<Tag> GetTags() => [];

    public IReadOnlyList<TagFamily> GetTagFamilies() => [];

    public IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target) => [];

    public DeviceEnrichment GetEnrichment(DeviceId id) => throw new NotSupportedException();

    internal static Device Make(DeviceId id, LocationId location, ServerId server, TypeId type, IReadOnlyList<TagId> tags) => new(id,
        new TypeRef(type, new SemVersion(1, 0, 0)), server, null, id.Value.ToString(), null, "v", location, tags, new Dictionary<string, Watchpoint.Kernel.Values.WpValue>(),
        new Dictionary<CustomFieldId, Watchpoint.Kernel.Values.WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, Harness.Noon, 1);
}
