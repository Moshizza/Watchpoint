using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Xunit;

namespace Watchpoint.Registry.Tests;

/// <summary>
/// Coverage of REG §9 error codes and new T1.07c reader/writer members not already exercised by the adapted
/// REG-U-001…030 tests (new ids, next free after REG-U-051/053 per CLAUDE.md §3).
/// </summary>
public sealed partial class RegistryUnitTests
{
    [Fact]
    public async Task REG_U_053_Readiness_unhealthy_with_reason_siteId_on_mismatch()
    {
        var configured = SiteId.New();
        var check = new RegistrySiteHealthCheck(_reader, configured);

        // The fixture's snapshot carries a different site id than `configured`.
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data["reason"].ShouldBe("siteId");

        var matching = new RegistrySiteHealthCheck(_reader, _reader.Site.Id);
        (await matching.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void REG_U_054_Site_not_initialised_error_shape()
    {
        var error = RegistryErrors.SiteNotInitialised();
        error.Code.Value.ShouldBe("registry.site.not_initialised");
        error.Kind.ShouldBe(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task REG_U_055_Not_found_codes_for_missing_entities()
    {
        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteServer(ServerId.New(), DeleteDevices: false)]), _context, TestContext.Current.CancellationToken))
            .Error.Code.Value.ShouldBe("registry.server.not_found");

        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteDevice(DeviceId.New(), DeleteChildren: false)]), _context, TestContext.Current.CancellationToken))
            .Error.Code.Value.ShouldBe("registry.device.not_found");

        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteTag(TagId.New())]), _context, TestContext.Current.CancellationToken))
            .Error.Code.Value.ShouldBe("registry.tag.not_found");

        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteLink(LinkId.New())]), _context, TestContext.Current.CancellationToken))
            .Error.Code.Value.ShouldBe("registry.link.not_found");

        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteCustomField(CustomFieldId.New())]), _context, TestContext.Current.CancellationToken))
            .Error.Code.Value.ShouldBe("registry.custom_field.not_found");

        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteLocation(LocationId.New())]), _context, TestContext.Current.CancellationToken))
            .Error.Code.Value.ShouldBe("registry.location.not_found");
    }

    [Fact]
    public async Task REG_U_056_Location_cycle_rejected()
    {
        var root = Root;
        var aId = LocationId.New();
        var bId = LocationId.New();
        var a = new Location(aId, root.Id, new LocalizedName("A", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 0, 0);
        var b = new Location(bId, aId, new LocalizedName("B", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 0, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(a), new RegistryChange.UpsertLocation(b)]), _context, TestContext.Current.CancellationToken);

        // Move A under its own descendant B: a cycle.
        var moved = _reader.GetLocation(aId).Value with { ParentId = bId };
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(moved, ExpectedVersion: moved.Version)]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.location.cycle");
    }

    [Fact]
    public async Task REG_U_057_Location_depth_limit()
    {
        var parentId = Root.Id;
        RegistryChangeBatch? lastBatch = null;
        for (var depth = 1; depth <= 17; depth++)
        {
            var id = LocationId.New();
            var loc = new Location(id, parentId, new LocalizedName($"L{depth}", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 0, 0);
            lastBatch = new RegistryChangeBatch([new RegistryChange.UpsertLocation(loc)]);
            var res = await _writer.ApplyAsync(lastBatch, _context, TestContext.Current.CancellationToken);
            if (!res.IsSuccess)
            {
                res.Error.Code.Value.ShouldBe("registry.location.too_deep");
                return;
            }

            parentId = id;
        }

        Assert.Fail("Expected the location depth limit to be reached.");
    }

    [Fact]
    public async Task REG_U_058_Location_sibling_duplicate_name_rejected()
    {
        var root = Root;
        var l1 = new Location(LocationId.New(), root.Id, new LocalizedName("Floor 1", new Dictionary<string, string>()), LocationKind.Floor, "", 0, null, null, null, 0, 0);
        var l2 = new Location(LocationId.New(), root.Id, new LocalizedName("FLOOR 1", new Dictionary<string, string>()), LocationKind.Floor, "", 0, null, null, null, 1, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(l1),
            new RegistryChange.UpsertLocation(l2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.location.duplicate_name");
    }

    [Fact]
    public async Task REG_U_059_Root_location_immutable()
    {
        var root = Root;

        var deleteRes = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteLocation(root.Id)]), _context, TestContext.Current.CancellationToken);
        deleteRes.IsSuccess.ShouldBeFalse();
        deleteRes.Error.Code.Value.ShouldBe("registry.location.root_immutable");

        var otherId = LocationId.New();
        var other = new Location(otherId, root.Id, new LocalizedName("Other", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 0, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(other)]), _context, TestContext.Current.CancellationToken);

        var moveRes = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(root with { ParentId = otherId }, ExpectedVersion: root.Version)
        ]), _context, TestContext.Current.CancellationToken);
        moveRes.IsSuccess.ShouldBeFalse();
        moveRes.Error.Code.Value.ShouldBe("registry.location.root_immutable");
    }

    [Fact]
    public async Task REG_U_060_Device_parent_type_not_allowed()
    {
        var root = Root;
        using var writer = new RegistryWriter(_factory, new RestrictiveChildTypeCatalog(), _outbox, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<RegistryWriter>.Instance, s => _snapshot = s, () => _snapshot, _enrichmentCache, _feed);

        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var parentId = DeviceId.New();
        var parent = new Device(parentId, new TypeRef(TypeId.FromTrusted("restrictive.parent"), SemVersion.Parse("1.0.0").Value), srvId, null, "parent", null, "Parent", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var child = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, parentId, "child", null, "Child", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var res = await writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(parent),
            new RegistryChange.UpsertDevice(child)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.device.parent_invalid");
        res.Error.Args["reason"].ShouldBe("typeNotAllowed");
    }

    /// <summary>A type catalog whose <c>restrictive.parent</c> type only allows a disjoint set of child types (REG-U-060).</summary>
    private sealed class RestrictiveChildTypeCatalog : ITypeCatalog
    {
        public long Revision => 1;
        public event EventHandler<CatalogChangedEventArgs>? Changed { add { } remove { } }

        public Result<DeviceTypeDefinition> Get(TypeRef type) => new DeviceTypeDefinition
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
            Attributes = ParameterSchema.Parse(System.Text.Json.JsonDocument.Parse("{}").RootElement).Value,
            AllowedChildTypes = type.Id.Value == "restrictive.parent" ? ["other.type"] : [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("door", "ac.door", "doorMode.mode"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = []
        };

        public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotImplementedException();
        public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => throw new NotImplementedException();
        public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotImplementedException();
        public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) => throw new NotImplementedException();
        public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task REG_U_061_Tag_duplicate_key_rejected()
    {
        var famId = TagFamilyId.New();
        var fam = new TagFamily(famId, "env", new LocalizedName("Env", new Dictionary<string, string>()), null, TagCardinality.ZeroOrMore, 0);
        var t1 = new Tag(TagId.New(), famId, "prod", new LocalizedName("Prod", new Dictionary<string, string>()), null, 0);
        var t2 = new Tag(TagId.New(), famId, "prod", new LocalizedName("Prod 2", new Dictionary<string, string>()), null, 0);

        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(t1),
            new RegistryChange.UpsertTag(t2)
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.tag.duplicate_key");
    }

    [Fact]
    public async Task REG_U_062_Device_has_children_requires_delete_children_flag()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var parentId = DeviceId.New();
        var parent = new Device(parentId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "parent", null, "Parent", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var childId = DeviceId.New();
        var child = new Device(childId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, parentId, "child", null, "Child", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(parent),
            new RegistryChange.UpsertDevice(child)
        ]), _context, TestContext.Current.CancellationToken);

        var blocked = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteDevice(parentId, DeleteChildren: false)]), _context, TestContext.Current.CancellationToken);
        blocked.IsSuccess.ShouldBeFalse();
        blocked.Error.Code.Value.ShouldBe("registry.device.has_children");

        var cascaded = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteDevice(parentId, DeleteChildren: true)]), _context, TestContext.Current.CancellationToken);
        cascaded.IsSuccess.ShouldBeTrue();
        _reader.GetDevice(parentId).IsSuccess.ShouldBeFalse();
        _reader.GetDevice(childId).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task REG_U_063_Server_not_empty_requires_delete_devices_flag()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var devId = DeviceId.New();
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "D1", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var blocked = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteServer(srvId, DeleteDevices: false)]), _context, TestContext.Current.CancellationToken);
        blocked.IsSuccess.ShouldBeFalse();
        blocked.Error.Code.Value.ShouldBe("registry.server.not_empty");

        var cascaded = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.DeleteServer(srvId, DeleteDevices: true)]), _context, TestContext.Current.CancellationToken);
        cascaded.IsSuccess.ShouldBeTrue();
        _reader.GetServer(srvId).IsSuccess.ShouldBeFalse();
        _reader.GetDevice(devId).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task REG_U_064_Duplicate_link_rejected()
    {
        var root = Root;
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var d1Id = DeviceId.New();
        var d2Id = DeviceId.New();
        var d1 = new Device(d1Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "D1", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var d2 = new Device(d2Id, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d2", null, "D2", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(d1),
            new RegistryChange.UpsertDevice(d2),
            new RegistryChange.UpsertLink(new DeviceLink(LinkId.New(), LinkKind.Controls, d1Id, d2Id, 0))
        ]), _context, TestContext.Current.CancellationToken);

        // Same (kind, from, to) under a different link id.
        var res = await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLink(new DeviceLink(LinkId.New(), LinkKind.Controls, d1Id, d2Id, 0))
        ]), _context, TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.link.invalid");
        res.Error.Args["reason"].ShouldBe("duplicate");
    }

    [Fact]
    public async Task REG_U_065_Batch_change_count_limit()
    {
        var changes = new List<RegistryChange>();
        for (var i = 0; i < RegistryWriter.MaxBatchChanges + 1; i++)
        {
            changes.Add(new RegistryChange.UpsertTag(new Tag(TagId.New(), TagFamilyId.New(), $"k{i}", new LocalizedName($"K{i}", new Dictionary<string, string>()), null, 0)));
        }

        var res = await _writer.ApplyAsync(new RegistryChangeBatch(changes), _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("registry.batch.too_large");
        res.Error.Args["max"].ShouldBe(RegistryWriter.MaxBatchChanges);
    }

    [Fact]
    public async Task REG_U_066_Revision_increments_with_every_applied_batch()
    {
        _reader.Revision.ShouldBe(0);

        var res1 = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpdateSite(_reader.Site with { DefaultTimezone = "Europe/Paris" })]), _context, TestContext.Current.CancellationToken);
        res1.IsSuccess.ShouldBeTrue();
        _reader.Revision.ShouldBe(1);

        var res2 = await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpdateSite(_reader.Site with { DefaultTimezone = "UTC" })]), _context, TestContext.Current.CancellationToken);
        res2.IsSuccess.ShouldBeTrue();
        _reader.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task REG_U_067_GetEnrichment_computes_caches_and_invalidates_on_location_rename()
    {
        var root = Root;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var otherDevId = DeviceId.New();

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "V", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var locId = LocationId.New();
        var loc = new Location(locId, root.Id, new LocalizedName("Lobby", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 0, 0);
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d1", null, "Door", locId, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var otherDev = new Device(otherDevId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "d2", null, "Other", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertLocation(loc),
            new RegistryChange.UpsertDevice(dev),
            new RegistryChange.UpsertDevice(otherDev)
        ]), _context, TestContext.Current.CancellationToken);

        var enrichment = _reader.GetEnrichment(devId);
        enrichment.LocationNames.ShouldBe(["Campus", "Lobby"]);
        var otherEnrichment = _reader.GetEnrichment(otherDevId);

        // Rename the device's location: its enrichment is invalidated and recomputed; the unrelated device's cached
        // enrichment (returned by reference from the cache) is untouched.
        var renamed = _reader.GetLocation(locId).Value with { Name = new LocalizedName("Reception", new Dictionary<string, string>()) };
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(renamed, ExpectedVersion: renamed.Version)]), _context, TestContext.Current.CancellationToken);

        _reader.GetEnrichment(devId).LocationNames.ShouldBe(["Campus", "Reception"]);
        _reader.GetEnrichment(otherDevId).ShouldBeSameAs(otherEnrichment);
    }
}
