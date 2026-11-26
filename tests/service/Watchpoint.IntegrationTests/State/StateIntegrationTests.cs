using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Watchpoint.Registry;
using Watchpoint.Registry.Contracts;
using Watchpoint.State;
using Watchpoint.State.Contracts;
using Watchpoint.State.Internal;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.State;

/// <summary>
/// Snapshots and recovery of the state module (spec STA §8, §13) on PostgreSQL and NATS, composed like in the host:
/// Storage, Model, Registry, Pipeline and State. A "life" of the node is one service provider; the database and the
/// NATS scope outlive it.
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "STA")]
public sealed class StateIntegrationTests(PostgresContainerFixture postgres, NatsContainerFixture nats) : IAsyncDisposable
{
    private static readonly ChangeContext _context = new(UserId.New(), "api", null, "integration-test");
    private readonly List<IAsyncDisposable> _owned = [];
    private long _logBytes = 64L * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task STA_I_001_Snapshot_persisted_to_postgresql()
    {
        var (db, scope) = await PrepareAsync(nameof(STA_I_001_Snapshot_persisted_to_postgresql));
        await using var node = await StartLifeAsync(db, scope, seed: (n, c) => SeedAsync(n, c, servers: 1, doorsPerServer: 20), snapshotInterval: "PT1S");
        await IngestAsync(node, "open");

        // The devices changed; the server, restored from the registry and unchanged since, needs no item yet.
        var entities = 20;
        var partitions = node.Devices.Select(d => Partitioning.Of(d.Id)).Distinct().Count();
        var deadline = Stopwatch.GetTimestamp() + (long)(TestTimeouts.Container.TotalSeconds * Stopwatch.Frequency);
        while (await CountAsync(db, "SELECT count(*) FROM wp_state.items WHERE kind = 'device'") < entities
            || await CountAsync(db, "SELECT count(*) FROM wp_state.checkpoints WHERE consumer = 'state' AND sequence > 0") < partitions)
        {
            Stopwatch.GetTimestamp().ShouldBeLessThan(deadline, "the snapshot was not written");
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }

        (await CountAsync(db, "SELECT count(*) FROM wp_state.items WHERE kind = 'device' AND payload ? 'properties' AND NOT payload ? 'summary'")).ShouldBe(entities);
    }

    [Fact]
    public async Task STA_I_002_Restart_restores_state()
    {
        var (db, scope) = await PrepareAsync(nameof(STA_I_002_Restart_restores_state));
        List<string> before;
        Node first;
        await using (first = await StartLifeAsync(db, scope, seed: (n, c) => SeedAsync(n, c, servers: 1, doorsPerServer: 30)))
        {
            await IngestAsync(first, "closed");
            await IngestAsync(first, "open", take: 10);
            await WaitAppliedAsync(first);
            before = Normalised(first);
            await first.StopGracefullyAsync();
        }

        await using var second = await StartLifeAsync(db, scope, devices: first.Devices, servers: first.Servers);

        Normalised(second).ShouldBe(before);
        second.Lifecycle.Recovered.ShouldBeTrue();
        second.Logs.WithEventId(1701).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task STA_I_003_Crash_restores_state()
    {
        var (db, scope) = await PrepareAsync(nameof(STA_I_003_Crash_restores_state));
        List<string> before;
        Node first;
        await using (first = await StartLifeAsync(db, scope, seed: (n, c) => SeedAsync(n, c, servers: 1, doorsPerServer: 30)))
        {
            await IngestAsync(first, "closed");
            await WaitAppliedAsync(first);
            (await first.Services.GetRequiredService<Snapshotter>().SnapshotAsync(onWorkers: true, Ct)).ShouldBeTrue();

            // Acknowledged after the snapshot: only the log holds them. The node then dies without a shutdown snapshot.
            await IngestAsync(first, "forced", take: 15);
            await WaitAppliedAsync(first);
            before = Normalised(first);
        }

        await using var second = await StartLifeAsync(db, scope, devices: first.Devices, servers: first.Servers);

        Normalised(second).ShouldBe(before);
        foreach (var (device, index) in first.Devices.Select((d, i) => (d, i)))
        {
            second.Reader.Get(device.Id.Value).Value.Properties[IngestNode.Position].Value.ShouldBe(WpValue.Enum(index < 15 ? "forced" : "closed"));
        }
    }

    [Fact]
    public async Task STA_I_005_Recovery_time_target()
    {
        // Scaled down ×10 for CI (spec STA §13): 2,500 devices, 100,000 records after the snapshot, target 6 s.
        const int Servers = 25;
        const int DoorsPerServer = 100;
        const int Rounds = 40;
        var (db, scope) = await PrepareAsync(nameof(STA_I_005_Recovery_time_target), logBytes: 512L * 1024 * 1024);
        Node first;
        await using (first = await StartLifeAsync(db, scope, seed: (n, c) => SeedAsync(n, c, Servers, DoorsPerServer)))
        {
            await IngestAsync(first, "closed");
            await WaitAppliedAsync(first);
            (await first.Services.GetRequiredService<Snapshotter>().SnapshotAsync(onWorkers: true, Ct)).ShouldBeTrue();
            string[] codes = ["open", "closed"];
            for (var round = 0; round < Rounds; round++)
            {
                await IngestAsync(first, codes[round % 2]);
            }

            await WaitAppliedAsync(first);
        }

        var started = Stopwatch.GetTimestamp();
        await using var second = await StartLifeAsync(db, scope, devices: first.Devices, servers: first.Servers);
        var elapsed = Stopwatch.GetElapsedTime(started);

        second.Reader.Get(first.Devices[0].Id.Value).Value.Properties[IngestNode.Position].Value.ShouldBe(WpValue.Enum("closed"));
        second.Logs.WithEventId(1701).ShouldHaveSingleItem();
        elapsed.ShouldBeLessThan(TimingLimits.For(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(60)));
    }

    private async Task<(PostgresDatabase Db, NatsScope Scope)> PrepareAsync(string test, long logBytes = 64L * 1024 * 1024)
    {
        await postgres.StartAsync(Ct);
        await nats.StartAsync(Ct);
        var db = await postgres.CreateDatabaseAsync(test, Ct);
        _owned.Add(db);
        _logBytes = logBytes;
        return (db, nats.CreateScope(test));
    }

    /// <summary>One life of the node: build, migrate, optional seed, then state restore → pipeline recovery → workers (→ periodic snapshots).</summary>
    private async Task<Node> StartLifeAsync(
        PostgresDatabase db,
        NatsScope scope,
        Func<ServiceProvider, ConnectionId[], Task<(List<Server> Servers, List<Device> Devices)>>? seed = null,
        string? snapshotInterval = null,
        List<Device>? devices = null,
        List<Server>? servers = null)
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
        if (snapshotInterval is not null)
        {
            settings["State:SnapshotInterval"] = snapshotInterval;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logs = new Watchpoint.Testing.Logging.LogCollector();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddWatchpointModel();
        services.AddWatchpointRegistry(SiteId.New());
        services.AddSingleton(new EventLogEndpoint(nats.Url, null));
        services.AddPipeline(PipelineNode.OptionsFor(scope, _logBytes));
        services.AddWatchpointState(configuration);
        var provider = services.BuildServiceProvider();
        var node = new Node(provider, logs);
        await provider.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        await provider.GetRequiredService<IEventLogSetup>().EnsureStreamsAsync(Ct);
        if (seed is not null)
        {
            var connections = Enumerable.Range(0, 25).Select(_ => ConnectionId.New()).ToArray();
            (node.Servers, node.Devices) = await seed(provider, connections);
        }
        else
        {
            node.Servers = servers!;
            node.Devices = devices!;
        }

        await node.Lifecycle.RestoreAsync(Ct);
        await node.PipelineLifecycle.RecoverAsync(Ct);
        await node.PipelineLifecycle.StartAsync(Ct);
        if (snapshotInterval is not null)
        {
            node.Lifecycle.StartSnapshots();
        }

        return node;
    }

    /// <summary>Site, root location, <paramref name="servers"/> servers (one connection each) with <paramref name="doorsPerServer"/> doors.</summary>
    private static async Task<(List<Server>, List<Device>)> SeedAsync(ServiceProvider node, ConnectionId[] connections, int servers, int doorsPerServer)
    {
        var writer = node.GetRequiredService<IRegistryWriter>();
        var rootId = LocationId.New();
        var version = SemVersion.Parse("1.0.0").Value;
        var site = new Site(SiteId.New(), new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0);
        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        await ApplyAsync(writer, [new RegistryChange.UpdateSite(site), new RegistryChange.UpsertLocation(root)]);
        var serverList = new List<Server>();
        var deviceList = new List<Device>();
        for (var s = 0; s < servers; s++)
        {
            var server = new Server(
                ServerId.New(), new TypeRef(TypeId.FromTrusted("generic.server"), version), connections[s], null, $"srv-{s}", null, $"Server {s}", rootId, [],
                new Dictionary<string, WpValue>(), true, false, 0);
            await ApplyAsync(writer, [new RegistryChange.UpsertServer(server)]);
            serverList.Add(server);
            var doors = Enumerable.Range(0, doorsPerServer).Select(i => new Device(
                DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), version), server.Id, null, $"door-{s}-{i}", null, $"Door {s}.{i}", rootId, [],
                new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false,
                DateTimeOffset.UtcNow, 0)).ToList();
            await ApplyAsync(writer, [.. doors.Select(d => new RegistryChange.UpsertDevice(d))]);
            deviceList.AddRange(doors);
        }

        return (serverList, deviceList);
    }

    private static async Task ApplyAsync(IRegistryWriter writer, RegistryChange[] changes)
    {
        var applied = await writer.ApplyAsync(new RegistryChangeBatch(changes), _context, Ct);
        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Code.Value + " " + applied.Error.DeveloperDetail);
    }

    /// <summary>Every device (or the first <paramref name="take"/>) reports a position; one batch per connection.</summary>
    private static async Task IngestAsync(Node node, string code, int? take = null)
    {
        var ingest = node.Services.GetRequiredService<IObservationIngest>();
        var connectionOf = node.Servers.ToDictionary(s => s.Id, s => s.ConnectionId);
        var targets = (take is { } n ? node.Devices.Take(n) : node.Devices).ToList();
        foreach (var group in targets.GroupBy(d => connectionOf[d.ServerId]))
        {
            var result = await ingest.IngestAsync([.. group.Select(d => IngestNode.Property(d.Id, group.Key, code))], Ct);
            result.Rejected.ShouldBeEmpty();
        }
    }

    /// <summary>Waits until the state applied every record the log holds (the workers apply after the append).</summary>
    private static async Task WaitAppliedAsync(Node node)
    {
        var last = await node.Services.GetRequiredService<IEventLogReader>().GetLastSequenceAsync(Ct);
        await Eventually.UntilAsync(() => node.Reader.CurrentSequence >= last, TestTimeouts.Container, "the state did not apply every record", ct: Ct);
    }

    private static async Task<long> CountAsync(PostgresDatabase db, string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The states of every seeded entity as §7.1 JSON, without the times derived at recovery.</summary>
    private static List<string> Normalised(Node node) =>
        [.. node.Servers.Select(s => s.Id.Value).Concat(node.Devices.Select(d => d.Id.Value))
            .Select(id => Canonical(System.Text.Json.Nodes.JsonNode.Parse(DeviceStateJson.ToJson(node.Reader.Get(id).Value with { UpdatedAt = default })))!.ToJsonString())];

    /// <summary>The JSON with the members of every object sorted, so that the order of a dictionary does not matter.</summary>
    private static System.Text.Json.Nodes.JsonNode? Canonical(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(
            o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
        System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(Canonical)]),
        _ => node?.DeepClone(),
    };

    private sealed class Node(ServiceProvider services, Watchpoint.Testing.Logging.LogCollector logs) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public Watchpoint.Testing.Logging.LogCollector Logs { get; } = logs;

        public List<Server> Servers { get; set; } = [];

        public List<Device> Devices { get; set; } = [];

        public IStateLifecycle Lifecycle => Services.GetRequiredService<IStateLifecycle>();

        public IPipelineLifecycle PipelineLifecycle => Services.GetRequiredService<IPipelineLifecycle>();

        public IStateReader Reader => Services.GetRequiredService<IStateReader>();

        /// <summary>The host's shutdown order: the workers drain (phase Runtime), then the final snapshot (phase Recovery).</summary>
        public async Task StopGracefullyAsync()
        {
            await PipelineLifecycle.StopAsync(Ct);
            await Lifecycle.StopAsync(Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Logs.Dispose();
        }
    }
}
