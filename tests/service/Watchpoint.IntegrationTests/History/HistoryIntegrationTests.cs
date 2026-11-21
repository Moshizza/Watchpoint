using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Shouldly;
using Watchpoint.History.Contracts;
using Watchpoint.IntegrationTests.Pipeline;
using Watchpoint.IntegrationTests.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.History;

/// <summary>History on real PostgreSQL (with and without TimescaleDB) and NATS (spec HIS §14, T2.13).</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Trait("layer", "I")]
[Trait("spec", "HIS")]
public sealed class HistoryIntegrationTests(PostgresContainerFixture postgres, NatsContainerFixture nats) : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan Wait => TestTimeouts.Container;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }
    }

    private static DateTimeOffset Now()
    {
        var now = TimeProvider.System.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
    }

    private async Task<HistoryNode> StartAsync(string name, HistorySettings? settings = null)
    {
        await postgres.StartAsync(Ct);
        await nats.StartAsync(Ct);
        var db = await postgres.CreateDatabaseAsync(name, Ct);
        _owned.Add(db);
        var pipeline = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(nats.CreateScope(name)), Ct);
        _owned.Add(pipeline);
        var node = await HistoryNode.CreateAsync(db, pipeline, settings);
        _owned.Add(node);
        return node;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(Ct))!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, string>> PairsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var result = new Dictionary<string, string>();
        while (await reader.ReadAsync(Ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static HistoryEventRow EventRow(DateTimeOffset time, Guid? entity = null, string kind = "door.forced") => new(
        time, Guid.CreateVersion7(), 0, kind, 0, 5, null, entity, 0, null, null, null, null, null, 0, null, "{}", null, null, "k", "{}", null, null, null);

    private static LogEnvelope Envelope<T>(HistoryNode node, string kind, T body, DeviceId? device, DateTimeOffset? ts = null, RecordOrigin? origin = null, bool global = false) => new(
        Guid.CreateVersion7(), kind, ts ?? Now(), null, global || device is null ? 0 : Partitioning.Of(device.Value), global ? null : device, null,
        origin ?? new RecordOrigin(RecordOriginType.Connection, Guid.CreateVersion7()), RecordCorrelation.None, node.Pipeline.Registry.SerializeBody(kind, body));

    private static EventRecordBody Forced(int n) => new("doorPosition.forced", new Dictionary<string, WpValue> { ["n"] = WpValue.Int(n) }, Severity.Alarm);

    [Fact]
    public async Task HIS_I_001_Schema_is_created_as_hypertables_when_TimescaleDB_is_present()
    {
        await postgres.StartAsync(Ct);
        var db = await postgres.CreateDatabaseAsync(nameof(HIS_I_001_Schema_is_created_as_hypertables_when_TimescaleDB_is_present), Ct);
        _owned.Add(db);
        var storage = new StorageNode(db.ConnectionString);
        _owned.Add(storage);
        var store = storage.Provider.CreateHistoryStore(storage.Database);

        // Two nodes starting together create the schema once, and starting again changes nothing.
        await Task.WhenAll(store.EnsureSchemaAsync(Now(), Ct), store.EnsureSchemaAsync(Now(), Ct));
        await store.EnsureSchemaAsync(Now(), Ct);

        var intervals = await PairsAsync(
            db.ConnectionString,
            "SELECT hypertable_name::text, time_interval::text FROM timescaledb_information.dimensions WHERE hypertable_schema = 'wp_history'");
        intervals.ShouldBe(new Dictionary<string, string>
        {
            ["events"] = "1 day",
            ["alarms"] = "7 days",
            ["alarm_timeline"] = "7 days",
            ["commands"] = "7 days",
            ["property_changes"] = "1 day",
        });
        (await ScalarAsync<int>(db.ConnectionString, "SELECT count(*) FROM pg_partitioned_table p JOIN pg_class c ON c.oid = p.partrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'wp_history'")).ShouldBe(0);
        (await ScalarAsync<int>(db.ConnectionString, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'wp_history' AND indexname LIKE 'events_%'")).ShouldBeGreaterThanOrEqualTo(4);
        (await ScalarAsync<int>(db.ConnectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'wp_history' AND table_name = 'policy_overrides'")).ShouldBe(1);

        // A write through the store works on hypertables, and the same row twice is one row.
        var row = EventRow(Now());
        (await store.WriteEventsAsync([row], Ct)).ShouldBe(1);
        (await store.WriteEventsAsync([row], Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task HIS_I_002_Schema_is_created_as_partitioned_tables_without_TimescaleDB()
    {
        await postgres.StartAsync(Ct);
        var plain = "t_plain_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(postgres.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{plain}\" TEMPLATE template1", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var cs = new NpgsqlConnectionStringBuilder(postgres.AdminConnectionString) { Database = plain }.ConnectionString;
        _owned.Add(new DropDatabase(postgres.AdminConnectionString, plain, cs));
        var storage = new StorageNode(cs);
        _owned.Add(storage);
        var store = storage.Provider.CreateHistoryStore(storage.Database);
        var now = Now();

        await store.EnsureSchemaAsync(now, Ct);
        await store.EnsureSchemaAsync(now, Ct);

        (await ScalarAsync<int>(cs, "SELECT count(*) FROM pg_extension WHERE extname = 'timescaledb'")).ShouldBe(0);
        (await ScalarAsync<int>(cs, "SELECT count(*) FROM pg_partitioned_table p JOIN pg_class c ON c.oid = p.partrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'wp_history'")).ShouldBe(5);

        // The month before, the current month and three months ahead, plus the default partition that takes anything else.
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = Enumerable.Range(-1, 5).Select(i => "events_p" + month.AddMonths(i).ToString("yyyyMM", CultureInfo.InvariantCulture)).Append("events_default").Order();
        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent WHERE p.relname = 'events' ORDER BY 1", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var names = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }

            names.ShouldBe(expected);
        }

        // Rows in the covered months and far outside them are all stored (the default partition), once.
        var rows = new[] { EventRow(now), EventRow(now.AddMonths(2)), EventRow(now.AddYears(15)), EventRow(now.AddYears(-3)) };
        (await store.WriteEventsAsync(rows, Ct)).ShouldBe(4);
        (await store.WriteEventsAsync(rows, Ct)).ShouldBe(0);
        (await ScalarAsync<int>(cs, "SELECT count(*) FROM wp_history.events")).ShouldBe(4);
        (await ScalarAsync<int>(cs, "SELECT count(*) FROM wp_history.events_default")).ShouldBe(2);
    }

    private sealed class DropDatabase(string adminConnectionString, string name, string connectionString) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using (var pooled = new NpgsqlConnection(connectionString))
            {
                NpgsqlConnection.ClearPool(pooled);
            }

            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task HIS_I_003_Events_are_written_end_to_end_and_stay_single_after_a_restart_and_a_lost_acknowledgement()
    {
        var node = await StartAsync(nameof(HIS_I_003_Events_are_written_end_to_end_and_stay_single_after_a_restart_and_a_lost_acknowledgement));
        var door = node.AddDevice(3);
        var writer = node.Pipeline.Writer;
        const string Count = "SELECT count(*) FROM wp_history.events";

        // Device events, a system event and an operator note, device-bound and global.
        var first = Enumerable.Range(1, 40).Select(n => Envelope(node, RecordKinds.Event, Forced(n), door)).ToList();
        await writer.AppendAsync(first, Ct);
        await writer.AppendGlobalAsync(
            LogStream.Sys,
            Envelope(node, HistoryRecordKinds.SysEvent, new SysEventBody("identity.accountLocked", new Dictionary<string, WpValue> { ["user"] = WpValue.String("jdoe") }, Severity.Warning, ["user"]), null, global: true),
            Ct);
        await writer.AppendGlobalAsync(
            LogStream.Sys,
            Envelope(node, HistoryRecordKinds.OperatorNote, new OperatorNoteBody("Checked the door"), null, origin: new RecordOrigin(RecordOriginType.User, Guid.CreateVersion7()), global: true),
            Ct);

        var service = await node.StartServiceAsync(ct: Ct);
        (await node.WaitForCountAsync(Count, 42, Wait, Ct)).ShouldBe(42);
        await service.StopAsync(Ct);

        // Restart: only the records after the last acknowledged one are read, and nothing is stored twice.
        var second = Enumerable.Range(41, 10).Select(n => Envelope(node, RecordKinds.Event, Forced(n), door)).ToList();
        await writer.AppendAsync(second, Ct);
        await node.StartServiceAsync(ct: Ct);
        (await node.WaitForCountAsync(Count, 52, Wait, Ct)).ShouldBe(52);
        node.Logs.WithEventId(3201).Any(l => Equals(l.Properties["Consumer"], "his-events") && Equals(l.Properties["Sequence"], 41L)).ShouldBeTrue("the restarted consumer must resume after the last acknowledged record");

        // Rows: log sequence for WP_LOG, 0 for WP_SYS; denormalised source; personal fields split.
        (await node.CountAsync("SELECT count(*) FROM wp_history.events WHERE seq = 0 AND category IN (2, 3)", Ct)).ShouldBe(2);
        (await node.CountAsync("SELECT count(*) FROM wp_history.events WHERE seq > 0 AND source_name = 'Door' AND location_names = ARRAY['Site', 'Floor']", Ct)).ShouldBe(50);
        (await node.CountAsync("SELECT count(*) FROM wp_history.events WHERE kind = 'identity.accountLocked' AND personal ? 'user' AND NOT payload ? 'user'", Ct)).ShouldBe(1);

        // The process dies between the commit and the acknowledgement: the records come again and are stored once.
        var third = Enumerable.Range(51, 25).Select(n => Envelope(node, RecordKinds.Event, Forced(n), door)).ToList();
        await writer.AppendAsync(third, Ct);
        using var crashed = new CancellationTokenSource();
        var dying = await node.StartServiceAsync(c => new CrashBeforeAckFactory(c, () => crashed.Cancel()), Ct);
        (await node.WaitForCountAsync(Count, 77, Wait, Ct)).ShouldBe(77);
        await dying.StopAsync(Ct);
        var survivor = await node.StartServiceAsync(ct: Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, Ct);
        (await node.CountAsync(Count, Ct)).ShouldBe(77);
        (await node.CountAsync("SELECT count(DISTINCT id) FROM wp_history.events", Ct)).ShouldBe(77);
        await survivor.StopAsync(Ct);

        // The journal reads them back, newest first, in pages.
        var from = Now().AddHours(-1);
        var page = (await node.Queries.QueryEventsAsync(new EventQuery(from, Now().AddHours(1), Kinds: ["doorPosition.*"]), new PageRequest(30), Ct)).ShouldBeSuccess();
        page.Items.Count.ShouldBe(30);
        page.NextCursor.ShouldNotBeNull();
        page.Items.Select(e => e.ReceivedTime).ShouldBe(page.Items.Select(e => e.ReceivedTime).OrderDescending());
    }

    private static AlarmTransitionBody Alarm(Guid id, AlarmHistoryState state, int count, DateTimeOffset raised, Guid? by = null, DateTimeOffset? ack = null, DateTimeOffset? closed = null, int priority = 3) => new(
        id, Guid.CreateVersion7(), "door.forced", 1, state, priority, 3, count, raised, raised.AddSeconds(count), AcknowledgedAt: ack, AcknowledgedBy: ack is null ? null : by,
        ClosedAt: closed, ClosedBy: closed is null ? null : by, Actor: by is null ? null : new AlarmActor(by, null, 1));

    [Fact]
    public async Task HIS_I_004_Alarms_and_their_timeline_end_to_end()
    {
        var node = await StartAsync(nameof(HIS_I_004_Alarms_and_their_timeline_end_to_end));
        var door = node.AddDevice(5);
        var user = Guid.CreateVersion7();
        var one = Guid.CreateVersion7();
        var two = Guid.CreateVersion7();
        var raised = Now();
        var records = new[]
        {
            Envelope(node, "alarm.raised", Alarm(one, AlarmHistoryState.Active, 1, raised), door, raised),
            Envelope(node, "alarm.raised", Alarm(two, AlarmHistoryState.Active, 1, raised.AddSeconds(1)), door, raised.AddSeconds(1)),
            Envelope(node, "alarm.occurred", Alarm(one, AlarmHistoryState.Active, 2, raised), door, raised.AddSeconds(2)),
            Envelope(node, "alarm.acknowledged", Alarm(one, AlarmHistoryState.Acknowledged, 2, raised, user, ack: raised.AddSeconds(3)), door, raised.AddSeconds(3)),
            Envelope(node, "alarm.closed", Alarm(one, AlarmHistoryState.Closed, 2, raised, user, ack: raised.AddSeconds(3), closed: raised.AddSeconds(4), priority: 4), door, raised.AddSeconds(4)),
        };
        await node.Pipeline.Writer.AppendAsync(records, Ct);

        // The crash trick again: the batch is stored, not acknowledged, and read a second time by the next process.
        using var crashed = new CancellationTokenSource();
        var dying = await node.StartServiceAsync(c => new CrashBeforeAckFactory(c, () => crashed.Cancel()), Ct);
        (await node.WaitForCountAsync("SELECT count(*) FROM wp_history.alarm_timeline", 5, Wait, Ct)).ShouldBe(5);
        await dying.StopAsync(Ct);
        await node.StartServiceAsync(ct: Ct);

        // Another alarm record after the restart, for the alarm that is already closed (a late timeline entry must not reopen it).
        var late = Envelope(node, "alarm.commented", Alarm(one, AlarmHistoryState.Closed, 2, raised, user, ack: raised.AddSeconds(3), closed: raised.AddSeconds(4), priority: 4), door, raised.AddSeconds(5));
        await node.Pipeline.Writer.AppendAsync([late], Ct);
        (await node.WaitForCountAsync("SELECT count(*) FROM wp_history.alarm_timeline", 6, Wait, Ct)).ShouldBe(6);

        (await node.CountAsync("SELECT count(*) FROM wp_history.alarms", Ct)).ShouldBe(2);
        var alarms = (await node.Queries.QueryAlarmsAsync(new AlarmHistoryQuery(raised.AddMinutes(-1), raised.AddMinutes(1)), new PageRequest(10), Ct)).ShouldBeSuccess();
        alarms.Items.Count.ShouldBe(2);
        var closed = alarms.Items.Single(a => a.Id == one);
        closed.State.ShouldBe(AlarmHistoryState.Closed);
        closed.Count.ShouldBe(2);
        closed.FinalPriority.ShouldBe(4);
        closed.InitialPriority.ShouldBe(3);
        closed.AcknowledgedBy.ShouldBe(user);
        closed.ClosedAt.ShouldBe(raised.AddSeconds(4));
        closed.SourceName.ShouldBe("Door");
        alarms.Items.Single(a => a.Id == two).State.ShouldBe(AlarmHistoryState.Active);

        var timeline = (await node.Queries.GetAlarmTimelineAsync(AlarmId.From(one), Ct)).ShouldBeSuccess();
        timeline.Select(t => t.Kind).ShouldBe(["alarm.raised", "alarm.occurred", "alarm.acknowledged", "alarm.closed", "alarm.commented"]);
        timeline[2].ActorUser.ShouldBe(user);

        // Filters: state and rule.
        var active = (await node.Queries.QueryAlarmsAsync(new AlarmHistoryQuery(raised.AddMinutes(-1), raised.AddMinutes(1), States: [AlarmHistoryState.Active]), new PageRequest(10), Ct)).ShouldBeSuccess();
        active.Items.Select(a => a.Id).ShouldBe([two]);
    }

    [Fact]
    public async Task HIS_I_005_Commands_end_to_end()
    {
        var node = await StartAsync(nameof(HIS_I_005_Commands_end_to_end));
        var door = node.AddDevice(7);
        var user = Guid.CreateVersion7();
        var requested = Now();
        var id1 = Guid.CreateVersion7();
        var id2 = Guid.CreateVersion7();
        var args = JsonDocument.Parse("""{"duration":"PT5S"}""").RootElement.Clone();
        var issuer = JsonDocument.Parse($$"""{"type":"user","id":"{{user}}"}""").RootElement.Clone();
        LogEnvelope Rec(string kind, CommandTransitionBody body, int ms) => Envelope(node, kind, body, door, requested.AddMilliseconds(ms));
        await node.Pipeline.Writer.AppendAsync(
        [
            Rec("command.requested", new CommandTransitionBody(id1, requested, Path: "openable.open", Arguments: args, Issuer: issuer, IssuerUser: user, Reason: "visitor", Risk: 2, Priority: 1), 0),
            Rec("command.requested", new CommandTransitionBody(id2, requested.AddMilliseconds(5), Path: "openable.open", Arguments: args, Issuer: issuer, IssuerUser: user, Risk: 2, Priority: 1), 5),
            Rec("command.queued", new CommandTransitionBody(id1, requested, Priority: 1), 10),
            Rec("command.dispatched", new CommandTransitionBody(id1, requested, Attempt: 1), 20),
            Rec("command.failed", new CommandTransitionBody(id2, requested.AddMilliseconds(5), Failure: JsonDocument.Parse("""{"code":"commands.device_offline"}""").RootElement.Clone()), 25),
            Rec("command.succeeded", new CommandTransitionBody(id1, requested), 30),
            Rec("command.verified", new CommandTransitionBody(id1, requested), 40),
        ], Ct);

        await node.StartServiceAsync(ct: Ct);
        (await node.WaitForCountAsync("SELECT count(*) FROM wp_history.commands WHERE state IN (8, 12)", 2, Wait, Ct)).ShouldBe(2);

        var page = (await node.Queries.QueryCommandsAsync(new CommandHistoryQuery(requested.AddMinutes(-1), requested.AddMinutes(1)), new PageRequest(10), Ct)).ShouldBeSuccess();
        page.Items.Count.ShouldBe(2);
        var done = page.Items.Single(c => c.Id == id1);
        done.State.ShouldBe(CommandHistoryState.Verified);
        done.Path.ShouldBe("openable.open");
        done.Reason.ShouldBe("visitor");
        done.IssuerUser.ShouldBe(user);
        done.DeviceName.ShouldBe("Door");
        done.QueuedAt.ShouldBe(requested.AddMilliseconds(10));
        done.DispatchedAt.ShouldBe(requested.AddMilliseconds(20));
        done.CompletedAt.ShouldBe(requested.AddMilliseconds(30));
        done.VerifiedAt.ShouldBe(requested.AddMilliseconds(40));
        done.Attempt.ShouldBe(1);
        done.Arguments.GetProperty("duration").GetString().ShouldBe("PT5S");
        var failed = page.Items.Single(c => c.Id == id2);
        failed.State.ShouldBe(CommandHistoryState.Failed);
        failed.Failure.ShouldNotBeNull().GetProperty("code").GetString().ShouldBe("commands.device_offline");

        // Filters: state, issuer, device scope.
        var onlyFailed = (await node.Queries.QueryCommandsAsync(new CommandHistoryQuery(requested.AddMinutes(-1), requested.AddMinutes(1), States: [CommandHistoryState.Failed]), new PageRequest(10), Ct)).ShouldBeSuccess();
        onlyFailed.Items.Select(c => c.Id).ShouldBe([id2]);
        var byIssuer = (await node.Queries.QueryCommandsAsync(new CommandHistoryQuery(requested.AddMinutes(-1), requested.AddMinutes(1), IssuerUser: UserId.From(user)), new PageRequest(10), Ct)).ShouldBeSuccess();
        byIssuer.Items.Count.ShouldBe(2);
        var otherScope = new HistoryScope(null, [DeviceId.New()]);
        (await node.Queries.QueryCommandsAsync(new CommandHistoryQuery(requested.AddMinutes(-1), requested.AddMinutes(1), Scope: otherScope), new PageRequest(10), Ct)).ShouldBeSuccess().Items.ShouldBeEmpty();
        var ownScope = new HistoryScope(null, [door]);
        (await node.Queries.QueryCommandsAsync(new CommandHistoryQuery(requested.AddMinutes(-1), requested.AddMinutes(1), Scope: ownScope), new PageRequest(10), Ct)).ShouldBeSuccess().Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HIS_I_006_Property_changes_are_stored_with_the_deadband_for_analog_properties()
    {
        var node = await StartAsync(nameof(HIS_I_006_Property_changes_are_stored_with_the_deadband_for_analog_properties));
        var sensor = node.AddDevice(9);
        var plain = node.AddDevice(10);
        node.Devices[plain] = node.Devices[plain] with { TypeRef = new TypeRef(TypeId.FromTrusted("ac.plain"), new SemVersion(1, 0, 0)), TypeId = "ac.plain" };
        await node.Store.EnsureSchemaAsync(Now(), Ct);
        await node.ExecuteAsync("INSERT INTO wp_history.policy_overrides (scope, key, path, policy) VALUES ('type', 'ac.door', 'temperature.value', '{\"mode\":\"deadband\",\"type\":\"absolute\",\"threshold\":2}')", Ct);

        // A random walk with small steps, one quality change in the middle, and a boolean that is stored on every change.
        var random = new Random(1234);
        var t0 = Now().AddMinutes(-10);
        var values = new List<decimal>();
        var value = 20m;
        for (var i = 0; i < 200; i++)
        {
            value += (decimal)(random.NextDouble() - 0.45) * 1.2m;
            values.Add(Math.Round(value, 2));
        }

        var batch = new List<LogEnvelope>();
        for (var i = 0; i < values.Count; i++)
        {
            var quality = i == 100 ? Quality.Uncertain : Quality.Good;
            batch.Add(Envelope(node, RecordKinds.Property, new PropertyRecordBody("temperature.value", WpValue.Decimal(values[i]), quality, null), sensor, t0.AddSeconds(i)));
            batch.Add(Envelope(node, RecordKinds.Property, new PropertyRecordBody("temperature.value", WpValue.Decimal(values[i]), Quality.Good, null), plain, t0.AddSeconds(i)));
        }

        await node.Pipeline.Writer.AppendAsync(batch, Ct);

        // The expected rows by the rule of spec HIS §6: first value, |v - last stored| >= 2, quality change, 1 h without a stored value.
        var expected = new List<decimal>();
        decimal? last = null;
        var lastQuality = Quality.Good;
        for (var i = 0; i < values.Count; i++)
        {
            var quality = i == 100 ? Quality.Uncertain : Quality.Good;
            if (last is null || Math.Abs(values[i] - last.Value) >= 2m || quality != lastQuality)
            {
                expected.Add(values[i]);
                last = values[i];
                lastQuality = quality;
            }
        }

        expected.Count.ShouldBeInRange(2, 150);
        await node.StartServiceAsync(ct: Ct);
        (await node.WaitForCountAsync($"SELECT count(*) FROM wp_history.property_changes WHERE device_id = '{plain.Value}'", values.Count, Wait, Ct)).ShouldBe(values.Count);
        (await node.WaitForCountAsync($"SELECT count(*) FROM wp_history.property_changes WHERE device_id = '{sensor.Value}'", expected.Count, Wait, Ct)).ShouldBe(expected.Count);
        (await node.CountAsync($"SELECT count(*) FROM wp_history.property_changes WHERE device_id = '{sensor.Value}' AND quality = 1", Ct)).ShouldBe(1);

        var series = (await node.Queries.QuerySeriesAsync(new SeriesQuery(sensor, PropertyPath.FromTrusted("temperature.value"), t0.AddMinutes(-1), Now().AddMinutes(1), null), Ct)).ShouldBeSuccess();
        series.Select(p => p.Value!.Value.AsDecimal()).ShouldBe(expected);
    }

    [Fact]
    public async Task HIS_I_007_Throughput_of_5000_records_per_second_sustained_into_history()
    {
        var node = await StartAsync(nameof(HIS_I_007_Throughput_of_5000_records_per_second_sustained_into_history), new HistorySettings { LagWarning = TimeSpan.FromHours(1) });
        var devices = Enumerable.Range(0, 64).Select(p => PipelineNode.DeviceIn(p)).ToList();
        const int Total = 50_000;
        var now = Now();
        for (var chunk = 0; chunk < Total / 1000; chunk++)
        {
            var batch = Enumerable.Range(0, 1000).Select(i =>
            {
                var n = chunk * 1000 + i;
                return Envelope(node, RecordKinds.Property, new PropertyRecordBody("temperature.value", WpValue.Int(n), Quality.Good, null), devices[n % devices.Count], now.AddMilliseconds(n % 1000));
            }).ToList();
            await node.Pipeline.Writer.AppendAsync(batch, Ct);
        }

        var limit = TimingLimits.For(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40));
        var clock = Stopwatch.StartNew();
        await node.StartServiceAsync(ct: Ct);
        var written = await node.WaitForCountAsync("SELECT count(*) FROM wp_history.property_changes", Total, limit * 3, Ct);
        clock.Stop();

        written.ShouldBe(Total);
        var rate = Total / clock.Elapsed.TotalSeconds;
        TestContext.Current.SendDiagnosticMessage($"HIS-I-007: {Total} records in {clock.Elapsed.TotalSeconds:F1} s = {rate:F0} records/s (limit {limit.TotalSeconds:F0} s)");
        clock.Elapsed.ShouldBeLessThan(limit);
    }

    [Fact]
    public async Task HIS_I_008_Journal_query_by_location_subtree_and_scope_is_fast_on_a_million_rows()
    {
        await postgres.StartAsync(Ct);
        var db = await postgres.CreateDatabaseAsync(nameof(HIS_I_008_Journal_query_by_location_subtree_and_scope_is_fast_on_a_million_rows), Ct);
        _owned.Add(db);
        var storage = new StorageNode(db.ConnectionString);
        _owned.Add(storage);
        var store = storage.Provider.CreateHistoryStore(storage.Database);
        var queries = new Watchpoint.History.Internal.Queries.HistoryQueries(store, new NoTagKeys(), new HistorySettings());
        await store.EnsureSchemaAsync(Now(), Ct);
        const string Load = """
            WITH l AS (SELECT array_agg(gen_random_uuid()) a FROM generate_series(1, 1000))
            INSERT INTO wp_history.events (time, id, seq, kind, category, severity, entity_id, entity_kind, location_id, location_ids, origin_type, payload, title_key, title_args)
            SELECT now() - (g % 604800) * interval '1 second', gen_random_uuid(), g, 'door.forced', 0, (g % 7)::smallint, gen_random_uuid(), 0,
                   l.a[1 + g % 1000], ARRAY[l.a[1], l.a[1 + g % 1000]], 0, '{}', 'k', '{}'
            FROM generate_series(1, 1000000) g, l
            """;
        await using (var connection = new NpgsqlConnection(db.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using (var load = new NpgsqlCommand(Load, connection) { CommandTimeout = 600 })
            {
                await load.ExecuteNonQueryAsync(Ct);
            }

            await using var analyze = new NpgsqlCommand("ANALYZE wp_history.events", connection) { CommandTimeout = 600 };
            await analyze.ExecuteNonQueryAsync(Ct);
        }

        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_history.events")).ShouldBe(1_000_000);
        var location = LocationId.From(await ScalarAsync<Guid>(db.ConnectionString, "SELECT location_id FROM wp_history.events WHERE seq = 2"));
        var to = Now().AddDays(-1);
        var from = to.AddDays(-1);
        var limit = TimingLimits.For(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));

        async Task<TimeSpan> RunAsync(EventQuery query)
        {
            // One warm-up, then the median of five.
            (await queries.QueryEventsAsync(query, new PageRequest(100), Ct)).ShouldBeSuccess();
            var times = new List<TimeSpan>();
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                var page = (await queries.QueryEventsAsync(query, new PageRequest(100), Ct)).ShouldBeSuccess();
                clock.Stop();
                page.Items.ShouldNotBeEmpty();
                times.Add(clock.Elapsed);
            }

            return times.Order().ElementAt(2);
        }

        var subtree = await RunAsync(new EventQuery(from, to, LocationsSubtree: [location]));
        var scoped = await RunAsync(new EventQuery(from, to, Scope: new HistoryScope([location], null)));
        var unfiltered = await RunAsync(new EventQuery(from, to));
        TestContext.Current.SendDiagnosticMessage($"HIS-I-008: subtree {subtree.TotalMilliseconds:F0} ms, scope {scoped.TotalMilliseconds:F0} ms, unfiltered {unfiltered.TotalMilliseconds:F0} ms (limit {limit.TotalMilliseconds:F0} ms)");
        subtree.ShouldBeLessThan(limit);
        scoped.ShouldBeLessThan(limit);
        unfiltered.ShouldBeLessThan(limit);

        // The results really are restricted: every event of the subtree query belongs to the location.
        var page = (await queries.QueryEventsAsync(new EventQuery(from, to, LocationsSubtree: [location]), new PageRequest(100), Ct)).ShouldBeSuccess();
        page.Items.ShouldAllBe(e => e.Source != null && e.Source.LocationId == location.Value);
        (await queries.QueryEventsAsync(new EventQuery(from, to, MinSeverity: Severity.Alarm, LocationsSubtree: [location]), new PageRequest(100), Ct)).ShouldBeSuccess().Items.ShouldAllBe(e => e.Severity >= Severity.Alarm);
    }

    private sealed class NoTagKeys : Watchpoint.History.Internal.IHistoryTagKeys
    {
        public IReadOnlyList<string> KeysOf(IReadOnlyCollection<TagId> tags) => [];
    }

    [Fact]
    public async Task HIS_I_009_Series_raw_and_bucketed()
    {
        await postgres.StartAsync(Ct);
        var db = await postgres.CreateDatabaseAsync(nameof(HIS_I_009_Series_raw_and_bucketed), Ct);
        _owned.Add(db);
        var storage = new StorageNode(db.ConnectionString);
        _owned.Add(storage);
        var store = storage.Provider.CreateHistoryStore(storage.Database);
        var queries = new Watchpoint.History.Internal.Queries.HistoryQueries(store, new NoTagKeys(), new HistorySettings());
        await store.EnsureSchemaAsync(Now(), Ct);
        var device = DeviceId.New();
        var other = DeviceId.New();
        var path = PropertyPath.FromTrusted("temperature.value");
        var start = new DateTimeOffset(Now().Year, Now().Month, 1, 0, 0, 0, TimeSpan.Zero).AddDays(1);

        // One value per minute for three days: value = minute number.
        var rows = Enumerable.Range(0, 3 * 24 * 60)
            .Select(m => new HistoryPropertyRow(start.AddMinutes(m), device.Value, path.Value, m, null, null, null, 0, null, Guid.CreateVersion7()))
            .Concat([new HistoryPropertyRow(start, other.Value, path.Value, 999, null, null, null, 0, null, Guid.CreateVersion7())])
            .Concat([new HistoryPropertyRow(start.AddMinutes(1), device.Value, "door.state", null, "open", null, null, 0, null, Guid.CreateVersion7())])
            .ToList();
        (await store.WritePropertyChangesAsync(rows, Ct)).ShouldBe(rows.Count);

        var raw = (await queries.QuerySeriesAsync(new SeriesQuery(device, path, start, start.AddDays(3), null), Ct)).ShouldBeSuccess();
        raw.Count.ShouldBe(4320);
        raw.Select(p => p.Time).ShouldBe(raw.Select(p => p.Time).Order());
        raw[61].Value.ShouldBe(WpValue.Decimal(61m));
        raw[61].Count.ShouldBe(1);

        // The range is half open: [from, to).
        (await queries.QuerySeriesAsync(new SeriesQuery(device, path, start.AddMinutes(10), start.AddMinutes(20), null), Ct)).ShouldBeSuccess().Select(p => p.Value!.Value.AsDecimal()).ShouldBe(Enumerable.Range(10, 10).Select(m => (decimal)m));

        // Hourly buckets: 72 of them, 60 values each, with min, average and max of the minute numbers inside.
        var hourly = (await queries.QuerySeriesAsync(new SeriesQuery(device, path, start, start.AddDays(3), TimeSpan.FromHours(1)), Ct)).ShouldBeSuccess();
        hourly.Count.ShouldBe(72);
        hourly.ShouldAllBe(b => b.Count == 60 && b.Value == null);
        hourly[0].Min.ShouldBe(0m);
        hourly[0].Max.ShouldBe(59m);
        hourly[0].Avg.ShouldBe(29.5m);
        hourly[2].Min.ShouldBe(120m);
        hourly[2].Max.ShouldBe(179m);
        hourly[2].Avg.ShouldBe(149.5m);
        hourly[2].Time.ShouldBe(start.AddHours(2));

        // A text property buckets to counts only; another device's values never leak in.
        var text = (await queries.QuerySeriesAsync(new SeriesQuery(device, PropertyPath.FromTrusted("door.state"), start, start.AddDays(1), null), Ct)).ShouldBeSuccess();
        text.Single().Value.ShouldBe(WpValue.String("open"));

        // Over 10,000 raw points: refused, with the way out. The same range in buckets is fine.
        var more = Enumerable.Range(0, 7000).Select(m => new HistoryPropertyRow(start.AddDays(3).AddMinutes(m), device.Value, path.Value, m, null, null, null, 0, null, Guid.CreateVersion7())).ToList();
        await store.WritePropertyChangesAsync(more, Ct);
        var tooMany = (await queries.QuerySeriesAsync(new SeriesQuery(device, path, start, start.AddDays(10), null), Ct)).ShouldFailWith("history.query.too_many_points");
        tooMany.Args["max"].ShouldBe(10_000);
        (await queries.QuerySeriesAsync(new SeriesQuery(device, path, start, start.AddDays(10), TimeSpan.FromHours(1)), Ct)).ShouldBeSuccess().Count.ShouldBeGreaterThan(72);

        // Counts per bucket, with a group and a filter, through the same store.
        var counts = (await queries.CountAsync(
            new CountQuery("events", Now().AddDays(-1), Now().AddDays(1), TimeSpan.FromDays(1), ["kind"], default), Ct)).ShouldBeSuccess();
        counts.ShouldBeEmpty();
    }

    [Fact]
    public async Task HIS_I_010_Database_outage_grows_the_lag_without_losing_records_and_catches_up_after_recovery()
    {
        var node = await StartAsync(nameof(HIS_I_010_Database_outage_grows_the_lag_without_losing_records_and_catches_up_after_recovery));
        var door = node.AddDevice(11);
        var writer = node.Pipeline.Writer;
        await writer.AppendAsync(Enumerable.Range(1, 20).Select(n => Envelope(node, RecordKinds.Event, Forced(n), door)).ToList(), Ct);
        await node.StartServiceAsync(ct: Ct);
        (await node.WaitForCountAsync("SELECT count(*) FROM wp_history.events", 20, Wait, Ct)).ShouldBe(20);

        // The database stops accepting connections and drops the ones it has.
        async Task SetAvailableAsync(bool available)
        {
            await using var admin = new NpgsqlConnection(postgres.AdminConnectionString);
            await admin.OpenAsync(Ct);
            await using var alter = new NpgsqlCommand($"ALTER DATABASE \"{node.Database.Name}\" WITH ALLOW_CONNECTIONS {(available ? "true" : "false")}", admin);
            await alter.ExecuteNonQueryAsync(Ct);
            if (!available)
            {
                await using var kill = new NpgsqlCommand($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{node.Database.Name}'", admin);
                await kill.ExecuteNonQueryAsync(Ct);
            }
        }

        await SetAvailableAsync(false);
        await writer.AppendAsync(Enumerable.Range(21, 60).Select(n => Envelope(node, RecordKinds.Event, Forced(n), door)).ToList(), Ct);

        // The writer keeps trying; the lag passes the (short) warning limit and is reported as a system event in the log.
        var warned = await WaitForAsync(async () =>
        {
            await foreach (var record in node.Pipeline.Reader.ReadGlobalAsync(LogStream.Sys, 1, Ct))
            {
                if (record.Envelope.Kind == HistoryRecordKinds.SysEvent
                    && record.BodyAs<SysEventBody>(node.Pipeline.Registry).Kind == "history.lagWarning")
                {
                    return true;
                }
            }

            return false;
        });
        warned.ShouldBeTrue("a lag warning must be raised while the database is down");
        node.Logs.WithEventId(3203).ShouldNotBeEmpty();

        // Recovery: everything that arrived meanwhile is stored, nothing twice, and the warning itself ends up in the journal.
        await SetAvailableAsync(true);
        (await node.WaitForCountAsync("SELECT count(*) FROM wp_history.events WHERE kind = 'doorPosition.forced'", 80, Wait * 2, Ct)).ShouldBe(80);
        (await node.CountAsync("SELECT count(DISTINCT id) FROM wp_history.events WHERE kind = 'doorPosition.forced'", Ct)).ShouldBe(80);
        (await node.WaitForCountAsync("SELECT count(*) FROM wp_history.events WHERE kind = 'history.lagWarning'", 1, Wait, Ct)).ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Wait;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }

        return false;
    }
}
