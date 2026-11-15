using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Net;
using Npgsql;
using Shouldly;
using Watchpoint.IntegrationTests.Pipeline;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Registry;
using Watchpoint.Registry.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.IntegrationTests.Registry;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class RegistryIntegrationTests(PostgresContainerFixture postgres, NatsContainerFixture nats) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly ChangeContext _context = new("test-user", "integration-test");

    public async ValueTask DisposeAsync()
    {
        foreach (var db in _owned)
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task REG_I_001_Persist_and_reload()
    {
        var db = await DatabaseAsync(nameof(REG_I_001_Persist_and_reload));
        await using var node1 = await NodeAsync(db);

        var siteId = SiteId.New();
        var rootId = LocationId.New();
        var buildingId = LocationId.New();
        var srvId = ServerId.New();
        var devId = DeviceId.New();

        var site = new Site(siteId, new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0);
        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var building = new Location(buildingId, rootId, new LocalizedName("Building 1", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 1, 0);

        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv-1", null, "VendorSrv", buildingId, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "door-1", null, "VendorDoor", buildingId, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var batch = new RegistryChangeBatch([
            new RegistryChange.UpdateSite(site),
            new RegistryChange.UpsertLocation(root),
            new RegistryChange.UpsertLocation(building),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]);

        var res = await node1.Writer.ApplyAsync(batch, _context, Ct);
        res.IsSuccess.ShouldBeTrue();

        // Node 2 starts from existing DB and rebuilds in-memory snapshot
        await using var node2 = await NodeAsync(db);
        var reloadedDev = node2.Reader.GetDevice(devId);
        reloadedDev.IsSuccess.ShouldBeTrue();
        reloadedDev.Value.NativeId.ShouldBe("door-1");
        reloadedDev.Value.VendorName.ShouldBe("VendorDoor");

        var reloadedLoc = node2.Reader.GetLocation(buildingId);
        reloadedLoc.IsSuccess.ShouldBeTrue();
        reloadedLoc.Value.Path.ShouldBe("/Root/Building 1");
        reloadedLoc.Value.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task REG_I_002_Transaction_rollback()
    {
        var db = await DatabaseAsync(nameof(REG_I_002_Transaction_rollback));
        await using var node = await NodeAsync(db);

        var initialLocCount = node.Reader.QueryLocations(new LocationQuery(), new PageRequest(100)).TotalCount;

        // Batch with valid site update followed by invalid location referencing non-existent parent
        var badBatch = new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(new Location(LocationId.New(), LocationId.New(), new LocalizedName("Ghost Child", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 0, 0))
        ]);

        var res = await node.Writer.ApplyAsync(badBatch, _context, Ct);
        res.IsSuccess.ShouldBeFalse();

        // Ensure database table row count unchanged
        var count = await ScalarAsync<long>(db.ConnectionString, "SELECT COUNT(*) FROM wp_registry.locations;");
        count.ShouldBe(initialLocCount);
    }

    [Fact]
    public async Task REG_I_003_Unique_constraints_back_up_validation()
    {
        var db = await DatabaseAsync(nameof(REG_I_003_Unique_constraints_back_up_validation));
        await using var node = await NodeAsync(db);

        var srvId = ServerId.New();
        var connId = ConnectionId.New();
        var rootId = LocationId.New();

        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), connId, null, "srv-1", null, "VendorSrv", rootId, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev1 = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "native-dup", null, "Door 1", rootId, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        var batch1 = new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(root),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev1)
        ]);

        var res1 = await node.Writer.ApplyAsync(batch1, _context, Ct);
        res1.IsSuccess.ShouldBeTrue();

        // Direct SQL insert simulating concurrent bypass of validation
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO wp_registry.devices (id, type_id, type_version, server_id, parent_id, native_id, vendor_name, location_id, tags, attributes, custom_fields, unsupported_caps, enabled, maintenance, orphaned, row_version, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, $11::jsonb, $12, $13, $14, $15, $16, $17, $18)", conn);
        cmd.Parameters.AddWithValue(Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("ac.door");
        cmd.Parameters.AddWithValue("1.0.0");
        cmd.Parameters.AddWithValue(srvId.Value);
        cmd.Parameters.AddWithValue(DBNull.Value);
        cmd.Parameters.AddWithValue("native-dup");
        cmd.Parameters.AddWithValue("Door 2");
        cmd.Parameters.AddWithValue(rootId.Value);
        cmd.Parameters.AddWithValue(Array.Empty<Guid>());
        cmd.Parameters.AddWithValue("{}");
        cmd.Parameters.AddWithValue("{}");
        cmd.Parameters.AddWithValue(Array.Empty<string>());
        cmd.Parameters.AddWithValue(true);
        cmd.Parameters.AddWithValue(false);
        cmd.Parameters.AddWithValue(false);
        cmd.Parameters.AddWithValue(1L);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

        var ex = await Should.ThrowAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync(Ct));
        ex.SqlState.ShouldBe("23505"); // Unique violation in PostgreSQL
    }

    [Fact]
    public async Task REG_I_004_Location_move_rewrites_descendant_paths()
    {
        var db = await DatabaseAsync(nameof(REG_I_004_Location_move_rewrites_descendant_paths));
        await using var node = await NodeAsync(db);

        var rootId = LocationId.New();
        var b1Id = LocationId.New();
        var b2Id = LocationId.New();
        var floorId = LocationId.New();
        var roomId = LocationId.New();

        var root = new Location(rootId, null, new LocalizedName("Campus", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var b1 = new Location(b1Id, rootId, new LocalizedName("Building A", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 1, 0);
        var b2 = new Location(b2Id, rootId, new LocalizedName("Building B", new Dictionary<string, string>()), LocationKind.Building, "", 0, null, null, null, 2, 0);
        var floor = new Location(floorId, b1Id, new LocalizedName("Floor 1", new Dictionary<string, string>()), LocationKind.Floor, "", 0, null, null, null, 1, 0);
        var room = new Location(roomId, floorId, new LocalizedName("Room 101", new Dictionary<string, string>()), LocationKind.Room, "", 0, null, null, null, 1, 0);

        await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(root),
            new RegistryChange.UpsertLocation(b1),
            new RegistryChange.UpsertLocation(b2),
            new RegistryChange.UpsertLocation(floor),
            new RegistryChange.UpsertLocation(room)
        ]), _context, Ct);

        node.Reader.GetLocation(roomId).Value.Path.ShouldBe("/Campus/Building A/Floor 1/Room 101");

        // Move Floor 1 from Building A to Building B
        var movedFloor = node.Reader.GetLocation(floorId).Value with { ParentId = b2Id };
        var moveRes = await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(movedFloor, ExpectedVersion: movedFloor.Version)
        ]), _context, Ct);
        moveRes.IsSuccess.ShouldBeTrue();

        // In-memory reader check
        node.Reader.GetLocation(floorId).Value.Path.ShouldBe("/Campus/Building B/Floor 1");
        node.Reader.GetLocation(roomId).Value.Path.ShouldBe("/Campus/Building B/Floor 1/Room 101");

        // Database row check
        var dbRoomPath = await ScalarAsync<string>(db.ConnectionString, $"SELECT path FROM wp_registry.locations WHERE id = '{roomId.Value}';");
        dbRoomPath.ShouldBe("/Campus/Building B/Floor 1/Room 101");
    }

    [Fact]
    public async Task REG_I_005_Registry_changed_record_published_to_the_log()
    {
        await nats.StartAsync(Ct);
        var db = await DatabaseAsync(nameof(REG_I_005_Registry_changed_record_published_to_the_log));
        var scope = nats.CreateScope(nameof(REG_I_005_Registry_changed_record_published_to_the_log));
        var log = PipelineNode.OptionsFor(scope);
        await using var node = await NodeAsync(db, services =>
        {
            services.AddSingleton(new EventLogEndpoint(nats.Url, null));
            services.AddPipeline(log);
        });
        await node.Services.GetRequiredService<IEventLogSetup>().EnsureStreamsAsync(Ct);

        // The pipeline's outbox publisher (a hosted service) sends the records the registry wrote in its transactions.
        var publisher = node.Services.GetRequiredService<RegistryOutboxPublisher>();
        await publisher.StartAsync(Ct);

        // A consumer on the configuration stream, filtered on the registry kind.
        var registry = node.Services.GetRequiredService<ILogRecordTypeRegistry>();
        await using var consumer = node.Services.GetRequiredService<ILogConsumerFactory>()
            .CreateDurable("reg-i-005", LogStream.Cfg, [RecordKinds.RegistryChanged], new LogConsumerOptions());
        await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);

        var siteId = SiteId.New();
        var rootId = LocationId.New();
        var site = new Site(siteId, new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0);
        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var context = new ChangeContext("test-user", "integration-test", "corr-1");
        (await node.Writer.ApplyAsync(
            new RegistryChangeBatch([new RegistryChange.UpdateSite(site), new RegistryChange.UpsertLocation(root)]), context, Ct)).IsSuccess.ShouldBeTrue();
        (await node.Writer.ApplyAsync(
            new RegistryChangeBatch([new RegistryChange.UpsertLocation(root with { Notes = "changed" }, ExpectedVersion: 1)]), context, Ct)).IsSuccess.ShouldBeTrue();
        (await node.Writer.ApplyAsync(
            new RegistryChangeBatch([new RegistryChange.UpsertLocation(root with { Notes = "stale" }, ExpectedVersion: 7)]), context, Ct)).IsSuccess.ShouldBeFalse();

        // After apply, the consumer receives one record per applied batch, in order, with revision and changes.
        var records = new List<LogRecord>();
        while (records.Count < 2)
        {
            (await batches.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.Container, Ct)).ShouldBeTrue();
            records.AddRange(batches.Current.Records);
            await batches.Current.AckAsync(Ct);
        }

        records.Count.ShouldBe(2); // the rejected batch published nothing
        records.Select(r => r.Sequence).ShouldBe([1L, 2L]);
        records.ShouldAllBe(r => r.Envelope.Kind == RecordKinds.RegistryChanged && r.Envelope.P == 0 && r.Envelope.Dev == null);
        var first = records[0].BodyAs<RegistryChangedBody>(registry);
        first.Revision.ShouldBe(1);
        first.Context.ShouldBe(new RegistryChangedContext("test-user", "integration-test", "corr-1"));
        first.Changes.ShouldBe([new RegistryChangedEntity("site", siteId.Value, "updated"), new RegistryChangedEntity("location", rootId.Value, "created")]);
        var second = records[1].BodyAs<RegistryChangedBody>(registry);
        second.Revision.ShouldBe(2);
        second.Changes.ShouldBe([new RegistryChangedEntity("location", rootId.Value, "updated")]);

        // The record is on the configuration subject of the log, not in a partition.
        await using var client = new NatsClient(nats.Url);
        var stream = await client.CreateJetStreamContext().GetStreamAsync(scope.StreamPrefix + "WP_CFG", new() { SubjectsFilter = ">" }, Ct);
        stream.Info.State.Subjects.ShouldNotBeNull().Keys.ShouldBe([scope.SubjectPrefix + ".cfg.registry.changed"]);
        (await node.Services.GetRequiredService<IEventLogReader>().GetLastSequenceAsync(Ct)).ShouldBe(0);
        await publisher.StopAsync(Ct);
    }

    [Fact]
    public async Task REG_I_006_Optimistic_concurrency_conflict()
    {
        var db = await DatabaseAsync(nameof(REG_I_006_Optimistic_concurrency_conflict));
        await using var node = await NodeAsync(db);

        var rootId = LocationId.New();
        var srvId = ServerId.New();
        var devId = DeviceId.New();

        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv-1", null, "VendorSrv", rootId, [], new Dictionary<string, WpValue>(), true, false, 0);
        var dev = new Device(devId, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), srvId, null, "door-1", null, "Door", rootId, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);

        await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertLocation(root),
            new RegistryChange.UpsertServer(srv),
            new RegistryChange.UpsertDevice(dev)
        ]), _context, Ct);

        var currentDev = node.Reader.GetDevice(devId).Value;
        currentDev.Version.ShouldBe(1);

        // Writer A updates device with expected version 1
        var resA = await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertDevice(currentDev with { VendorName = "A" }, ExpectedVersion: currentDev.Version),
        ]), _context, Ct);
        resA.IsSuccess.ShouldBeTrue();

        // Writer B attempts update with same initial expected version 1 -> conflict!
        var resB = await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpsertDevice(currentDev with { VendorName = "B" }, ExpectedVersion: currentDev.Version),
        ]), _context, Ct);

        resB.IsSuccess.ShouldBeFalse();
        resB.Error.Code.Value.ShouldBe("common.version.conflict");
    }

    [Fact]
    public async Task REG_I_007_Discovered_items_persisted()
    {
        var db = await DatabaseAsync(nameof(REG_I_007_Discovered_items_persisted));
        await using var node = await NodeAsync(db);

        var siteId = SiteId.New();
        var rootId = LocationId.New();
        var connId = ConnectionId.New();

        var site = new Site(siteId, new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0);
        var root = new Location(rootId, null, new LocalizedName("Campus", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);

        await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpdateSite(site),
            new RegistryChange.UpsertLocation(root)
        ]), _context, Ct);

        var serverType = new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value);
        var devType = new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);

        var report = new InventoryReport(
            Servers: [new InventoryServer("srv-1", null, serverType, "VendorServer", new Dictionary<string, WpValue>())],
            Devices: [new InventoryDevice("door-101", "srv-1", null, devType, "Front Door", new Dictionary<string, WpValue>(), new HashSet<CapabilityId>(), [])],
            IsComplete: true);

        // Reconcile with AutoImportNew = false -> writes to discovered_items table
        var recResult = await node.Reconciler.ReconcileAsync(connId, report, new ReconcileOptions(AutoImportNew: false, DefaultLocation: rootId), Ct);
        recResult.IsSuccess.ShouldBeTrue();
        recResult.Value.NewDiscovered.ShouldBe(1);

        // Verify row exists directly in Postgres DB
        var rowCount = await ScalarAsync<long>(db.ConnectionString, $"SELECT COUNT(*) FROM wp_registry.discovered_items WHERE connection_id = '{connId.Value}' AND native_id = 'door-101';");
        rowCount.ShouldBe(1L);

        var dbStatus = await ScalarAsync<string>(db.ConnectionString, $"SELECT status FROM wp_registry.discovered_items WHERE connection_id = '{connId.Value}' AND native_id = 'door-101';");
        dbStatus.ShouldBe("new");

        // Verify query through reconciler
        var discoveredItems = await node.Reconciler.GetDiscoveredAsync(connId, DiscoveredStatus.New, Ct);
        discoveredItems.Count.ShouldBe(1);
        discoveredItems[0].NativeId.ShouldBe("door-101");
        discoveredItems[0].Status.ShouldBe(DiscoveredStatus.New);

        // Import the discovered item
        var importResult = await node.Reconciler.ImportAsync(connId, new ImportRequest(NativeIds: ["door-101"]), _context, Ct);
        importResult.IsSuccess.ShouldBeTrue();

        // Verify status updated in Postgres DB
        var updatedDbStatus = await ScalarAsync<string>(db.ConnectionString, $"SELECT status FROM wp_registry.discovered_items WHERE connection_id = '{connId.Value}' AND native_id = 'door-101';");
        updatedDbStatus.ShouldBe("imported");

        // Verify device now exists in registry reader
        var servers = node.Reader.QueryServers(new ServerQuery(ConnectionId: connId), new PageRequest()).Items;
        servers.Count.ShouldBe(1);
        var devResult = node.Reader.GetDeviceByNativeId(servers[0].Id, "door-101");
        devResult.IsSuccess.ShouldBeTrue();
        devResult.Value.VendorName.ShouldBe("Front Door");
    }

    [Fact]
    public async Task REG_I_009_Load_time_50000_devices()
    {
        var db = await DatabaseAsync(nameof(REG_I_009_Load_time_50000_devices));
        await using var node = await NodeAsync(db);

        var siteId = SiteId.New();
        var rootId = LocationId.New();
        var srvId = ServerId.New();

        var site = new Site(siteId, new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0);
        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var srv = new Server(srvId, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv-1", null, "VendorSrv", rootId, [], new Dictionary<string, WpValue>(), true, false, 0);

        await node.Writer.ApplyAsync(new RegistryChangeBatch([
            new RegistryChange.UpdateSite(site),
            new RegistryChange.UpsertLocation(root),
            new RegistryChange.UpsertServer(srv),
        ]), _context, Ct);

        // Bulk seed 50,000 devices via PostgreSQL binary COPY
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var writer = await conn.BeginBinaryImportAsync(
                "COPY wp_registry.devices (id, type_id, type_version, server_id, parent_id, native_id, vendor_name, location_id, tags, attributes, custom_fields, unsupported_caps, enabled, maintenance, orphaned, row_version, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                Ct);

            var now = DateTimeOffset.UtcNow;
            for (var i = 1; i <= 50000; i++)
            {
                await writer.StartRowAsync(Ct);
                await writer.WriteAsync(Guid.CreateVersion7(), NpgsqlTypes.NpgsqlDbType.Uuid, Ct);
                await writer.WriteAsync("ac.door", NpgsqlTypes.NpgsqlDbType.Text, Ct);
                await writer.WriteAsync("1.0.0", NpgsqlTypes.NpgsqlDbType.Text, Ct);
                await writer.WriteAsync(srvId.Value, NpgsqlTypes.NpgsqlDbType.Uuid, Ct);
                await writer.WriteNullAsync(Ct);
                await writer.WriteAsync($"door-{i}", NpgsqlTypes.NpgsqlDbType.Text, Ct);
                await writer.WriteAsync($"Door {i}", NpgsqlTypes.NpgsqlDbType.Text, Ct);
                await writer.WriteAsync(rootId.Value, NpgsqlTypes.NpgsqlDbType.Uuid, Ct);
                await writer.WriteAsync(Array.Empty<Guid>(), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Ct);
                await writer.WriteAsync("{}", NpgsqlTypes.NpgsqlDbType.Jsonb, Ct);
                await writer.WriteAsync("{}", NpgsqlTypes.NpgsqlDbType.Jsonb, Ct);
                await writer.WriteAsync(Array.Empty<string>(), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, Ct);
                await writer.WriteAsync(true, NpgsqlTypes.NpgsqlDbType.Boolean, Ct);
                await writer.WriteAsync(false, NpgsqlTypes.NpgsqlDbType.Boolean, Ct);
                await writer.WriteAsync(false, NpgsqlTypes.NpgsqlDbType.Boolean, Ct);
                await writer.WriteAsync(1L, NpgsqlTypes.NpgsqlDbType.Bigint, Ct);
                await writer.WriteAsync(now, NpgsqlTypes.NpgsqlDbType.TimestampTz, Ct);
                await writer.WriteAsync(now, NpgsqlTypes.NpgsqlDbType.TimestampTz, Ct);
            }

            await writer.CompleteAsync(Ct);
        }

        // Measure reload time
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var reloadNode = await NodeAsync(db);
        sw.Stop();

        reloadNode.Reader.QueryDevices(new DeviceQuery(), new PageRequest(10)).Items.Count.ShouldBe(10);
        var limit = SnapshotLoadLimit(Environment.GetEnvironmentVariable);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"REG-I-009 50,000-device snapshot load = {sw.Elapsed.TotalMilliseconds:0} ms (limit {limit.TotalMilliseconds:0} ms)");
        sw.Elapsed.ShouldBeLessThan(limit);
    }

    /// <summary>
    /// REG-I-009 limit (REG §14): the spec's 3 s on a developer machine or in benchmark runs; a 10 s ceiling on shared
    /// CI runners (<see cref="TimingLimits"/>), where the measurement is only a warning. The ceiling still catches a load
    /// that is an order of magnitude too slow (e.g. one query per device).
    /// </summary>
    internal static TimeSpan SnapshotLoadLimit(Func<string, string?> environment) =>
        TimingLimits.For(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), environment);

    private async Task<PostgresDatabase> DatabaseAsync(string name)
    {
        var db = await postgres.CreateDatabaseAsync(nameof(RegistryIntegrationTests) + "." + name, Ct);
        _owned.Add(db);
        return db;
    }

    private static async Task<Node> NodeAsync(PostgresDatabase db, Action<IServiceCollection>? configure = null)
    {
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var node = new Node();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(node.Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(node.Clock);
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddWatchpointModel();
        services.AddWatchpointRegistry(SiteId.New());
        configure?.Invoke(services);
        node.Services = services.BuildServiceProvider();
        await node.Services.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        return node;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Node : IAsyncDisposable
    {
        public LogCollector Logs { get; } = new();

        public TestClock Clock { get; } = new();

        public ServiceProvider Services { get; set; } = null!;

        public IRegistryReader Reader => Services.GetRequiredService<IRegistryReader>();

        public IRegistryWriter Writer => Services.GetRequiredService<IRegistryWriter>();

        public IInventoryReconciler Reconciler => Services.GetRequiredService<IInventoryReconciler>();

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Logs.Dispose();
        }
    }
}
