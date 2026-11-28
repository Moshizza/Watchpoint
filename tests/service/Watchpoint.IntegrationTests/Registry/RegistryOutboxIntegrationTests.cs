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
using Watchpoint.Model;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Registry;
using Watchpoint.Registry.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.IntegrationTests.Registry;

/// <summary>
/// The registry's transactional outbox on real PostgreSQL and NATS (spec REG §6, §10; PIP §5.5; T1.09b): the revision
/// survives restarts, nothing is lost while the log is down or across a node restart, and a publish repeated after a crash
/// is stored once.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Trait("layer", "I")]
public sealed class RegistryOutboxIntegrationTests(PostgresContainerFixture postgres, NatsContainerFixture nats) : IAsyncDisposable
{
    private static readonly ChangeContext _context = new(UserId.New(), "api", null, "integration-test");
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task REG_I_011_Revision_persisted_across_node_restart()
    {
        var db = await DatabaseAsync(nameof(REG_I_011_Revision_persisted_across_node_restart));
        var rootId = LocationId.New();
        await using (var first = await Node.StartAsync(db))
        {
            (await first.Writer.ApplyAsync(SiteAndRoot(rootId), _context, Ct)).IsSuccess.ShouldBeTrue();
            (await first.Writer.ApplyAsync(Rename(first, rootId, "second"), _context, Ct)).IsSuccess.ShouldBeTrue();
            (await first.Outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([1L, 2L]);
        }

        // Without the pipeline module nothing publishes: the records wait in the outbox, and the next node continues the
        // revision from the database instead of restarting at 1.
        await using var second = await Node.StartAsync(db);
        (await second.Writer.ApplyAsync(Rename(second, rootId, "third"), _context, Ct)).IsSuccess.ShouldBeTrue();
        (await ScalarAsync<long>(db.ConnectionString, "SELECT value FROM wp_registry.revision;")).ShouldBe(3);
        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_registry.outbox WHERE sent_at IS NULL;")).ShouldBe(3);

        var records = await second.Outbox.ReadUnsentAsync(10, Ct);
        records.Select(r => r.Revision).ShouldBe([1L, 2L, 3L]);
        records[0].Context.ShouldBe(_context);
        records[0].Changes.Select(c => c.Entity).ShouldBe(["site", "location"]);
        records[2].Changes.ShouldBe([new EntityChange("location", rootId.Value, ChangeKind.Updated)]);
        records.Select(r => r.CreatedAt).ShouldBe(records.Select(r => r.CreatedAt).Order());
    }

    [Fact]
    public async Task PIP_I_017_Log_down_during_commits_everything_published_in_order_once_it_is_back()
    {
        // A NATS server of its own, so it can be stopped; short timeouts so the outage and the recovery are quick.
        await using var server = await RestartableNatsContainer.StartNewAsync(Ct);
        var db = await DatabaseAsync(nameof(PIP_I_017_Log_down_during_commits_everything_published_in_order_once_it_is_back));
        var scope = new NatsScope("t17.wp", "T17_");
        var options = PipelineNode.OptionsFor(scope);
        options.AppendTimeout = TimeSpan.FromSeconds(2);
        options.OutboxRetryDelayMin = TimeSpan.FromMilliseconds(200);
        options.OutboxRetryDelayMax = TimeSpan.FromSeconds(1);
        await using var node = await Node.StartAsync(db, server.Url, options);
        await node.StartPublisherAsync();

        var rootId = LocationId.New();
        (await node.Writer.ApplyAsync(SiteAndRoot(rootId), _context, Ct)).IsSuccess.ShouldBeTrue();
        await UntilAsync(async () => (await node.Outbox.ReadUnsentAsync(1, Ct)).Count == 0, "revision 1 was not published");

        // The log goes down. Registry writes still complete at once (the caller never waits for the log), the records queue up
        // in the outbox and the publisher retries.
        await server.StopAsync(Ct);
        for (var n = 2; n <= 6; n++)
        {
            var result = await node.Writer.ApplyAsync(Rename(node, rootId, "name " + n), _context, Ct).WaitAsync(TestTimeouts.Default, Ct);
            result.IsSuccess.ShouldBeTrue();
        }

        await Eventually.UntilAsync(() => node.Logs.WithEventId(1611).Any(), TestTimeouts.Container, "the publisher did not retry", ct: Ct);
        (await node.Outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([2L, 3L, 4L, 5L, 6L]);

        // The log is back: everything is published in revision order, once, and marked sent.
        await server.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => (await node.Outbox.ReadUnsentAsync(1, Ct)).Count == 0, "the outbox was not drained after the restart", TimeSpan.FromSeconds(60));
            var revisions = await node.ReadRevisionsAsync("pip-i-017", 6);
            revisions.ShouldBe([1L, 2L, 3L, 4L, 5L, 6L]);
            node.Logs.WithEventId(1612).ShouldNotBeEmpty();
            (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_registry.outbox WHERE sent_at IS NOT NULL;")).ShouldBe(6);
        }
        catch (Exception ex)
        {
            // The failure of this test was seen only in CI, without a cause: say what the publisher logged and what is unsent.
            var unsent = string.Join(",", (await node.Outbox.ReadUnsentAsync(10, CancellationToken.None)).Select(r => r.Revision));
            var logs = string.Join("\n", node.Logs.Entries.Where(e => e.Level >= LogLevel.Warning || e.EventId is 1611 or 1612).Select(e => $"  {e.EventId} {e.Level}: {e.RenderedMessage} {e.Exception?.GetType().Name}"));
            throw new InvalidOperationException($"PIP-I-017 failed after the log came back (unsent revisions: [{unsent}]); publisher log:\n{logs}", ex);
        }
    }

    [Fact]
    public async Task PIP_I_018_Node_restart_with_unsent_records_resumes_from_the_oldest()
    {
        await nats.StartAsync(Ct);
        var db = await DatabaseAsync(nameof(PIP_I_018_Node_restart_with_unsent_records_resumes_from_the_oldest));
        var scope = nats.CreateScope(nameof(PIP_I_018_Node_restart_with_unsent_records_resumes_from_the_oldest));
        var rootId = LocationId.New();

        // The first node commits three batches while nothing publishes (no pipeline module: the log was never reachable).
        await using (var stopped = await Node.StartAsync(db))
        {
            (await stopped.Writer.ApplyAsync(SiteAndRoot(rootId), _context, Ct)).IsSuccess.ShouldBeTrue();
            (await stopped.Writer.ApplyAsync(Rename(stopped, rootId, "two"), _context, Ct)).IsSuccess.ShouldBeTrue();
            (await stopped.Writer.ApplyAsync(Rename(stopped, rootId, "three"), _context, Ct)).IsSuccess.ShouldBeTrue();
        }

        // The next node publishes the backlog before anything else, then goes on with new batches.
        await using var node = await Node.StartAsync(db, nats.Url, PipelineNode.OptionsFor(scope));
        await node.StartPublisherAsync();
        await UntilAsync(async () => (await node.Outbox.ReadUnsentAsync(1, Ct)).Count == 0, "the backlog was not published");
        (await node.Writer.ApplyAsync(Rename(node, rootId, "four"), _context, Ct)).IsSuccess.ShouldBeTrue();

        var revisions = await node.ReadRevisionsAsync("pip-i-018", 4);
        revisions.ShouldBe([1L, 2L, 3L, 4L]);
        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_registry.outbox WHERE sent_at IS NULL;")).ShouldBe(0);
    }

    [Fact]
    public async Task PIP_I_019_Duplicate_publish_after_a_crash_between_publish_and_mark_is_deduplicated()
    {
        await nats.StartAsync(Ct);
        var db = await DatabaseAsync(nameof(PIP_I_019_Duplicate_publish_after_a_crash_between_publish_and_mark_is_deduplicated));
        var scope = nats.CreateScope(nameof(PIP_I_019_Duplicate_publish_after_a_crash_between_publish_and_mark_is_deduplicated));
        var rootId = LocationId.New();

        await using (var crashed = await Node.StartAsync(db, nats.Url, PipelineNode.OptionsFor(scope)))
        {
            await crashed.StartPublisherAsync();
            (await crashed.Writer.ApplyAsync(SiteAndRoot(rootId), _context, Ct)).IsSuccess.ShouldBeTrue();
            await UntilAsync(async () => (await crashed.Outbox.ReadUnsentAsync(1, Ct)).Count == 0, "revision 1 was not published");
            await crashed.StopPublisherAsync();

            // The log acknowledged revision 1, but the node died before the mark was stored.
            await ExecuteAsync(db.ConnectionString, "UPDATE wp_registry.outbox SET sent_at = NULL WHERE revision = 1;");
        }

        // After the restart the record is published again with the same id: the log keeps one copy (duplicate window).
        await using var node = await Node.StartAsync(db, nats.Url, PipelineNode.OptionsFor(scope));
        await node.StartPublisherAsync();
        await UntilAsync(async () => (await node.Outbox.ReadUnsentAsync(1, Ct)).Count == 0, "revision 1 was not published again");

        await using var client = new NatsClient(nats.Url);
        var stream = await client.CreateJetStreamContext().GetStreamAsync(scope.StreamPrefix + "WP_CFG", cancellationToken: Ct);
        stream.Info.State.Messages.ShouldBe(1);
        var revisions = await node.ReadRevisionsAsync("pip-i-019", 1);
        revisions.ShouldBe([1L]);
        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_registry.outbox WHERE sent_at IS NULL;")).ShouldBe(0);
    }

    private static RegistryChangeBatch SiteAndRoot(LocationId rootId) => new([
        new RegistryChange.UpdateSite(new Site(SiteId.New(), new LocalizedName("Main Site", new Dictionary<string, string>()), "UTC", 0)),
        new RegistryChange.UpsertLocation(new Location(rootId, null, new LocalizedName("Root", new Dictionary<string, string>()), LocationKind.Site, "", 0, null, null, null, 0, 0)),
    ]);

    private static RegistryChangeBatch Rename(Node node, LocationId id, string notes)
    {
        var current = node.Reader.GetLocation(id).Value;
        return new RegistryChangeBatch([new RegistryChange.UpsertLocation(current with { Notes = notes }, current.Version)]);
    }

    /// <summary>Polls an asynchronous condition until it holds; a bounded wait followed by assertions, not a timing limit.</summary>
    private static async Task UntilAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        var limit = timeout ?? TestTimeouts.Container;
        while (!await condition())
        {
            if (clock.GetElapsedTime(started) > limit)
            {
                throw new TestAssertionException($"Timed out after {limit.TotalMilliseconds:0} ms: {because}.");
            }

            await Task.Delay(TestTimeouts.PollInterval, clock, Ct);
        }
    }

    private async Task<PostgresDatabase> DatabaseAsync(string name)
    {
        var db = await postgres.CreateDatabaseAsync(nameof(RegistryOutboxIntegrationTests) + "." + name, Ct);
        _owned.Add(db);
        return db;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>A registry node on a database, optionally with the pipeline module on a NATS server (system clock).</summary>
    private sealed class Node : IAsyncDisposable
    {
        private Node(ServiceProvider services, LogCollector logs)
        {
            Services = services;
            Logs = logs;
        }

        public ServiceProvider Services { get; }

        public LogCollector Logs { get; }

        public IRegistryReader Reader => Services.GetRequiredService<IRegistryReader>();

        public IRegistryWriter Writer => Services.GetRequiredService<IRegistryWriter>();

        public IRegistryOutbox Outbox => Services.GetRequiredService<IRegistryOutbox>();

        public static async Task<Node> StartAsync(PostgresDatabase db, string? natsUrl = null, PipelineOptions? log = null)
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
            var logs = new LogCollector();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton(TimeProvider.System);
            services.AddWatchpointStorage(configuration, isDevelopment: true);
            services.AddPostgresStorage();
            services.AddWatchpointModel();
            services.AddWatchpointRegistry(SiteId.New());
            if (natsUrl is not null)
            {
                services.AddSingleton(new EventLogEndpoint(natsUrl, null));
                services.AddPipeline(log ?? new PipelineOptions());
            }

            var node = new Node(services.BuildServiceProvider(), logs);
            try
            {
                await node.Services.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
                if (natsUrl is not null)
                {
                    await node.Services.GetRequiredService<IEventLogSetup>().EnsureStreamsAsync(Ct);
                }
            }
            catch
            {
                await node.DisposeAsync();
                throw;
            }

            return node;
        }

        /// <summary>Starts the outbox publisher as the host would (hosted service).</summary>
        public Task StartPublisherAsync() => Services.GetRequiredService<RegistryOutboxPublisher>().StartAsync(Ct);

        public Task StopPublisherAsync() => Services.GetService<RegistryOutboxPublisher>()?.StopAsync(Ct) ?? Task.CompletedTask;

        /// <summary>Reads <paramref name="count"/> registry.changed records from the configuration stream, in log order, and returns their revisions.</summary>
        public async Task<List<long>> ReadRevisionsAsync(string consumerName, int count)
        {
            var registry = Services.GetRequiredService<ILogRecordTypeRegistry>();
            await using var consumer = Services.GetRequiredService<ILogConsumerFactory>()
                .CreateDurable(consumerName, LogStream.Cfg, [RecordKinds.RegistryChanged], new LogConsumerOptions());
            await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);
            var revisions = new List<long>();
            while (revisions.Count < count)
            {
                (await batches.MoveNextAsync().AsTask().WaitAsync(TestTimeouts.Container, Ct)).ShouldBeTrue();
                foreach (var record in batches.Current.Records)
                {
                    record.Envelope.Id.ShouldBe(RegistryOutboxPublisher.RecordId(record.BodyAs<RegistryChangedBody>(registry).Revision));
                    revisions.Add(record.BodyAs<RegistryChangedBody>(registry).Revision);
                }

                await batches.Current.AckAsync(Ct);
            }

            return revisions;
        }

        public async ValueTask DisposeAsync()
        {
            await StopPublisherAsync();
            await Services.DisposeAsync();
            Logs.Dispose();
        }
    }
}
