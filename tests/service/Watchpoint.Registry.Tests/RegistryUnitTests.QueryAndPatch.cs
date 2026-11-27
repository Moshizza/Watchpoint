using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Xunit;

namespace Watchpoint.Registry.Tests;

// DeviceQuery / DevicePatch in the REG §5–§6 shape (aligned in T1.19). Method names carry the REG §14 ids of the
// behaviour they cover; older tests in this class predate the current numbering.
public sealed partial class RegistryUnitTests
{
    private static readonly SemVersion _v1 = SemVersion.Parse("1.0.0").Value;

    private async Task<(ServerId Server, Device[] Devices)> SeedQueryDevicesAsync(params (string NativeId, string Type, IReadOnlyList<TagId> Tags, DeviceId? Parent)[] devices)
    {
        var srvId = ServerId.New();
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), _v1), ConnectionId.New(), null, "srv", null, "V", Root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var built = devices.Select((d, i) => new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted(d.Type), _v1), srvId, d.Parent, d.NativeId, null, d.NativeId.ToUpperInvariant(), Root.Id, d.Tags,
            new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, DateTimeOffset.UnixEpoch.AddDays(devices.Length - i), 0)).ToArray();
        var changes = new List<RegistryChange> { new RegistryChange.UpsertServer(srv) };
        changes.AddRange(built.Select(d => new RegistryChange.UpsertDevice(d)));
        (await _writer.ApplyAsync(new RegistryChangeBatch(changes), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        return (srvId, built);
    }

    [Fact]
    public async Task REG_U_023_Query_by_type_includes_descendants()
    {
        await SeedQueryDevicesAsync(("door-a", "ac.door", [], null), ("door-b", "vendor.x.door", [], null), ("cam", "video.camera", [], null));

        var doors = _reader.QueryDevices(new DeviceQuery(Types: [TypeId.FromTrusted("ac.door")]), new PageRequest(10)).Items;

        doors.Select(d => d.NativeId).Order(StringComparer.Ordinal).ShouldBe(["door-a", "door-b"]);
    }

    [Fact]
    public async Task REG_U_025_Tags_all_and_any_filters()
    {
        var famId = TagFamilyId.New();
        var a = TagId.New();
        var b = TagId.New();
        var fam = new TagFamily(famId, "zone", new LocalizedName("Zone", new Dictionary<string, string>()), null, TagCardinality.ZeroOrMore, 0);
        (await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(new Tag(a, famId, "a", new LocalizedName("A", new Dictionary<string, string>()), null, 0)),
            new RegistryChange.UpsertTag(new Tag(b, famId, "b", new LocalizedName("B", new Dictionary<string, string>()), null, 0)),
        ]), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        await SeedQueryDevicesAsync(("both", "ac.door", [a, b], null), ("only-a", "ac.door", [a], null), ("none", "ac.door", [], null));

        _reader.QueryDevices(new DeviceQuery(TagsAll: [a, b]), new PageRequest(10)).Items.Select(d => d.NativeId).ShouldBe(["both"]);
        _reader.QueryDevices(new DeviceQuery(TagsAny: [a, b]), new PageRequest(10)).Items.Select(d => d.NativeId).ShouldBe(["both", "only-a"]);
    }

    [Fact]
    public async Task REG_U_027_Within_restricts_results()
    {
        var (_, devices) = await SeedQueryDevicesAsync(("d1", "ac.door", [], null), ("d2", "ac.door", [], null), ("d3", "ac.door", [], null));

        var visible = _reader.QueryDevices(new DeviceQuery(Within: new HashSet<DeviceId> { devices[0].Id, devices[2].Id }), new PageRequest(10)).Items;

        visible.Select(d => d.NativeId).ShouldBe(["d1", "d3"]);
    }

    [Fact]
    public async Task REG_U_069_Query_by_server_category_parent_roots_and_sort()
    {
        var (server, devices) = await SeedQueryDevicesAsync(("p", "ac.controller", [], null), ("x", "video.camera", [], null));
        var child = devices[0] with { Id = DeviceId.New(), ParentId = devices[0].Id, NativeId = "c", VendorName = "C", Version = 0 };
        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertDevice(child)]), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        _reader.QueryDevices(new DeviceQuery(Servers: [server]), new PageRequest(10)).Items.Count.ShouldBe(3);
        _reader.QueryDevices(new DeviceQuery(Servers: [ServerId.New()]), new PageRequest(10)).Items.ShouldBeEmpty();
        _reader.QueryDevices(new DeviceQuery(Parent: devices[0].Id), new PageRequest(10)).Items.Select(d => d.NativeId).ShouldBe(["c"]);
        _reader.QueryDevices(new DeviceQuery(RootsOnly: true), new PageRequest(10)).Items.Select(d => d.NativeId).ShouldBe(["p", "x"]);
        _reader.QueryDevices(new DeviceQuery(Categories: [Category.AccessControl]), new PageRequest(10)).Items.Count.ShouldBe(3);
        _reader.QueryDevices(new DeviceQuery(Categories: [Category.Video]), new PageRequest(10)).Items.ShouldBeEmpty();

        var byNameDesc = SortSpec.Parse("-name", DeviceQuery.SortFields, DeviceQuery.DefaultSort).Value;
        _reader.QueryDevices(new DeviceQuery(Sort: byNameDesc), new PageRequest(10)).Items.Select(d => d.NativeId).ShouldBe(["x", "p", "c"]);
        var byType = SortSpec.Parse("type,name", DeviceQuery.SortFields, DeviceQuery.DefaultSort).Value;
        _reader.QueryDevices(new DeviceQuery(Sort: byType), new PageRequest(10)).Items.Select(d => d.NativeId).ShouldBe(["c", "p", "x"]);
        SortSpec.Parse("color", DeviceQuery.SortFields, DeviceQuery.DefaultSort).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task REG_U_070_Patch_tags_add_remove_and_attribute_merge()
    {
        var famId = TagFamilyId.New();
        var a = TagId.New();
        var b = TagId.New();
        var fam = new TagFamily(famId, "zone", new LocalizedName("Zone", new Dictionary<string, string>()), null, TagCardinality.ZeroOrMore, 0);
        (await _writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertTagFamily(fam),
            new RegistryChange.UpsertTag(new Tag(a, famId, "a", new LocalizedName("A", new Dictionary<string, string>()), null, 0)),
            new RegistryChange.UpsertTag(new Tag(b, famId, "b", new LocalizedName("B", new Dictionary<string, string>()), null, 0)),
        ]), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var (_, devices) = await SeedQueryDevicesAsync(("d", "ac.door", [a], null));
        var id = devices[0].Id;
        var withAttrs = _reader.GetDevice(id).Value with { Attributes = new Dictionary<string, WpValue> { ["keep"] = WpValue.Int(1), ["drop"] = WpValue.Int(2) } };
        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertDevice(withAttrs)]), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        var patch = new DevicePatch(
            Name: new LocalizedName("Main door", new Dictionary<string, string>()),
            TagsAdd: [b, a],
            TagsRemove: [a],
            AttributesMerge: new Dictionary<string, WpValue> { ["drop"] = WpValue.Null, ["added"] = WpValue.Bool(true) });
        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.PatchDevice(id, patch, ExpectedVersion: 2)]), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        var patched = _reader.GetDevice(id).Value;
        patched.DisplayName!.Default.ShouldBe("Main door");
        patched.Tags.ShouldBe([b]);
        patched.Attributes.Keys.Order(StringComparer.Ordinal).ShouldBe(["added", "keep"]);
        patched.Version.ShouldBe(3);

        var clear = new DevicePatch(Name: new Optional<LocalizedName?>(null), TagsSet: new Optional<IReadOnlyList<TagId>>([a]));
        (await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.PatchDevice(id, clear)]), _context, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        _reader.GetDevice(id).Value.DisplayName.ShouldBeNull();
        _reader.GetDevice(id).Value.Tags.ShouldBe([a]);
    }
}
