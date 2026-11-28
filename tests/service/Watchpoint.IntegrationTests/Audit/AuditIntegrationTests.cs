using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SortDirection = Watchpoint.Audit.Contracts.SortDirection;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Audit;

/// <summary>The audit trail on real PostgreSQL (spec AUD §14, T3.06).</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Trait("layer", "I")]
[Trait("spec", "AUD")]
public sealed class AuditIntegrationTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private static readonly Guid _alice = new("00000000-0000-7000-8000-0000000000a1");
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly string _fallback = Path.Combine(Path.GetTempPath(), "wp-audit-it-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        if (Directory.Exists(_fallback))
        {
            Directory.Delete(_fallback, recursive: true);
        }
    }

    [Fact]
    public async Task AUD_I_001_Sign_in_publish_rights_copy_command_and_alarm_acknowledgement_leave_entries_with_correlation_ids()
    {
        var (node, _) = await StartAsync();
        var sink = node.Writer;
        var user = UserId.From(_alice);
        var copyOperation = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        var configVersion = Guid.CreateVersion7();
        var alarmId = Guid.CreateVersion7();
        var door = Guid.CreateVersion7();
        var actor = AuditActor.User(_alice, "Alice", AuditClientKind.Desktop, "WS-1", "10.0.0.5");
        AuditRecord Record(string action, Guid correlation, AuditSeverity severity, params AuditObjectRef[] objects) =>
            new(new AuditAction(action), AuditOutcome.Success, actor, objects, null, JsonSerializer.SerializeToElement(new { password = "never", step = action }), severity, correlation);

        // Sign-in goes through the identity adapter, as the identity module writes it.
        await new IdentityAuditAdapter(sink).RecordAsync(
            new IdentityAuditRecord("identity.signedIn", IdentityAuditOutcome.Success, user, null, null, IdentityAuditSeverity.Normal,
                new Dictionary<string, string?> { ["client"] = "desktop" }, DateTimeOffset.UtcNow), Ct);
        await sink.WriteAsync(Record("config.published", configVersion, AuditSeverity.Sensitive, new AuditObjectRef("configuration", configVersion)), Ct);
        await sink.WriteAsync(Record("rights.copyApplied", copyOperation, AuditSeverity.Sensitive, new AuditObjectRef("user", Guid.CreateVersion7())), Ct);
        await sink.WriteAsync(Record("commands.requested", commandId, AuditSeverity.Normal, new AuditObjectRef("device", door)), Ct);
        await sink.WriteAsync(Record("alarms.acknowledged", alarmId, AuditSeverity.Critical, new AuditObjectRef("alarm", alarmId)), Ct);
        await UntilHeadAsync(node, 5);

        var from = DateTimeOffset.UtcNow.AddHours(-1);
        var all = new AuditQuery(from, DateTimeOffset.UtcNow.AddHours(1), Sort: SortDirection.Ascending);
        var entries = (await node.Query.QueryAsync(all, new PageRequest(100), Ct)).Value.Items;
        entries.Select(e => e.Action.Value).ShouldBe(["identity.signedIn", "config.published", "rights.copyApplied", "commands.requested", "alarms.acknowledged"]);
        entries.Select(e => e.Sequence).ShouldBe([1L, 2, 3, 4, 5]);

        // Each entry is found by its correlation id, with the filters running in PostgreSQL.
        foreach (var (correlation, action) in new[] { (configVersion, "config.published"), (copyOperation, "rights.copyApplied"), (commandId, "commands.requested"), (alarmId, "alarms.acknowledged") })
        {
            (await node.Query.QueryAsync(all with { CorrelationId = correlation }, new PageRequest(10), Ct)).Value.Items.ShouldHaveSingleItem().Action.Value.ShouldBe(action);
        }

        (await node.Query.QueryAsync(all with { ObjectType = "device", ObjectId = door }, new PageRequest(10), Ct)).Value.Items.ShouldHaveSingleItem().Action.Value.ShouldBe("commands.requested");
        (await node.Query.QueryAsync(all with { Actors = [_alice], ActionPrefixes = ["rights.", "alarms."] }, new PageRequest(10), Ct)).Value.Items.Count.ShouldBe(2);
        (await node.Query.QueryAsync(all with { MinSeverity = AuditSeverity.Critical }, new PageRequest(10), Ct)).Value.Items.ShouldHaveSingleItem();

        // Secrets never reach the row, and the whole chain verifies.
        entries.Where(e => e.Details is not null).ShouldAllBe(e => !e.Details!.Value.GetRawText().Contains("never"));
        (await node.Verifier.VerifyAsync("node-it", 1, null, Ct)).Valid.ShouldBeTrue();
        var one = await node.Query.GetAsync(entries[1].Id, Ct);
        one.ShouldNotBeNull().Details!.Value.GetProperty("step").GetString().ShouldBe("config.published");
    }

    [Fact]
    public async Task AUD_I_002_Update_is_rejected_by_the_trigger_and_the_role_and_delete_only_works_for_the_maintenance_role()
    {
        var (node, db) = await StartAsync();
        await node.Writer.WriteAsync(Critical("identity.signedIn"), Ct);
        await node.Writer.WriteAsync(Critical("identity.signedOut"), Ct);

        // The owner (and so the service connection) cannot update or delete.
        (await ErrorAsync(db, "UPDATE wp_audit.records SET reason = 'x'")).ShouldBe("42501");
        (await ErrorAsync(db, "DELETE FROM wp_audit.records")).ShouldBe("42501");

        // The writer role has neither privilege.
        (await ErrorAsync(db, "UPDATE wp_audit.records SET reason = 'x'", "wp_audit_writer")).ShouldBe("42501");
        (await ErrorAsync(db, "DELETE FROM wp_audit.records", "wp_audit_writer")).ShouldBe("42501");
        (await ErrorAsync(db, "INSERT INTO wp_audit.records (chain_id, sequence, occurred_at, recorded_at, action, outcome, severity, actor, objects, object_ids, prev_hash, hash) " +
            "VALUES ('x', 1, now(), now(), 'a.b', 0, 0, '{}', '[]', '{}', '\\x00', '\\x00')", "wp_audit_maint")).ShouldBe("42501");

        // The maintenance role cannot update either; it can delete (retention).
        (await ErrorAsync(db, "UPDATE wp_audit.records SET reason = 'x'", "wp_audit_maint")).ShouldBe("42501");
        (await ErrorAsync(db, "DELETE FROM wp_audit.records WHERE sequence = 1", "wp_audit_maint")).ShouldBeNull();
        (await Count(db)).ShouldBe(1);

        // The store's retention path uses that role.
        await node.Store.AddCheckpointAsync(new AuditCheckpointRow("node-it", 2, (await node.Store.GetHeadAsync("node-it", Ct))!.Hash, DateTimeOffset.UtcNow, [1], "k"), Ct);
        (await node.Store.PurgeUpToAsync("node-it", 2, Ct)).ShouldBe(1);
        (await Count(db)).ShouldBe(0);
    }

    [Fact]
    public async Task AUD_I_003_Manual_sql_tampering_is_detected_by_on_demand_verification()
    {
        var (node, db) = await StartAsync();
        for (var i = 0; i < 5; i++)
        {
            await node.Writer.WriteAsync(Critical("identity.signedIn"), Ct);
        }

        await node.Checkpoints.CreateAsync(Ct);
        (await node.Verifier.VerifyAsync("node-it", 1, null, Ct)).Valid.ShouldBeTrue();

        // Someone with database access switches the guard off and edits a record.
        await ExecuteAsync(db, "ALTER TABLE wp_audit.records DISABLE TRIGGER USER");
        await ExecuteAsync(db, "UPDATE wp_audit.records SET reason = 'edited by hand' WHERE sequence = 3");

        var report = await node.Verifier.VerifyAsync("node-it", 1, null, Ct);
        report.Valid.ShouldBeFalse();
        report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.HashMismatch && i.Sequence == 3);

        // Deleting the newest records is caught by the signed checkpoint.
        await ExecuteAsync(db, "UPDATE wp_audit.records SET reason = NULL WHERE sequence = 3");
        (await node.Verifier.VerifyAsync("node-it", 1, null, Ct)).Valid.ShouldBeTrue();
        await ExecuteAsync(db, "DELETE FROM wp_audit.records WHERE sequence > 4");
        (await node.Verifier.VerifyAsync("node-it", 1, null, Ct)).Issues.ShouldContain(i => i.Kind == VerificationIssueKind.MissingAfterCheckpoint);
    }

    [Fact]
    public async Task AUD_I_004_A_crash_in_the_middle_of_a_batch_leaves_no_gaps_after_the_restart()
    {
        var db = await AuditDatabase.CreateAsync(postgres, nameof(AUD_I_004_A_crash_in_the_middle_of_a_batch_leaves_no_gaps_after_the_restart), Ct);
        _owned.Add(db);

        // A batch with a duplicate sequence fails as a whole: the transaction leaves no partial rows.
        await using (var first = await AuditNode.StartAsync(db.ConnectionString, _fallback))
        {
            for (var i = 0; i < 3; i++)
            {
                await first.Writer.WriteAsync(Critical("identity.signedIn"), Ct);
            }

            var head = (await first.Store.GetHeadAsync("node-it", Ct))!;
            var row = (await first.Store.ReadChainAsync("node-it", 3, 3, 1, Ct))[0];
            var a = row with { Sequence = head.Sequence + 1 };
            var b = row with { Sequence = head.Sequence + 1 };
            await Should.ThrowAsync<PostgresException>(async () => await first.Store.AppendAsync([a, b], Ct));
            (await first.Store.GetHeadAsync("node-it", Ct))!.Sequence.ShouldBe(3);
        }

        // The store is down while records arrive; at shutdown they go to the fallback file.
        var down = new FailingStore();
        await using (var crashing = await AuditNode.StartAsync(db.ConnectionString, _fallback, s => s with { ShutdownTimeout = TimeSpan.FromMilliseconds(300) }, real => down.Wrap(real)))
        {
            down.Fail = true;
            for (var i = 0; i < 4; i++)
            {
                await crashing.Writer.WriteAsync(AuditRecordOf("identity.signedOut"), Ct);
            }

            await crashing.Writer.StopAsync(Ct);
        }

        Directory.GetFiles(_fallback, "fallback-*.jsonl").ShouldNotBeEmpty();

        // The restart ingests the fallback first: sequences continue without a gap and the chain verifies.
        await using var restarted = await AuditNode.StartAsync(db.ConnectionString, _fallback);
        await UntilHeadAsync(restarted, 7);
        (await restarted.Store.ReadChainAsync("node-it", 1, null, 100, Ct)).Select(r => r.Sequence).ShouldBe([1L, 2, 3, 4, 5, 6, 7]);
        (await restarted.Verifier.VerifyAsync("node-it", 1, null, Ct)).Valid.ShouldBeTrue();
        Directory.GetFiles(_fallback, "fallback-*.jsonl").ShouldBeEmpty();
    }

    [Fact]
    public async Task AUD_U_020_Write_throughput_is_at_least_5000_records_per_second_sustained()
    {
        var (node, _) = await StartAsync(s => s with { BatchDelay = TimeSpan.FromMilliseconds(50) });
        const int Producers = 8;
        const int PerProducer = 5_000;
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();

        await Task.WhenAll(Enumerable.Range(0, Producers).Select(_ => Task.Run(
            async () =>
            {
                for (var i = 0; i < PerProducer; i++)
                {
                    await node.Writer.WriteAsync(AuditRecordOf("commands.requested"), Ct);
                }
            },
            Ct)));
        await UntilHeadAsync(node, Producers * PerProducer);

        var seconds = clock.GetElapsedTime(started).TotalSeconds;
        (Producers * PerProducer / seconds).ShouldBeGreaterThanOrEqualTo(5_000, $"{Producers * PerProducer} records took {seconds:F2} s");
    }

    private static AuditRecord AuditRecordOf(string action, AuditSeverity severity = AuditSeverity.Normal) =>
        new(new AuditAction(action), AuditOutcome.Success, AuditActor.User(_alice, "Alice"), [new AuditObjectRef("device", Guid.CreateVersion7())], null, null, severity);

    private static AuditRecord Critical(string action) => AuditRecordOf(action, AuditSeverity.Critical);

    private async Task<(AuditNode Node, AuditDatabase Db)> StartAsync(Func<AuditSettings, AuditSettings>? tweak = null)
    {
        var db = await AuditDatabase.CreateAsync(postgres, "AuditNode" + Guid.NewGuid().ToString("N")[..8], Ct);
        _owned.Add(db);
        var node = await AuditNode.StartAsync(db.ConnectionString, _fallback, tweak);
        _owned.Add(node);
        return (node, db);
    }

    private static async Task UntilHeadAsync(AuditNode node, long sequence)
    {
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        while ((await node.Store.GetHeadAsync("node-it", Ct))?.Sequence is not { } head || head < sequence)
        {
            clock.GetElapsedTime(started).ShouldBeLessThan(TestTimeouts.Container * 4, $"the head did not reach {sequence}");
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, Ct);
        }
    }

    private static async Task ExecuteAsync(AuditDatabase db, string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> Count(AuditDatabase db)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM wp_audit.records", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Runs <paramref name="sql"/> (as <paramref name="role"/>, when given) and returns the SQLSTATE of the error, or <see langword="null"/>.</summary>
    private static async Task<string?> ErrorAsync(AuditDatabase db, string sql, string? role = null)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        try
        {
            if (role is not null)
            {
                await using var set = new NpgsqlCommand($"SET ROLE {role}", connection);
                await set.ExecuteNonQueryAsync(Ct);
            }

            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    /// <summary>A store that can be switched off, as when the database connection is lost.</summary>
    private sealed class FailingStore
    {
        public bool Fail { get; set; }

        public IAuditStore Wrap(IAuditStore inner) => new Proxy(inner, this);

        private sealed class Proxy(IAuditStore inner, FailingStore owner) : IAuditStore
        {
            public Task EnsureSchemaAsync(CancellationToken ct) => inner.EnsureSchemaAsync(ct);

            public Task<AuditChainHead?> GetHeadAsync(string chainId, CancellationToken ct) => owner.Fail ? throw new NpgsqlException("connection lost") : inner.GetHeadAsync(chainId, ct);

            public Task AppendAsync(IReadOnlyList<AuditRow> rows, CancellationToken ct) => owner.Fail ? throw new NpgsqlException("connection lost") : inner.AppendAsync(rows, ct);

            public Task<IReadOnlyList<AuditRow>> ReadChainAsync(string chainId, long fromSequence, long? toSequence, int limit, CancellationToken ct) => inner.ReadChainAsync(chainId, fromSequence, toSequence, limit, ct);

            public Task<IReadOnlyList<string>> ListChainsAsync(CancellationToken ct) => inner.ListChainsAsync(ct);

            public Task<IReadOnlyList<AuditRow>> QueryAsync(AuditRowFilter filter, AuditCursorPosition? after, int limit, CancellationToken ct) => inner.QueryAsync(filter, after, limit, ct);

            public Task<AuditRow?> GetAsync(long id, CancellationToken ct) => inner.GetAsync(id, ct);

            public Task AddCheckpointAsync(AuditCheckpointRow checkpoint, CancellationToken ct) => inner.AddCheckpointAsync(checkpoint, ct);

            public Task<IReadOnlyList<AuditCheckpointRow>> GetCheckpointsAsync(string chainId, CancellationToken ct) => inner.GetCheckpointsAsync(chainId, ct);

            public Task<AuditVerificationState?> GetVerificationStateAsync(string chainId, CancellationToken ct) => inner.GetVerificationStateAsync(chainId, ct);

            public Task SaveVerificationStateAsync(AuditVerificationState state, CancellationToken ct) => inner.SaveVerificationStateAsync(state, ct);

            public Task<long> PurgeUpToAsync(string chainId, long sequence, CancellationToken ct) => inner.PurgeUpToAsync(chainId, sequence, ct);
        }
    }
}
