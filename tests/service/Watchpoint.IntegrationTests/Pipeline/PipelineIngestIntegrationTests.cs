using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Nats;
using Watchpoint.Registry;
using Watchpoint.Registry.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Pipeline;

/// <summary>The ingest on real NATS, with the real registry where the test says so (spec PIP §14).</summary>
[Trait("layer", "I")]
[Trait("spec", "PIP")]
public sealed class PipelineIngestIntegrationTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
{
    private static readonly ChangeContext _context = new("test-user", "integration-test", "corr-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PIP_I_009_End_to_end_ingest_to_subscriber()
    {
        await nats.StartAsync(Ct);
        var state = new StateStandIn();
        await using var node = await RegistryNodeAsync(nameof(PIP_I_009_End_to_end_ingest_to_subscriber), state);
        var connection = ConnectionId.New();
        var devices = await SeedAsync(node, connection, 10);
        await node.GetRequiredService<IPipelineLifecycle>().StartAsync(Ct);
        var ingest = node.GetRequiredService<IObservationIngest>();

        // Per device 100 observations in order: every tenth names a property the type does not have; of the others,
        // every value is reported twice (the second time nothing changed).
        var results = await Task.WhenAll(devices.Select(async device =>
        {
            var batch = new List<Observation>();
            var valid = 0;
            for (var j = 0; j < 100; j++)
            {
                if (j % 10 == 9)
                {
                    batch.Add(IngestNode.Property(device, connection, "open") with { Property = PropertyPath.FromTrusted("doorPosition.colour") });
                    continue;
                }

                batch.Add(IngestNode.Property(device, connection, (valid / 2) % 2 == 0 ? "open" : "closed"));
                valid++;
            }

            return (Device: device, Batch: batch, Result: await ingest.IngestAsync(batch, Ct));
        }));

        // The counts of every call, and their sum over the 1,000 observations.
        results.ShouldAllBe(r => r.Result.Accepted == 45 && r.Result.Unchanged == 45 && r.Result.Coalesced == 0 && r.Result.Rejected.Count == 10);
        results.SelectMany(r => r.Result.Rejected).ShouldAllBe(r => r.Error.Code.Value == "model.property.not_found");

        // The subscriber received exactly the accepted records, per device in the order they were reported.
        state.Records.Count.ShouldBe(450);
        state.Touches.ShouldBe(450);
        var registry = node.GetRequiredService<ILogRecordTypeRegistry>();
        foreach (var (device, batch, _) in results)
        {
            var received = state.Records.Where(r => r.Envelope.Dev == device).ToList();
            received.Select(r => r.Sequence).ShouldBe(received.Select(r => r.Sequence).Order());
            var expected = batch.Where(o => o.Property == IngestNode.Position).Where((_, k) => k % 2 == 0).ToList();
            received.Select(r => r.Envelope.Id).ShouldBe(expected.Select(o => o.Id));
            received.Select(r => r.BodyAs<PropertyRecordBody>(registry).Value).ShouldBe(expected.Select(o => o.Value));
            received.ShouldAllBe(r => r.Envelope.Origin.Id == connection.Value && r.Envelope.P == Partitioning.Of(device));
        }

        // What the subscriber saw is what the log holds.
        var logged = new List<LogRecord>();
        await foreach (var record in node.GetRequiredService<IEventLogReader>().ReadPartitionsAsync(AllPartitions(), 1, Ct))
        {
            logged.Add(record);
        }

        logged.Select(r => r.Envelope.Id).Order().ShouldBe(state.Records.Select(r => r.Envelope.Id).Order());
    }

    [Fact]
    public async Task PIP_I_012_Crash_before_ack_loses_nothing_that_was_acknowledged()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_012_Crash_before_ack_loses_nothing_that_was_acknowledged));
        var directory = new MemoryDirectory();
        var connection = ConnectionId.New();
        var devices = Enumerable.Range(0, 32).Select(_ => directory.AddDoor(connection)).ToList();
        var acknowledged = new ConcurrentBag<Guid>();
        var sent = 0;

        await using (var node = await IngestNode.StartAsync(nats, scope, directory, Ct))
        {
            var ingest = node.Services.GetRequiredService<IObservationIngest>();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            // Eight producers ingest events as fast as they can.
            var producers = Enumerable.Range(0, 8).Select(p => Task.Run(
                async () =>
                {
                    for (var i = 0; !stop.IsCancellationRequested; i++)
                    {
                        var batch = Enumerable.Range(0, 20).Select(k => IngestNode.Event(devices[(p + i + k) % devices.Count], connection)).ToList();
                        Interlocked.Add(ref sent, batch.Count);
                        try
                        {
                            var result = await ingest.IngestAsync(batch, stop.Token);
                            var rejected = result.Rejected.Select(r => r.ObservationId).ToHashSet();
                            foreach (var observation in batch.Where(o => !rejected.Contains(o.Id)))
                            {
                                acknowledged.Add(observation.Id);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                },
                Ct)).ToList();

            // In the middle of the load the node "dies": its connections to the log are cut without any flush.
            await Eventually.UntilAsync(() => acknowledged.Count >= 2000, TestTimeouts.Container, "the load did not start", ct: Ct);
            await node.Services.GetRequiredService<NatsConnectionProvider>().DisposeAsync();
            await stop.CancelAsync();
            await Task.WhenAll(producers).WaitAsync(TestTimeouts.Container, Ct);
        }

        // After the restart every observation whose IngestAsync completed is in the log, exactly once.
        await using var restarted = await IngestNode.StartAsync(nats, scope, directory, Ct);
        var logged = new List<Guid>();
        await foreach (var record in restarted.Reader.ReadPartitionsAsync(AllPartitions(), 1, Ct))
        {
            logged.Add(record.Envelope.Id);
        }

        acknowledged.Count.ShouldBeGreaterThanOrEqualTo(2000);
        logged.Count.ShouldBe(logged.Distinct().Count());
        acknowledged.Except(logged).ShouldBeEmpty();
        logged.Count.ShouldBeLessThanOrEqualTo(sent);
    }

    [Fact]
    public async Task PIP_I_013_Recovery_handshake_with_real_state_module()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_013_Recovery_handshake_with_real_state_module));
        var directory = new MemoryDirectory();
        var connection = ConnectionId.New();
        var devices = Enumerable.Range(0, 20).Select(_ => directory.AddDoor(connection)).ToList();
        StateSnapshot snapshot;
        HashSet<Guid> beforeSnapshot;

        // First life of the node: values, a snapshot of the state, then more changes that only the log holds.
        var first = new StateStandIn();
        await using (var node = await StateNodeAsync(scope, directory, first))
        {
            var ingest = node.Services.GetRequiredService<IObservationIngest>();
            (await ingest.IngestAsync([.. devices.Select(d => IngestNode.Property(d, connection, "closed"))], Ct)).Accepted.ShouldBe(20);
            (await ingest.IngestAsync([.. devices.Select(d => IngestNode.Property(d, connection, "open"))], Ct)).Accepted.ShouldBe(20);
            snapshot = first.Snapshot();
            beforeSnapshot = [.. first.Applied.Keys];
            (await ingest.IngestAsync([.. devices.Take(10).Select(d => IngestNode.Property(d, connection, "forced"))], Ct)).Accepted.ShouldBe(10);
            (await ingest.IngestAsync([.. devices.Take(5).Select(d => IngestNode.Event(d, connection))], Ct)).Accepted.ShouldBe(5);
        }

        // Second life: the state restores its snapshot; the pipeline replays what came after its checkpoints.
        var restored = new StateStandIn(snapshot);
        await using var restarted = await StateNodeAsync(scope, directory, restored);

        // Applied once: the 15 records after the snapshot, none of the 40 before it.
        restored.Applied.Count.ShouldBe(15);
        restored.Applied.Values.ShouldAllBe(n => n == 1);
        restored.Applied.Keys.Intersect(beforeSnapshot).ShouldBeEmpty();
        foreach (var (device, index) in devices.Select((d, i) => (d, i)))
        {
            restored.ValueOf(device, IngestNode.Position).ShouldBe(WpValue.Enum(index < 10 ? "forced" : "open"));
        }

        restarted.Logs.WithEventId(1610).Count().ShouldBe(devices.Take(10).Select(Partitioning.Of).Distinct().Count(), "one line per partition that replayed records");

        // The change detection continues from the restored values: the same values change nothing, a new one does.
        var again = restarted.Services.GetRequiredService<IObservationIngest>();
        var same = await again.IngestAsync([.. devices.Select((d, i) => IngestNode.Property(d, connection, i < 10 ? "forced" : "open"))], Ct);
        (same.Accepted, same.Unchanged).ShouldBe((0, 20));
        var changed = await again.IngestAsync([IngestNode.Property(devices[0], connection, "closed")], Ct);
        changed.Accepted.ShouldBe(1);
        var last = restored.Records.Last();
        last.BodyAs<PropertyRecordBody>(restarted.Registry).Prev.ShouldBe(WpValue.Enum("forced"));
        restored.Applied.Values.ShouldAllBe(n => n == 1);
    }

    [Fact]
    public async Task PIP_I_015_Global_config_stream_consumer()
    {
        await nats.StartAsync(Ct);
        await using var node = await RegistryNodeAsync(nameof(PIP_I_015_Global_config_stream_consumer));
        var registry = node.GetRequiredService<ILogRecordTypeRegistry>();
        var revisions = new ConcurrentQueue<(long Sequence, RegistryChangedBody Body)>();

        // A module consuming the configuration stream with the consumer helper.
        await using var consumer = node.GetRequiredService<ILogConsumerFactory>()
            .CreateDurable("pip-i-015", LogStream.Cfg, [RecordKinds.RegistryChanged], new LogConsumerOptions());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var consuming = consumer.ProcessAsync(
            new LogProcessingOptions("pip-i-015", registry, NullLoggerOf(node)),
            (record, _) =>
            {
                revisions.Enqueue((record.Sequence, record.BodyAs<RegistryChangedBody>(registry)));
                return ValueTask.CompletedTask;
            },
            stop.Token);

        // The registry applies four batches: site and root, a server, ten devices, one device changed.
        var connection = ConnectionId.New();
        var devices = await SeedAsync(node, connection, 10);
        var writer = node.GetRequiredService<IRegistryWriter>();
        var device = node.GetRequiredService<IRegistryReader>().GetDevice(devices[0]).Value;
        (await writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.UpsertDevice(device with { Enabled = false }, device.Version)]), _context, Ct))
            .IsSuccess.ShouldBeTrue();

        await Eventually.UntilAsync(() => revisions.Count >= 4, TestTimeouts.Container, "the consumer did not receive the registry changes", ct: Ct);
        await stop.CancelAsync();
        await consuming.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        // In order, one record per applied batch, each with its revision and the entities it names.
        var received = revisions.ToList();
        received.Select(r => r.Sequence).ShouldBe([1L, 2L, 3L, 4L]);
        received.Select(r => r.Body.Revision).ShouldBe([1L, 2L, 3L, 4L]);
        received[0].Body.Changes.Select(c => (c.Entity, c.Kind)).ShouldBe([("site", "updated"), ("location", "created")]);
        received[1].Body.Changes.Select(c => (c.Entity, c.Kind)).ShouldBe([("server", "created")]);
        received[2].Body.Changes.Select(c => c.Id).ShouldBe(devices.Select(d => d.Value));
        received[3].Body.Changes.ShouldBe([new RegistryChangedEntity("device", devices[0].Value, "updated")]);
        received.ShouldAllBe(r => r.Body.Context == new RegistryChangedContext("test-user", "integration-test", "corr-1"));
    }

    private static HashSet<int> AllPartitions() => [.. Enumerable.Range(0, Partitioning.PartitionCount)];

    private static ILogger NullLoggerOf(ServiceProvider node) => node.GetRequiredService<ILoggerFactory>().CreateLogger("pip-i-015");

    /// <summary>The pipeline with the state stand-in as subscriber and source of last values; recovered and running.</summary>
    private Task<PipelineNode> StateNodeAsync(NatsScope scope, MemoryDirectory directory, StateStandIn state) =>
        IngestNode.StartAsync(nats, scope, directory, Ct, configure: services =>
        {
            services.AddSingleton<IPartitionSubscriber>(state);
            services.AddSingleton<ILastValueSource>(state);
        });

    /// <summary>Storage, Model, Registry and Pipeline composed like in the host, on PostgreSQL and NATS; streams provisioned.</summary>
    private async Task<ServiceProvider> RegistryNodeAsync(string test, IPartitionSubscriber? subscriber = null)
    {
        var db = await postgres.CreateDatabaseAsync(nameof(PipelineIngestIntegrationTests), Ct);
        var scope = nats.CreateScope(test);
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddWatchpointModel();
        services.AddWatchpointRegistry(SiteId.New());
        services.AddSingleton(new EventLogEndpoint(nats.Url, null));
        services.AddPipeline(PipelineNode.OptionsFor(scope));
        if (subscriber is not null)
        {
            services.AddSingleton(subscriber);
        }

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        await provider.GetRequiredService<IEventLogSetup>().EnsureStreamsAsync(Ct);

        // The registry's outbox publisher (a hosted service, started by the host) sends registry.changed to the log.
        await provider.GetRequiredService<RegistryOutboxPublisher>().StartAsync(Ct);
        return provider;
    }

    /// <summary>Site, root location, one server of the connection and <paramref name="count"/> doors, in three batches.</summary>
    private static async Task<List<DeviceId>> SeedAsync(ServiceProvider node, ConnectionId connection, int count)
    {
        var writer = node.GetRequiredService<IRegistryWriter>();
        var rootId = LocationId.New();
        var serverId = ServerId.New();
        var version = SemVersion.Parse("1.0.0").Value;
        var site = new Site(SiteId.New(), new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0);
        var root = new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0);
        var server = new Server(
            serverId, new TypeRef(TypeId.FromTrusted("generic.server"), version), connection, null, "srv-1", null, "Server", rootId, [],
            new Dictionary<string, WpValue>(), true, false, 0);
        var devices = Enumerable.Range(0, count).Select(i => new Device(
            DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), version), serverId, null, $"door-{i}", null, $"Door {i}", rootId, [],
            new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false,
            DateTimeOffset.UtcNow, 0)).ToList();
        foreach (var batch in new RegistryChange[][]
        {
            [new RegistryChange.UpdateSite(site), new RegistryChange.UpsertLocation(root)],
            [new RegistryChange.UpsertServer(server)],
            [.. devices.Select(d => new RegistryChange.UpsertDevice(d))],
        })
        {
            var applied = await writer.ApplyAsync(new RegistryChangeBatch(batch), _context, Ct);
            applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? null : applied.Error.Code.Value + " " + applied.Error.DeveloperDetail);
        }

        return [.. devices.Select(d => d.Id)];
    }
}
