using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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

public partial class RegistryUnitTests
{
    private InventoryReconciler CreateReconciler(ITypeCatalog? customCatalog = null)
    {
        return new InventoryReconciler(
            _reader,
            _writer,
            customCatalog ?? _types,
            _factory,
            _clock,
            NullLogger<InventoryReconciler>.Instance);
    }

    [Fact]
    public async Task REG_U_031_Reconcile_new_server_auto_created()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var reconciler = CreateReconciler();

        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer1", new Dictionary<string, WpValue>())],
            Devices: [],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        res.Value.Imported.ShouldBe(1);

        var servers = _reader.QueryServers(new ServerQuery(ConnectionId: connId), new PageRequest()).Items;
        servers.Count.ShouldBe(1);
        servers[0].NativeId.ShouldBe("srv-1");
        servers[0].VendorName.ShouldBe("VendorServer1");
        servers[0].LocationId.ShouldBe(root.Id);
    }

    [Fact]
    public async Task REG_U_032_Reconcile_existing_device_vendor_name_updated_user_name_untouched()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var userCustomName = new LocalizedName("My Custom Door Name", new Dictionary<string, string>());
        var dev = new Device(devId, devType, srvId, null, "door-1", userCustomName, "OldVendorName", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-1", "srv-1", null, devType, "NewVendorName", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        res.Value.Updated.ShouldBe(1);

        var updated = _reader.GetDevice(devId).Value;
        updated.VendorName.ShouldBe("NewVendorName");
        updated.DisplayName.ShouldNotBeNull();
        updated.DisplayName!.Default.ShouldBe("My Custom Door Name");
    }

    [Fact]
    public async Task REG_U_033_Reconcile_vendor_attributes_updated_user_attributes_untouched()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var customCatalog = new TestTypeCatalogWithVendorSchema();

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, devType, srvId, null, "door-1", null, "Door 1", root.Id, [],
            new Dictionary<string, WpValue>
            {
                ["userConfig"] = WpValue.String("keep-me"),
                ["firmwareVersion"] = WpValue.String("1.0.0")
            },
            new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler(customCatalog);
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-1", "srv-1", null, devType, "Door 1",
                new Dictionary<string, WpValue>
                {
                    ["userConfig"] = WpValue.String("attempted-vendor-overwrite"),
                    ["firmwareVersion"] = WpValue.String("2.0.0")
                },
                new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        var updated = _reader.GetDevice(devId).Value;
        updated.Attributes["userConfig"].AsString().ShouldBe("keep-me");
        updated.Attributes["firmwareVersion"].AsString().ShouldBe("2.0.0");
    }

    [Fact]
    public async Task REG_U_034_Reconcile_missing_device_orphaned_on_complete_report()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, devType, srvId, null, "door-missing", null, "Door", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        // Report is complete, but devices list is empty -> dev should be orphaned
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        res.Value.Orphaned.ShouldBe(1);

        _reader.GetDevice(devId).Value.Orphaned.ShouldBeTrue();
    }

    [Fact]
    public async Task REG_U_035_Reconcile_partial_report_never_orphans()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, devType, srvId, null, "door-1", null, "Door", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        // IsComplete is FALSE
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [],
            IsComplete: false);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        res.Value.Orphaned.ShouldBe(0);

        _reader.GetDevice(devId).Value.Orphaned.ShouldBeFalse();
    }

    [Fact]
    public async Task REG_U_036_Reconcile_orphan_restored_when_back()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, devType, srvId, null, "door-1", null, "Door", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, Orphaned: true, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-1", "srv-1", null, devType, "Door", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        res.Value.Restored.ShouldBe(1);

        _reader.GetDevice(devId).Value.Orphaned.ShouldBeFalse();
    }

    [Fact]
    public async Task REG_U_037_Reconcile_new_devices_go_to_discovered_list_when_not_auto_importing()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-new", "srv-1", null, devType, "Vendor Door", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        res.IsSuccess.ShouldBeTrue();
        res.Value.NewDiscovered.ShouldBe(1);

        var discovered = await reconciler.GetDiscoveredAsync(connId, DiscoveredStatus.New, TestContext.Current.CancellationToken);
        discovered.Count.ShouldBe(1);
        discovered[0].NativeId.ShouldBe("door-new");
        discovered[0].Status.ShouldBe(DiscoveredStatus.New);
        discovered[0].FirstSeen.ShouldNotBe(default);
    }

    [Fact]
    public async Task REG_U_038_Reconcile_ignored_items_stay_ignored()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-ignore", "srv-1", null, devType, "Vendor Door", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        // Run 1: discovered
        await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        // Set ignored
        var ignoreRes = await reconciler.SetIgnoredAsync(connId, ["door-ignore"], ignored: true, TestContext.Current.CancellationToken);
        ignoreRes.IsSuccess.ShouldBeTrue();

        var ignoredItems = await reconciler.GetDiscoveredAsync(connId, DiscoveredStatus.Ignored, TestContext.Current.CancellationToken);
        ignoredItems.Count.ShouldBe(1);

        // Run 2: report comes again
        var res2 = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res2.IsSuccess.ShouldBeTrue();

        // Stays ignored
        var stillIgnored = await reconciler.GetDiscoveredAsync(connId, DiscoveredStatus.Ignored, TestContext.Current.CancellationToken);
        stillIgnored.Count.ShouldBe(1);
        stillIgnored[0].Status.ShouldBe(DiscoveredStatus.Ignored);
    }

    [Fact]
    public async Task REG_U_039_Reconcile_auto_import_places_devices_at_parents_location()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var parentLocationId = LocationId.New();
        var parentLocation = new Location(parentLocationId, root.Id, new LocalizedName("SubLocation", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 1, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(parentLocation)]), _context, TestContext.Current.CancellationToken);

        var srvId = ServerId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);
        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);

        // Parent device exists at parentLocationId
        var parentDevId = DeviceId.New();
        var parentDev = new Device(parentDevId, devType, srvId, null, "door-parent", null, "Parent Door", parentLocationId, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(parentDev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [
                new InventoryDevice("door-parent", "srv-1", null, devType, "Parent Door", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), []),
                new InventoryDevice("reader-child", "srv-1", "door-parent", devType, "Reader Child", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])
            ],
            IsComplete: true);

        // Auto-import is true, default location is root, but child should follow parent's location
        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: true, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        var childDev = _reader.GetDeviceByNativeId(srvId, "reader-child").Value;
        childDev.LocationId.ShouldBe(parentLocationId);
    }

    [Fact]
    public async Task REG_U_040_Reconcile_minor_type_upgrade_applied_major_change_warned()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var dev1Id = DeviceId.New();
        var dev2Id = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType100 = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);
        var devType110 = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.1.0").Value);
        var devType200 = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("2.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev1 = new Device(dev1Id, devType100, srvId, null, "door-minor", null, "Door Minor", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var dev2 = new Device(dev2Id, devType100, srvId, null, "door-major", null, "Door Major", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev1), new RegistryChange.UpsertDevice(dev2)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [
                new InventoryDevice("door-minor", "srv-1", null, devType110, "Door Minor", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), []),
                new InventoryDevice("door-major", "srv-1", null, devType200, "Door Major", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])
            ],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        // dev1 upgraded to 1.1.0
        _reader.GetDevice(dev1Id).Value.Type.Version.ShouldBe(SemVersion.Parse("1.1.0").Value);

        // dev2 kept at 1.0.0 and generated warning
        _reader.GetDevice(dev2Id).Value.Type.Version.ShouldBe(SemVersion.Parse("1.0.0").Value);
        res.Value.Warnings.ShouldContain(w => w.Code == RegistryErrors.ReconcileTypeMajorChange && w.NativeId == "door-major");
    }

    [Fact]
    public async Task REG_U_041_Reconcile_links_by_native_id_created_unresolved_warning()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [
                new InventoryDevice("door-1", "srv-1", null, devType, "Door 1", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(),
                    [
                        new InventoryLink("CameraViews", "door-2"),
                        new InventoryLink("CameraViews", "missing-target")
                    ]),
                new InventoryDevice("door-2", "srv-1", null, devType, "Door 2", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])
            ],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: true, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        var d1 = _reader.GetDeviceByNativeId(srvId, "door-1").Value;
        var d2 = _reader.GetDeviceByNativeId(srvId, "door-2").Value;

        var links = _reader.GetOutgoingLinks(d1.Id);
        links.Count.ShouldBe(1);
        links[0].TargetDeviceId.ShouldBe(d2.Id);

        res.Value.Warnings.ShouldContain(w => w.Code == RegistryErrors.ReconcileLinkUnresolved && w.NativeId == "door-1");
    }

    [Fact]
    public async Task REG_U_042_Reconcile_unsupported_optional_capabilities_recorded()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var srvId = ServerId.New();
        var devId = DeviceId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, devType, srvId, null, "door-1", null, "Door", root.Id, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv), new RegistryChange.UpsertDevice(dev)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var tamperCap = CapabilityId.FromTrusted("tamper");
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-1", "srv-1", null, devType, "Door", new Dictionary<string, WpValue>(), new HashSet<CapabilityId> { tamperCap }, [])],
            IsComplete: true);

        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        var updated = _reader.GetDevice(devId).Value;
        updated.UnsupportedCaps.ShouldContain(tamperCap);
    }

    [Fact]
    public async Task REG_U_043_Import_selected_discovered_items_with_overrides()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var customLocId = LocationId.New();
        var customLoc = new Location(customLocId, root.Id, new LocalizedName("CustomLoc", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 1, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertLocation(customLoc)]), _context, TestContext.Current.CancellationToken);

        var srvId = ServerId.New();
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var srv = new Server(srvId, serverType, connId, null, "srv-1", null, "VendorServer", root.Id, [], new Dictionary<string, WpValue>(), true, false, 0);
        await _writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertServer(srv)]), _context, TestContext.Current.CancellationToken);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [
                new InventoryDevice("parent-door", "srv-1", null, devType, "VendorParent", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), []),
                new InventoryDevice("child-reader", "srv-1", "parent-door", devType, "VendorChild", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])
            ],
            IsComplete: true);

        // Discovered staging
        await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: root.Id), TestContext.Current.CancellationToken);

        // Import with overrides
        var overrideName = new LocalizedName("User Override Parent", new Dictionary<string, string>());
        var importRequest = new ImportRequest(
            NativeIds: ["parent-door", "child-reader"],
            Overrides: new Dictionary<string, ImportOverrides>
            {
                ["parent-door"] = new ImportOverrides(DisplayName: overrideName, LocationId: customLocId)
            });

        var importRes = await reconciler.ImportAsync(connId, importRequest, _context, TestContext.Current.CancellationToken);
        importRes.IsSuccess.ShouldBeTrue();

        var importedParent = _reader.GetDeviceByNativeId(srvId, "parent-door").Value;
        importedParent.DisplayName.ShouldNotBeNull();
        importedParent.DisplayName!.Default.ShouldBe("User Override Parent");
        importedParent.LocationId.ShouldBe(customLocId);

        // Child follows parent's location
        var importedChild = _reader.GetDeviceByNativeId(srvId, "child-reader").Value;
        importedChild.LocationId.ShouldBe(customLocId);
    }

    [Fact]
    public async Task REG_U_044_Reconcile_is_idempotent()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-1", "srv-1", null, devType, "Door 1", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        // Run 1: imports server and device
        var res1 = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: true, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res1.IsSuccess.ShouldBeTrue();
        res1.Value.Imported.ShouldBe(2); // 1 server + 1 device

        var devBefore = _reader.QueryDevices(new DeviceQuery(), new PageRequest()).Items[0];
        long devVersionBefore = devBefore.Version;

        // Run 2: identical report
        var res2 = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: true, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res2.IsSuccess.ShouldBeTrue();
        res2.Value.Unchanged.ShouldBe(2); // server + device unchanged
        res2.Value.Updated.ShouldBe(0);

        var devAfter = _reader.QueryDevices(new DeviceQuery(), new PageRequest()).Items[0];
        devAfter.Version.ShouldBe(devVersionBefore);
    }

    [Fact]
    public async Task REG_U_045_Reconcile_publishes_one_batch_with_origin_integration_id()
    {
        var connId = ConnectionId.New();
        var root = _reader.GetRootLocation().Value;
        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var reconciler = CreateReconciler();
        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-1", "srv-1", null, devType, "Door 1", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        _publisher.Published.Clear();
        var res = await reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: true, DefaultLocation: root.Id), TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue();

        _publisher.Published.Count.ShouldBe(1);
        _publisher.Published[0].Context.Reason.ShouldBe($"integration:{connId.Value}");
    }

    private sealed class TestTypeCatalogWithVendorSchema : ITypeCatalog
    {
        public long Revision => 1;
        public event EventHandler<CatalogChangedEventArgs>? Changed { add { } remove { } }

        public Result<DeviceTypeDefinition> Get(TypeRef type)
        {
            var schemaJson = """
            {
                "type": "object",
                "properties": {
                    "firmwareVersion": {
                        "type": "string",
                        "x-wp-vendor": true
                    },
                    "userConfig": {
                        "type": "string"
                    }
                }
            }
            """;
            var paramSchema = ParameterSchema.Parse(JsonDocument.Parse(schemaJson).RootElement).Value;

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
                Attributes = paramSchema,
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
