using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Watchpoint.Registry.Internal.Storage;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.Registry.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed partial class RegistryUnitTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<RegistryDbContext> _options;
    private readonly StorageContextInfo _info;
    private readonly TestTypeCatalog _types = new();
    private readonly FakeTimeProvider _clock = TestTime.CreateProvider();
    private RegistrySnapshot _snapshot;
    private readonly TestDbContextFactory _factory;
    private readonly RegistryOutbox _outbox;
    private readonly DeviceEnrichmentCache _enrichmentCache = new();
    private readonly RegistryChangeFeed _feed = new();
    private readonly RegistryWriter _writer;
    private readonly RegistryReader _reader;
    private readonly ChangeContext _context = new(UserId.New(), "api", null, "unit-test");

    public RegistryUnitTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseSqlite(_connection)
            .Options;
        _info = new StorageContextInfo("Registry", StorageNaming.Schemas, _clock);

        using (var db = new RegistryDbContext(_options, _info))
        {
            db.Database.EnsureCreated();
        }

        var siteId = SiteId.New();
        var rootId = LocationId.New();
        var site = new Site(siteId, new LocalizedName("Test Site", new Dictionary<string, string>()), "UTC", 1);
        var root = new Location(rootId, null, new LocalizedName("Campus", new Dictionary<string, string>()), LocationKind.Site, "/Campus", 0, null, null, null, 0, 1);

        _snapshot = new RegistrySnapshot(
            0,
            site,
            new Dictionary<LocationId, Location> { [rootId] = root },
            new Dictionary<TagFamilyId, TagFamily>(),
            new Dictionary<TagId, Tag>(),
            new Dictionary<ServerId, Server>(),
            new Dictionary<DeviceId, Device>(),
            [],
            new Dictionary<CustomFieldId, CustomFieldDefinition>());

        _factory = new TestDbContextFactory(_options, _info);
        _outbox = new RegistryOutbox(_factory, _clock);
        _writer = new RegistryWriter(_factory, _types, _outbox, _clock, NullLogger<RegistryWriter>.Instance, s => _snapshot = s, () => _snapshot, _enrichmentCache, _feed);
        _reader = new RegistryReader(() => _snapshot, _enrichmentCache, _feed);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _connection.Dispose();
    }

    /// <summary>The single root location of the freshly-created snapshot.</summary>
    private Location Root => _reader.GetLocationTree().Single(l => l.ParentId == null);

    [Fact]
    public async Task REG_U_001_Site_configuration_read_and_update()
    {
        var current = _reader.Site;
        current.DefaultTimezone.ShouldBe("UTC");

        var updated = current with { DefaultTimezone = "Europe/Paris" };
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpdateSite(updated, ExpectedVersion: current.Version)]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        _reader.Site.DefaultTimezone.ShouldBe("Europe/Paris");
        _reader.Site.Version.ShouldBe(current.Version + 1);
    }

    [Fact]
    public async Task REG_U_002_Location_tree_creation_and_traversal()
    {
        var root = Root;
        var bId = LocationId.New();
        var fId = LocationId.New();

        var b = new Location(bId, root.Id, new LocalizedName("Building 1", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 1, 0);
        var f = new Location(fId, bId, new LocalizedName("Floor 1", new Dictionary<string, string>()), LocationKind.Floor, "", 0, null, null, null, 1, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(b),
            new RegistryChange.UpsertLocation(f)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();

        var locF = _reader.GetLocation(fId).Value;
        locF.Path.ShouldBe("/Campus/Building 1/Floor 1");
        locF.Depth.ShouldBe(2);

        var ancestorsOfF = _reader.GetAncestors(fId);
        ancestorsOfF.Select(l => l.Id).ShouldBe([root.Id, bId]);

        var tree = _reader.GetLocationTree();
        tree.Select(l => l.Id).ShouldBe([root.Id, bId, fId]);
    }

    [Fact]
    public async Task REG_U_003_Location_path_and_depth_computation()
    {
        var root = Root;
        var locId = LocationId.New();
        var loc = new Location(locId, root.Id, new LocalizedName("North Wing", new Dictionary<string, string>()), LocationKind.Zone, "", 0, null, null, null, 0, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(loc)]), _context, TestContext.Current.CancellationToken);

        var saved = _reader.GetLocation(locId).Value;
        saved.Path.ShouldBe("/Campus/North Wing");
        saved.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task REG_U_004_Location_move_rewrites_descendant_paths()
    {
        var root = Root;
        var b1Id = LocationId.New();
        var b2Id = LocationId.New();
        var rId = LocationId.New();

        var b1 = new Location(b1Id, root.Id, new LocalizedName("B1", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 1, 0);
        var b2 = new Location(b2Id, root.Id, new LocalizedName("B2", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 2, 0);
        var room = new Location(rId, b1Id, new LocalizedName("Room A", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 1, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(b1),
            new RegistryChange.UpsertLocation(b2),
            new RegistryChange.UpsertLocation(room)
        ]), _context, TestContext.Current.CancellationToken);

        _reader.GetLocation(rId).Value.Path.ShouldBe("/Campus/B1/Room A");

        // Move Room A to B2
        var moved = _reader.GetLocation(rId).Value with { ParentId = b2Id };
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(moved, ExpectedVersion: moved.Version)]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        _reader.GetLocation(rId).Value.Path.ShouldBe("/Campus/B2/Room A");
    }

    [Fact]
    public async Task REG_U_005_Location_deletion_checks()
    {
        var root = Root;
        var locId = LocationId.New();
        var srvId = ServerId.New();

        var loc = new Location(locId, root.Id, new LocalizedName("Zone X", new Dictionary<string, string>()), LocationKind.Zone, "", 0, null, null, null, 0, 0);
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv-x", null, "Vendor", locId, [], new Dictionary<string, WpValue>(), true, false, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(loc),
            new RegistryChange.UpsertServer(srv)
        ]), _context, TestContext.Current.CancellationToken);

        // Deleting the location without ReassignTo while a server resides on it fails.
        var delRes = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteLocation(locId)]), _context, TestContext.Current.CancellationToken);
        delRes.IsSuccess.ShouldBeFalse();
        delRes.Error.Code.Value.ShouldBe("registry.location.not_empty");

        // With ReassignTo, the server moves to the new location and the delete succeeds.
        var reassignRes = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteLocation(locId, ReassignTo: root.Id)]), _context, TestContext.Current.CancellationToken);
        reassignRes.IsSuccess.ShouldBeTrue();
        _reader.GetLocation(locId).IsSuccess.ShouldBeFalse();
        _reader.GetServer(srvId).Value.LocationId.ShouldBe(root.Id);
    }

    [Fact]
    public async Task REG_U_006_Tag_family_and_tag_lifecycle()
    {
        var famId = TagFamilyId.New();
        var tagId = TagId.New();

        var fam = new TagFamily(famId, "priority", new LocalizedName("Priority", new Dictionary<string, string>()), "#ff0000", TagCardinality.ZeroOrOne, 0);
        var tag = new Tag(tagId, famId, "high", new LocalizedName("High", new Dictionary<string, string>()), "#ff0000", 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(tag)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();

        _reader.GetTagFamilies().ShouldContain(f => f.Id == famId);
        _reader.GetTags().Single(t => t.Id == tagId).Key.ShouldBe("high");
    }

    [Fact]
    public async Task REG_U_007_Tag_cardinality_validation()
    {
        var root = Root;
        var famId = TagFamilyId.New();
        var t1Id = TagId.New();
        var t2Id = TagId.New();
        var srvId = ServerId.New();
        var devId = DeviceId.New();

        var fam = new TagFamily(famId, "tier", new LocalizedName("Tier", new Dictionary<string, string>()), null, TagCardinality.ZeroOrOne, 0);
        var t1 = new Tag(t1Id, famId, "gold", new LocalizedName("Gold", new Dictionary<string, string>()), null, 0);
        var t2 = new Tag(t2Id, famId, "silver", new LocalizedName("Silver", new Dictionary<string, string>()), null, 0);
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Door", root.Id, [t1Id, t2Id], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(t1),
            new RegistryChange.UpsertTag(t2),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.tag.family_exclusive");
    }

    [Fact]
    public async Task REG_U_008_Server_registration_and_lookup()
    {
        var root = Root;
        var srvId = ServerId.New();
        var connId = ConnectionId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), connId, null, "srv-100", null, "VendorMain", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv)]), _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        var found = _reader.GetServersOfConnection(connId).SingleOrDefault(s => s.NativeId == "srv-100");
        found.ShouldNotBeNull();
        found.Id.ShouldBe(srvId);
    }

    [Fact]
    public async Task REG_U_009_Server_parent_hierarchy_and_depth()
    {
        var root = Root;
        var connId = ConnectionId.New();
        var s1Id = ServerId.New();
        var s2Id = ServerId.New();

        var s1 = new Server(s1Id, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), connId, null, "s1", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var s2 = new Server(s2Id, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), connId, s1Id, "s2", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(s1),
            new RegistryChange.UpsertServer(s2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        _reader.GetServer(s2Id).Value.ParentId.ShouldBe(s1Id);

        // Parent on another connection is rejected with the parent_invalid reason.
        var otherConn = ConnectionId.New();
        var s3 = new Server(ServerId.New(), new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), otherConn, s1Id, "s3", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var badRes = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(s3)]), _context, TestContext.Current.CancellationToken);
        badRes.IsSuccess.ShouldBeFalse();
        badRes.Error.Code.Value.ShouldBe("registry.server.parent_invalid");
        badRes.Error.Args["reason"].ShouldBe("otherConnection");
    }

    [Fact]
    public async Task REG_U_010_Device_registration_and_lookup()
    {
        var root = Root;
        var srvId = ServerId.New();
        var devId = DeviceId.New();

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "door-nat", null, "VendorDoor", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]), _context, TestContext.Current.CancellationToken);

        var found = _reader.FindDeviceByNativeId(srvId, "door-nat");
        found.IsSuccess.ShouldBeTrue();
        found.Value.Id.ShouldBe(devId);
    }

    [Fact]
    public async Task REG_U_011_Device_parent_hierarchy_and_allowed_types()
    {
        var root = Root;
        var srvId = ServerId.New();
        var d1Id = DeviceId.New();
        var d2Id = DeviceId.New();

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d1 = new Device(d1Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Door", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(d2Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, d1Id, "d2", null, "SubDoor", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        _reader.GetDevice(d2Id).Value.ParentId.ShouldBe(d1Id);
        _reader.GetChildren(d1Id).Select(d => d.Id).ShouldBe([d2Id]);

        // Parent on another server is rejected.
        var otherSrvId = ServerId.New();
        var otherSrv = new Server(otherSrvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv2", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d3 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), otherSrvId, d1Id, "d3", null, "D3", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var badRes = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(otherSrv), new RegistryChange.UpsertDevice(d3)]), _context, TestContext.Current.CancellationToken);
        badRes.IsSuccess.ShouldBeFalse();
        badRes.Error.Code.Value.ShouldBe("registry.device.parent_invalid");
        badRes.Error.Args["reason"].ShouldBe("otherServer");
    }

    [Fact]
    public async Task REG_U_012_Device_patching_semantics()
    {
        var root = Root;
        var srvId = ServerId.New();
        var devId = DeviceId.New();

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Door", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]), _context, TestContext.Current.CancellationToken);

        // Patch: only enable maintenance
        var patch = new DevicePatch(Maintenance: true);
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.PatchDevice(devId, patch, ExpectedVersion: 1)]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        var patched = _reader.GetDevice(devId).Value;
        patched.Maintenance.ShouldBeTrue();
        patched.Enabled.ShouldBeTrue(); // unchanged
        patched.VendorName.ShouldBe("Door"); // unchanged
    }

    [Fact]
    public async Task REG_U_013_Device_deletion_and_unlinking()
    {
        var root = Root;
        var srvId = ServerId.New();
        var d1Id = DeviceId.New();
        var d2Id = DeviceId.New();

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d1 = new Device(d1Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Door1", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(d2Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d2", null, "Door2", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var linkId = LinkId.New();
        var link = new DeviceLink(linkId, LinkKind.Controls, d1Id, d2Id, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2),
            new RegistryChange.UpsertLink(link)
        ]), _context, TestContext.Current.CancellationToken);

        // Delete d1: its link is deleted along with it (no explicit DeleteLink needed).
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.DeleteDevice(d1Id, DeleteChildren: false)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        _reader.GetDevice(d1Id).IsSuccess.ShouldBeFalse();
        _reader.GetLinks(d2Id, direction: LinkDirection.Incoming).ShouldBeEmpty();
    }

    [Fact]
    public async Task REG_U_014_Device_links_CRUD_and_queries()
    {
        var root = Root;
        var srvId = ServerId.New();
        var d1Id = DeviceId.New();
        var d2Id = DeviceId.New();

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d1 = new Device(d1Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Door1", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(d2Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d2", null, "Door2", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var linkId = LinkId.New();
        var link = new DeviceLink(linkId, LinkKind.MemberOf, d1Id, d2Id, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2),
            new RegistryChange.UpsertLink(link)
        ]), _context, TestContext.Current.CancellationToken);

        var outgoing = _reader.GetLinks(d1Id, direction: LinkDirection.Outgoing);
        outgoing.Count.ShouldBe(1);
        outgoing[0].Kind.ShouldBe(LinkKind.MemberOf);

        var incoming = _reader.GetLinks(d2Id, direction: LinkDirection.Incoming);
        incoming.Count.ShouldBe(1);
        incoming[0].From.ShouldBe(d1Id);

        var byKind = _reader.GetLinks(d1Id, LinkKind.MemberOf);
        byKind.Count.ShouldBe(1);
        var byOtherKind = _reader.GetLinks(d1Id, LinkKind.Controls);
        byOtherKind.ShouldBeEmpty();

        var delRes = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteLink(linkId)]), _context, TestContext.Current.CancellationToken);
        delRes.IsSuccess.ShouldBeTrue();
        _reader.GetLinks(d1Id, direction: LinkDirection.Outgoing).ShouldBeEmpty();
    }

    [Fact]
    public async Task REG_U_015_Custom_field_definitions_and_values()
    {
        var cfId = CustomFieldId.New();
        var cf = new CustomFieldDefinition(cfId, CustomFieldTarget.Device, "asset_tag", new LocalizedName("Asset Tag", new Dictionary<string, string>()), WpValueKind.String, false, null, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertCustomField(cf)]), _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        _reader.GetCustomFields(CustomFieldTarget.Device).ShouldContain(c => c.Id == cfId);
    }

    [Fact]
    public async Task REG_U_016_Keyset_paging_for_devices()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var changes = new List<RegistryChange> { new RegistryChange.UpsertServer(srv) };
        for (var i = 1; i <= 5; i++)
        {
            changes.Add(new RegistryChange.UpsertDevice(new Device(
                DeviceId.New(),
                new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value),
                srvId,
                null,
                $"d-{i:D2}",
                null,
                $"Door {i:D2}",
                root.Id,
                [],
                new Dictionary<string, WpValue>(),
                new Dictionary<CustomFieldId, WpValue>(),
                new HashSet<CapabilityId>(),
                null,
                true,
                false,
                false,
                default,
                0)));
        }

        await _writer.ApplyAsync(new RegistryChangeBatch(changes), _context, TestContext.Current.CancellationToken);

        var p1 = _reader.QueryDevices(new DeviceQuery(), new PageRequest(2));
        p1.Items.Count.ShouldBe(2);
        p1.NextCursor.ShouldNotBeNull();

        var p2 = _reader.QueryDevices(new DeviceQuery(), new PageRequest(2, p1.NextCursor));
        p2.Items.Count.ShouldBe(2);
        p2.NextCursor.ShouldNotBeNull();

        var p3 = _reader.QueryDevices(new DeviceQuery(), new PageRequest(2, p2.NextCursor));
        p3.Items.Count.ShouldBe(1);
        p3.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task REG_U_017_Text_search_normalization_accents_and_arabic()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var d1Id = DeviceId.New();
        var d2Id = DeviceId.New();

        var d1 = new Device(d1Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Entrée Principale", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(d2Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d2", null, "بوابة الدخول", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2)
        ]), _context, TestContext.Current.CancellationToken);

        // Search without diacritics
        var resFrench = _reader.QueryDevices(new DeviceQuery(SearchText: "entree"), new PageRequest(10));
        resFrench.Items.Count.ShouldBe(1);
        resFrench.Items[0].Id.ShouldBe(d1Id);

        // Search Arabic substring
        var resArabic = _reader.QueryDevices(new DeviceQuery(SearchText: "الدخول"), new PageRequest(10));
        resArabic.Items.Count.ShouldBe(1);
        resArabic.Items[0].Id.ShouldBe(d2Id);
    }

    [Fact]
    public async Task REG_U_018_Query_filter_by_location_subtree()
    {
        var root = Root;
        var b1Id = LocationId.New();
        var b2Id = LocationId.New();

        var b1 = new Location(b1Id, root.Id, new LocalizedName("B1", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 1, 0);
        var b2 = new Location(b2Id, root.Id, new LocalizedName("B2", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 2, 0);
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var devB1 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d-b1", null, "D1", b1Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var devB2 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d-b2", null, "D2", b2Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(b1),
            new RegistryChange.UpsertLocation(b2),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(devB1),
            new RegistryChange.UpsertDevice(devB2)
        ]), _context, TestContext.Current.CancellationToken);

        var qB1 = _reader.QueryDevices(new DeviceQuery(LocationId: b1Id), new PageRequest(10));
        qB1.Items.Count.ShouldBe(1);
        qB1.Items[0].LocationId.ShouldBe(b1Id);
    }

    [Fact]
    public async Task REG_U_019_Query_filter_by_tags_and_status()
    {
        var root = Root;
        var famId = TagFamilyId.New();
        var tagId = TagId.New();
        var srvId = ServerId.New();

        var fam = new TagFamily(famId, "env", new LocalizedName("Env", new Dictionary<string, string>()), null, TagCardinality.ZeroOrMore, 0);
        var tag = new Tag(tagId, famId, "prod", new LocalizedName("Prod", new Dictionary<string, string>()), null, 0);
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var d1 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "D1", root.Id, [tagId], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d2", null, "D2", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, false, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(tag),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2)
        ]), _context, TestContext.Current.CancellationToken);

        var filtered = _reader.QueryDevices(new DeviceQuery(Tags: [tagId], Enabled: true), new PageRequest(10));
        filtered.Items.Count.ShouldBe(1);
        filtered.Items[0].NativeId.ShouldBe("d1");
    }

    [Fact]
    public async Task REG_U_020_Optimistic_concurrency_conflict_handling()
    {
        var site = _reader.Site;
        var conflictBatch = new RegistryChangeBatch([
            new RegistryChange.UpdateSite(site with { DefaultTimezone = "Asia/Tokyo" }, ExpectedVersion: 999)
        ]);

        var res = await _writer.ApplyAsync(conflictBatch, _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("common.version.conflict");
    }

    [Fact]
    public async Task REG_U_021_Invariant_R1_valid_time_zone()
    {
        var site = _reader.Site;
        var invalidSiteBatch = new RegistryChangeBatch([
            new RegistryChange.UpdateSite(site with { DefaultTimezone = "Invalid/NonExistent/TZ" })
        ]);

        var res = await _writer.ApplyAsync(invalidSiteBatch, _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("common.value.invalid_format");
    }

    [Fact]
    public async Task REG_U_022_Invariant_R2_location_tree_validation()
    {
        // Creating a location with a non-existent parent fails with the specific not-found code.
        var badLoc = new Location(LocationId.New(), LocationId.New(), new LocalizedName("Ghost", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 0, 0);
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(badLoc)]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.location.not_found");
    }

    [Fact]
    public async Task REG_U_023_Invariant_R5_server_native_id_uniqueness()
    {
        var root = Root;
        var connId = ConnectionId.New();
        var s1 = new Server(ServerId.New(), new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), connId, null, "srv-dup", null, "V1", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var s2 = new Server(ServerId.New(), new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), connId, null, "srv-dup", null, "V2", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(s1),
            new RegistryChange.UpsertServer(s2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.server.duplicate_native_id");
    }

    [Fact]
    public async Task REG_U_024_Invariant_R5_device_native_id_uniqueness()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d1 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "door-dup", null, "D1", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "door-dup", null, "D2", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.device.duplicate_native_id");
    }

    [Fact]
    public async Task REG_U_025_Invariant_R4_type_catalog_resolution()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("unknown.type"), SemVersion.Parse("1.0.0").Value), srvId, null, "u1", null, "Unknown", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("model.type.not_found");
    }

    [Fact]
    public async Task REG_U_026_Invariant_R3_tag_family_exclusivity_enforcement()
    {
        var famId = TagFamilyId.New();
        var t1Id = TagId.New();
        var t2Id = TagId.New();
        var fam = new TagFamily(famId, "single", new LocalizedName("Single", new Dictionary<string, string>()), null, TagCardinality.ZeroOrOne, 0);
        var t1 = new Tag(t1Id, famId, "t1", new LocalizedName("T1", new Dictionary<string, string>()), null, 0);
        var t2 = new Tag(t2Id, famId, "t2", new LocalizedName("T2", new Dictionary<string, string>()), null, 0);

        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d", null, "D", root.Id, [t1Id, t2Id], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(t1),
            new RegistryChange.UpsertTag(t2),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.tag.family_exclusive");
    }

    [Fact]
    public async Task REG_U_027_Invariant_R9_link_endpoints_and_self_link_checks()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d1Id = DeviceId.New();
        var d1 = new Device(d1Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "D1", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        // Self-link attempt
        var selfLink = new DeviceLink(LinkId.New(), LinkKind.Controls, d1Id, d1Id, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertLink(selfLink)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.link.invalid");
        res.Error.Args["reason"].ShouldBe("selfLink");
    }

    [Fact]
    public async Task REG_U_028_Invariant_R8_custom_fields_validation()
    {
        var cf1 = new CustomFieldDefinition(CustomFieldId.New(), CustomFieldTarget.Device, "same_key", new LocalizedName("A", new Dictionary<string, string>()), WpValueKind.String, false, null, 0);
        var cf2 = new CustomFieldDefinition(CustomFieldId.New(), CustomFieldTarget.Device, "same_key", new LocalizedName("B", new Dictionary<string, string>()), WpValueKind.String, false, null, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertCustomField(cf1),
            new RegistryChange.UpsertCustomField(cf2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("common.value.duplicate");
    }

    [Fact]
    public void REG_U_029_Device_enrichment_cache_updates()
    {
        var devId = DeviceId.New();
        var enrichment = new DeviceEnrichment(devId, "Door", ServerId.New(), LocationId.New(), "/Campus/Building 1", ["Campus", "Building 1"], [], new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), [], true, false);

        _enrichmentCache.Put(enrichment);
        var cached = _enrichmentCache.Get(devId);
        cached.ShouldNotBeNull();
        cached.LocationPath.ShouldBe("/Campus/Building 1");

        _enrichmentCache.Remove(devId);
        _enrichmentCache.Get(devId).ShouldBeNull();
    }

    [Fact]
    public async Task REG_U_030_Registry_change_recorded_in_the_outbox()
    {
        var site = _reader.Site;
        var batch = new RegistryChangeBatch([new RegistryChange.UpdateSite(site with { DefaultTimezone = "UTC" })]);

        var res = await _writer.ApplyAsync(batch, _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        var unsent = await _outbox.ReadUnsentAsync(10, TestContext.Current.CancellationToken);
        unsent.Count.ShouldBe(1);
        unsent[0].Revision.ShouldBe(1);
        unsent[0].CreatedAt.ShouldBe(_clock.GetUtcNow());
        unsent[0].Changes.ShouldBe([new EntityChange("site", site.Id.Value, ChangeKind.Updated)]);
        unsent[0].Context.ShouldBe(_context);
    }

    private sealed class TestDbContextFactory(DbContextOptions<RegistryDbContext> options, StorageContextInfo info) : IDbContextFactory<RegistryDbContext>
    {
        public RegistryDbContext CreateDbContext() => new(options, info);
        public Task<RegistryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RegistryDbContext(options, info));
    }

    private sealed class TestTypeCatalog : ITypeCatalog
    {
        public long Revision => 1;

        public event EventHandler<CatalogChangedEventArgs>? Changed { add { } remove { } }

        public Result<DeviceTypeDefinition> Get(TypeRef type)
        {
            if (type.Id.Value == "unknown.type")
                return Error.NotFound("model.type.not_found");

            return new DeviceTypeDefinition
            {
                Ref = type,
                Role = type.Id.Value.Contains("server", StringComparison.OrdinalIgnoreCase) ? TypeRole.Server : TypeRole.Device,
                Category = Category.AccessControl,
                Source = "standard",
                Lineage = [type],
                Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>(),
                Properties = new Dictionary<PropertyPath, PropertyDefinition>(),
                Commands = new Dictionary<CommandPath, CommandDefinition>(),
                Events = new Dictionary<EventPath, EventDefinition>(),
                Attributes = ParameterSchema.Parse(JsonDocument.Parse("{}").RootElement).Value,
                AllowedChildTypes = [],
                LicenceClass = LicenceClass.Full,
                Display = new TypeDisplay("door", "ac.door", "doorMode.mode"),
                StateOverrides = [],
                DefaultAlarmRules = [],
                DefaultLinks = []
            };
        }

        public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotImplementedException();
        public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => throw new NotImplementedException();
        public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotImplementedException();
        public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotImplementedException();
    }
}
