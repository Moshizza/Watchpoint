using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres.History;

/// <summary>
/// The history store on PostgreSQL (spec HIS §4–§7): TimescaleDB hypertables when the extension is installed, monthly range
/// partitions otherwise. Bulk writes use binary COPY into a temporary table and one <c>INSERT … SELECT … ON CONFLICT</c>
/// per batch, in one transaction.
/// </summary>
internal sealed partial class PostgresHistoryStore(NpgsqlDataSource source) : IHistoryStore
{
    private const string EventColumns =
        "time, id, seq, kind, category, severity, source_time, entity_id, entity_kind, server_id, location_id, location_ids, type_id, tag_keys, " +
        "origin_type, origin_id, payload, personal, person_refs, title_key, title_args, source_name, location_names, correlation";

    private const string AlarmColumns =
        "time, id, seq, rule_id, rule_key, rule_version, source_id, source_kind, source_name, location_id, location_ids, type_id, tag_keys, state, " +
        "priority_initial, priority_final, count, last_occurrence_at, acknowledged_at, acknowledged_by, resolved_at, closed_at, closed_by, report, " +
        "\"owner\", escalation_level, procedure_id, procedure_version, false_alarm";

    private const string CommandColumns =
        "time, id, seq, group_id, device_id, device_name, location_ids, path, arguments, issuer, issuer_user, reason, risk, priority, state, failure, " +
        "queued_at, dispatched_at, completed_at, verified_at, attempt, approval, correlation";

    private const string PropertyColumns = "time, device_id, path, v_num, v_text, v_bool, v_json, quality, source_time, record_id";

    private static readonly string[] _tables = ["events", "alarms", "alarm_timeline", "commands", "property_changes"];

    private static readonly Dictionary<string, string> _chunkIntervals = new(StringComparer.Ordinal)
    {
        ["events"] = "1 day",
        ["alarms"] = "7 days",
        ["alarm_timeline"] = "7 days",
        ["commands"] = "7 days",
        ["property_changes"] = "1 day",
    };

    private static readonly DateTime _binOrigin = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task EnsureSchemaAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('wp_history_schema'))", ct).ConfigureAwait(false);

        var timescale = false;
        await using (var probe = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb')", connection, transaction))
        {
            timescale = (bool)(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var script = PostgresProvider.EmbeddedSql("history-schema.sql")
            .Replace("/*PARTITION*/", timescale ? string.Empty : "PARTITION BY RANGE (time)", StringComparison.Ordinal);
        var existing = await ExistingTablesAsync(connection, transaction, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, script, ct).ConfigureAwait(false);

        foreach (var table in _tables)
        {
            if (timescale)
            {
                if (!existing.Contains(table))
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        $"SELECT create_hypertable('wp_history.{table}', 'time', chunk_time_interval => INTERVAL '{_chunkIntervals[table]}', if_not_exists => TRUE)",
                        ct).ConfigureAwait(false);
                }
            }
            else
            {
                await EnsurePartitionsAsync(connection, transaction, table, now, ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> WriteEventsAsync(IReadOnlyList<HistoryEventRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        return await InTransactionAsync(
            async (connection, transaction) =>
            {
                await CreateTempAsync(connection, transaction, "wp_history.events", "tmp_events", ct).ConfigureAwait(false);
                await using (var writer = await connection.BeginBinaryImportAsync($"COPY tmp_events ({EventColumns}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct).ConfigureAwait(false);
                        await Put(writer, r.Time, ct).ConfigureAwait(false);
                        await Put(writer, r.Id, ct).ConfigureAwait(false);
                        await Put(writer, r.Seq, ct).ConfigureAwait(false);
                        await Put(writer, r.Kind, ct).ConfigureAwait(false);
                        await Put(writer, r.Category, ct).ConfigureAwait(false);
                        await Put(writer, r.Severity, ct).ConfigureAwait(false);
                        await Put(writer, r.SourceTime, ct).ConfigureAwait(false);
                        await Put(writer, r.EntityId, ct).ConfigureAwait(false);
                        await Put(writer, r.EntityKind, ct).ConfigureAwait(false);
                        await Put(writer, r.ServerId, ct).ConfigureAwait(false);
                        await Put(writer, r.LocationId, ct).ConfigureAwait(false);
                        await PutUuidArray(writer, r.LocationIds, ct).ConfigureAwait(false);
                        await Put(writer, r.TypeId, ct).ConfigureAwait(false);
                        await PutTextArray(writer, r.TagKeys, ct).ConfigureAwait(false);
                        await Put(writer, r.OriginType, ct).ConfigureAwait(false);
                        await Put(writer, r.OriginId, ct).ConfigureAwait(false);
                        await PutJson(writer, r.PayloadJson, ct).ConfigureAwait(false);
                        await PutJson(writer, r.PersonalJson, ct).ConfigureAwait(false);
                        await PutTextArray(writer, r.PersonRefs, ct).ConfigureAwait(false);
                        await Put(writer, r.TitleKey, ct).ConfigureAwait(false);
                        await PutJson(writer, r.TitleArgsJson, ct).ConfigureAwait(false);
                        await Put(writer, r.SourceName, ct).ConfigureAwait(false);
                        await PutTextArray(writer, r.LocationNames, ct).ConfigureAwait(false);
                        await PutJson(writer, r.CorrelationJson, ct).ConfigureAwait(false);
                    }

                    await writer.CompleteAsync(ct).ConfigureAwait(false);
                }

                return await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO wp_history.events ({EventColumns}) SELECT {EventColumns} FROM tmp_events ON CONFLICT (id, time) DO NOTHING",
                    ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<int> WriteAlarmsAsync(IReadOnlyList<HistoryAlarmRow> alarms, IReadOnlyList<HistoryAlarmTimelineRow> timeline, CancellationToken ct)
    {
        if (alarms.Count == 0 && timeline.Count == 0)
        {
            return 0;
        }

        return await InTransactionAsync(
            async (connection, transaction) =>
            {
                var written = 0;
                if (alarms.Count > 0)
                {
                    await CreateTempAsync(connection, transaction, "wp_history.alarms", "tmp_alarms", ct).ConfigureAwait(false);
                    await using (var writer = await connection.BeginBinaryImportAsync($"COPY tmp_alarms ({AlarmColumns}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
                    {
                        foreach (var r in alarms)
                        {
                            await writer.StartRowAsync(ct).ConfigureAwait(false);
                            await Put(writer, r.Time, ct).ConfigureAwait(false);
                            await Put(writer, r.Id, ct).ConfigureAwait(false);
                            await Put(writer, r.Seq, ct).ConfigureAwait(false);
                            await Put(writer, r.RuleId, ct).ConfigureAwait(false);
                            await Put(writer, r.RuleKey, ct).ConfigureAwait(false);
                            await Put(writer, r.RuleVersion, ct).ConfigureAwait(false);
                            await Put(writer, r.SourceId, ct).ConfigureAwait(false);
                            await Put(writer, r.SourceKind, ct).ConfigureAwait(false);
                            await Put(writer, r.SourceName, ct).ConfigureAwait(false);
                            await Put(writer, r.LocationId, ct).ConfigureAwait(false);
                            await PutUuidArray(writer, r.LocationIds, ct).ConfigureAwait(false);
                            await Put(writer, r.TypeId, ct).ConfigureAwait(false);
                            await PutTextArray(writer, r.TagKeys, ct).ConfigureAwait(false);
                            await Put(writer, r.State, ct).ConfigureAwait(false);
                            await Put(writer, r.PriorityInitial, ct).ConfigureAwait(false);
                            await Put(writer, r.PriorityFinal, ct).ConfigureAwait(false);
                            await Put(writer, r.Count, ct).ConfigureAwait(false);
                            await Put(writer, r.LastOccurrenceAt, ct).ConfigureAwait(false);
                            await Put(writer, r.AcknowledgedAt, ct).ConfigureAwait(false);
                            await Put(writer, r.AcknowledgedBy, ct).ConfigureAwait(false);
                            await Put(writer, r.ResolvedAt, ct).ConfigureAwait(false);
                            await Put(writer, r.ClosedAt, ct).ConfigureAwait(false);
                            await Put(writer, r.ClosedBy, ct).ConfigureAwait(false);
                            await Put(writer, r.Report, ct).ConfigureAwait(false);
                            await PutJson(writer, r.OwnerJson, ct).ConfigureAwait(false);
                            await Put(writer, r.EscalationLevel, ct).ConfigureAwait(false);
                            await Put(writer, r.ProcedureId, ct).ConfigureAwait(false);
                            await Put(writer, r.ProcedureVersion, ct).ConfigureAwait(false);
                            await Put(writer, r.FalseAlarm, ct).ConfigureAwait(false);
                        }

                        await writer.CompleteAsync(ct).ConfigureAwait(false);
                    }

                    written += await ExecuteAsync(connection, transaction, AlarmUpsertSql, ct).ConfigureAwait(false);
                }

                if (timeline.Count > 0)
                {
                    await CreateTempAsync(connection, transaction, "wp_history.alarm_timeline", "tmp_timeline", ct).ConfigureAwait(false);
                    const string columns = "time, alarm_id, record_id, kind, actor_user, actor_session, client_kind, body";
                    await using (var writer = await connection.BeginBinaryImportAsync($"COPY tmp_timeline ({columns}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
                    {
                        foreach (var r in timeline)
                        {
                            await writer.StartRowAsync(ct).ConfigureAwait(false);
                            await Put(writer, r.Time, ct).ConfigureAwait(false);
                            await Put(writer, r.AlarmId, ct).ConfigureAwait(false);
                            await Put(writer, r.RecordId, ct).ConfigureAwait(false);
                            await Put(writer, r.Kind, ct).ConfigureAwait(false);
                            await Put(writer, r.ActorUser, ct).ConfigureAwait(false);
                            await Put(writer, r.ActorSession, ct).ConfigureAwait(false);
                            await Put(writer, r.ClientKind, ct).ConfigureAwait(false);
                            await PutJson(writer, r.BodyJson, ct).ConfigureAwait(false);
                        }

                        await writer.CompleteAsync(ct).ConfigureAwait(false);
                    }

                    written += await ExecuteAsync(
                        connection,
                        transaction,
                        $"INSERT INTO wp_history.alarm_timeline ({columns}) SELECT {columns} FROM tmp_timeline ON CONFLICT (record_id, time) DO NOTHING",
                        ct).ConfigureAwait(false);
                }

                return written;
            },
            ct).ConfigureAwait(false);
    }

    public async Task<int> WriteCommandsAsync(IReadOnlyList<HistoryCommandRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        return await InTransactionAsync(
            async (connection, transaction) =>
            {
                await CreateTempAsync(connection, transaction, "wp_history.commands", "tmp_commands", ct).ConfigureAwait(false);
                await using (var writer = await connection.BeginBinaryImportAsync($"COPY tmp_commands ({CommandColumns}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct).ConfigureAwait(false);
                        await Put(writer, r.Time, ct).ConfigureAwait(false);
                        await Put(writer, r.Id, ct).ConfigureAwait(false);
                        await Put(writer, r.Seq, ct).ConfigureAwait(false);
                        await Put(writer, r.GroupId, ct).ConfigureAwait(false);
                        await Put(writer, r.DeviceId, ct).ConfigureAwait(false);
                        await Put(writer, r.DeviceName, ct).ConfigureAwait(false);
                        await PutUuidArray(writer, r.LocationIds, ct).ConfigureAwait(false);
                        await Put(writer, r.Path, ct).ConfigureAwait(false);
                        await PutJson(writer, r.ArgumentsJson, ct).ConfigureAwait(false);
                        await PutJson(writer, r.IssuerJson, ct).ConfigureAwait(false);
                        await Put(writer, r.IssuerUser, ct).ConfigureAwait(false);
                        await Put(writer, r.Reason, ct).ConfigureAwait(false);
                        await Put(writer, r.Risk, ct).ConfigureAwait(false);
                        await Put(writer, r.Priority, ct).ConfigureAwait(false);
                        await Put(writer, r.State, ct).ConfigureAwait(false);
                        await PutJson(writer, r.FailureJson, ct).ConfigureAwait(false);
                        await Put(writer, r.QueuedAt, ct).ConfigureAwait(false);
                        await Put(writer, r.DispatchedAt, ct).ConfigureAwait(false);
                        await Put(writer, r.CompletedAt, ct).ConfigureAwait(false);
                        await Put(writer, r.VerifiedAt, ct).ConfigureAwait(false);
                        await Put(writer, r.Attempt, ct).ConfigureAwait(false);
                        await PutJson(writer, r.ApprovalJson, ct).ConfigureAwait(false);
                        await PutJson(writer, r.CorrelationJson, ct).ConfigureAwait(false);
                    }

                    await writer.CompleteAsync(ct).ConfigureAwait(false);
                }

                return await ExecuteAsync(connection, transaction, CommandUpsertSql, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<int> WritePropertyChangesAsync(IReadOnlyList<HistoryPropertyRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        return await InTransactionAsync(
            async (connection, transaction) =>
            {
                await CreateTempAsync(connection, transaction, "wp_history.property_changes", "tmp_props", ct).ConfigureAwait(false);
                await using (var writer = await connection.BeginBinaryImportAsync($"COPY tmp_props ({PropertyColumns}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct).ConfigureAwait(false);
                        await Put(writer, r.Time, ct).ConfigureAwait(false);
                        await Put(writer, r.DeviceId, ct).ConfigureAwait(false);
                        await Put(writer, r.Path, ct).ConfigureAwait(false);
                        if (r.VNum is { } num)
                        {
                            await writer.WriteAsync(num, NpgsqlDbType.Numeric, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await writer.WriteNullAsync(ct).ConfigureAwait(false);
                        }

                        await Put(writer, r.VText, ct).ConfigureAwait(false);
                        await Put(writer, r.VBool, ct).ConfigureAwait(false);
                        await PutJson(writer, r.VJson, ct).ConfigureAwait(false);
                        await Put(writer, r.Quality, ct).ConfigureAwait(false);
                        await Put(writer, r.SourceTime, ct).ConfigureAwait(false);
                        await Put(writer, r.RecordId, ct).ConfigureAwait(false);
                    }

                    await writer.CompleteAsync(ct).ConfigureAwait(false);
                }

                return await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO wp_history.property_changes ({PropertyColumns}) SELECT {PropertyColumns} FROM tmp_props ON CONFLICT (record_id, time) DO NOTHING",
                    ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryPropertyRow>> GetLastPropertyRowsAsync(IReadOnlyList<(Guid DeviceId, string Path)> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT p.time, p.device_id, p.path, p.v_num, p.v_text, p.v_bool, p.v_json::text, p.quality, p.source_time, p.record_id
            FROM unnest(@devices, @paths) AS k(device_id, path)
            CROSS JOIN LATERAL (
              SELECT * FROM wp_history.property_changes pc
              WHERE pc.device_id = k.device_id AND pc.path = k.path
              ORDER BY pc.time DESC LIMIT 1) p
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("devices", [.. keys.Select(k => k.DeviceId)]));
        command.Parameters.Add(new NpgsqlParameter<string[]>("paths", [.. keys.Select(k => k.Path)]));
        var result = new List<HistoryPropertyRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadProperty(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<HistoryPolicyOverrideRow>> GetPolicyOverridesAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT scope, key, path, policy::text FROM wp_history.policy_overrides", connection);
        var result = new List<HistoryPolicyOverrideRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new HistoryPolicyOverrideRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return result;
    }

    private async Task<int> InTransactionAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task<int>> work, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await work(connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private static Task<int> CreateTempAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string like, string name, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, $"CREATE TEMP TABLE {name} (LIKE {like}) ON COMMIT DROP", ct);

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ExistingTablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'wp_history'", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task EnsurePartitionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, DateTimeOffset now, CancellationToken ct)
    {
        var utc = now.UtcDateTime;
        var first = new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        for (var i = 0; i <= 4; i++)
        {
            var from = first.AddMonths(i);
            var to = from.AddMonths(1);
            var name = $"{table}_p{from.ToString("yyyyMM", CultureInfo.InvariantCulture)}";
            await ExecuteAsync(
                connection,
                transaction,
                $"CREATE TABLE IF NOT EXISTS wp_history.{name} PARTITION OF wp_history.{table} " +
                $"FOR VALUES FROM ('{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 00:00:00+00') TO ('{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 00:00:00+00')",
                ct).ConfigureAwait(false);
        }

        // Rows outside the created months land here instead of failing the write (retention and partition upkeep: T5.01).
        await ExecuteAsync(connection, transaction, $"CREATE TABLE IF NOT EXISTS wp_history.{table}_default PARTITION OF wp_history.{table} DEFAULT", ct).ConfigureAwait(false);
    }

    private const string AlarmUpsertSql = $"""
        INSERT INTO wp_history.alarms ({AlarmColumns}) SELECT {AlarmColumns} FROM tmp_alarms
        ON CONFLICT (id, time) DO UPDATE SET
          seq = GREATEST(alarms.seq, EXCLUDED.seq),
          state = CASE WHEN EXCLUDED.seq > alarms.seq THEN EXCLUDED.state ELSE alarms.state END,
          priority_final = CASE WHEN EXCLUDED.seq > alarms.seq THEN EXCLUDED.priority_final ELSE alarms.priority_final END,
          count = GREATEST(alarms.count, EXCLUDED.count),
          last_occurrence_at = GREATEST(alarms.last_occurrence_at, EXCLUDED.last_occurrence_at),
          acknowledged_by = CASE WHEN alarms.acknowledged_at IS NULL THEN EXCLUDED.acknowledged_by ELSE alarms.acknowledged_by END,
          acknowledged_at = COALESCE(alarms.acknowledged_at, EXCLUDED.acknowledged_at),
          resolved_at = COALESCE(alarms.resolved_at, EXCLUDED.resolved_at),
          closed_by = CASE WHEN alarms.closed_at IS NULL THEN EXCLUDED.closed_by ELSE alarms.closed_by END,
          closed_at = COALESCE(alarms.closed_at, EXCLUDED.closed_at),
          report = CASE WHEN EXCLUDED.seq > alarms.seq THEN COALESCE(EXCLUDED.report, alarms.report) ELSE COALESCE(alarms.report, EXCLUDED.report) END,
          "owner" = CASE WHEN EXCLUDED.seq > alarms.seq THEN COALESCE(EXCLUDED."owner", alarms."owner") ELSE COALESCE(alarms."owner", EXCLUDED."owner") END,
          escalation_level = GREATEST(alarms.escalation_level, EXCLUDED.escalation_level),
          procedure_id = CASE WHEN EXCLUDED.seq > alarms.seq THEN COALESCE(EXCLUDED.procedure_id, alarms.procedure_id) ELSE COALESCE(alarms.procedure_id, EXCLUDED.procedure_id) END,
          procedure_version = CASE WHEN EXCLUDED.seq > alarms.seq THEN COALESCE(EXCLUDED.procedure_version, alarms.procedure_version) ELSE COALESCE(alarms.procedure_version, EXCLUDED.procedure_version) END,
          false_alarm = CASE WHEN EXCLUDED.seq > alarms.seq THEN COALESCE(EXCLUDED.false_alarm, alarms.false_alarm) ELSE COALESCE(alarms.false_alarm, EXCLUDED.false_alarm) END
        """;

    // A row built from a record other than the request has path '' and risk/priority -1: the request wins its own fields whenever it arrives.
    private const string CommandUpsertSql = $"""
        INSERT INTO wp_history.commands ({CommandColumns}) SELECT {CommandColumns} FROM tmp_commands
        ON CONFLICT (id, time) DO UPDATE SET
          seq = GREATEST(commands.seq, EXCLUDED.seq),
          state = CASE WHEN EXCLUDED.seq > commands.seq THEN EXCLUDED.state ELSE commands.state END,
          group_id = COALESCE(commands.group_id, EXCLUDED.group_id),
          device_name = COALESCE(commands.device_name, EXCLUDED.device_name),
          location_ids = COALESCE(commands.location_ids, EXCLUDED.location_ids),
          path = CASE WHEN EXCLUDED.path <> '' THEN EXCLUDED.path ELSE commands.path END,
          arguments = CASE WHEN EXCLUDED.path <> '' THEN EXCLUDED.arguments ELSE commands.arguments END,
          issuer = CASE WHEN EXCLUDED.path <> '' THEN EXCLUDED.issuer ELSE commands.issuer END,
          issuer_user = CASE WHEN EXCLUDED.path <> '' THEN EXCLUDED.issuer_user ELSE commands.issuer_user END,
          reason = CASE WHEN EXCLUDED.path <> '' THEN EXCLUDED.reason ELSE commands.reason END,
          risk = CASE WHEN EXCLUDED.risk >= 0 THEN EXCLUDED.risk ELSE commands.risk END,
          priority = CASE WHEN EXCLUDED.priority >= 0 AND (commands.priority < 0 OR EXCLUDED.seq > commands.seq) THEN EXCLUDED.priority ELSE commands.priority END,
          failure = CASE WHEN EXCLUDED.seq > commands.seq THEN COALESCE(EXCLUDED.failure, commands.failure) ELSE COALESCE(commands.failure, EXCLUDED.failure) END,
          queued_at = COALESCE(commands.queued_at, EXCLUDED.queued_at),
          dispatched_at = COALESCE(commands.dispatched_at, EXCLUDED.dispatched_at),
          completed_at = COALESCE(commands.completed_at, EXCLUDED.completed_at),
          verified_at = COALESCE(commands.verified_at, EXCLUDED.verified_at),
          attempt = GREATEST(commands.attempt, EXCLUDED.attempt),
          approval = CASE WHEN EXCLUDED.seq > commands.seq THEN COALESCE(EXCLUDED.approval, commands.approval) ELSE COALESCE(commands.approval, EXCLUDED.approval) END,
          correlation = COALESCE(commands.correlation, EXCLUDED.correlation)
        """;

    private static ValueTask Put(NpgsqlBinaryImporter w, DateTimeOffset v, CancellationToken ct) => new(w.WriteAsync(v.UtcDateTime, NpgsqlDbType.TimestampTz, ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, DateTimeOffset? v, CancellationToken ct) =>
        v is { } t ? new(w.WriteAsync(t.UtcDateTime, NpgsqlDbType.TimestampTz, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, Guid v, CancellationToken ct) => new(w.WriteAsync(v, NpgsqlDbType.Uuid, ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, Guid? v, CancellationToken ct) =>
        v is { } g ? new(w.WriteAsync(g, NpgsqlDbType.Uuid, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, long v, CancellationToken ct) => new(w.WriteAsync(v, NpgsqlDbType.Bigint, ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, long? v, CancellationToken ct) =>
        v is { } n ? new(w.WriteAsync(n, NpgsqlDbType.Bigint, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, int v, CancellationToken ct) => new(w.WriteAsync(v, NpgsqlDbType.Integer, ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, short v, CancellationToken ct) => new(w.WriteAsync(v, NpgsqlDbType.Smallint, ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, short? v, CancellationToken ct) =>
        v is { } n ? new(w.WriteAsync(n, NpgsqlDbType.Smallint, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, bool? v, CancellationToken ct) =>
        v is { } b ? new(w.WriteAsync(b, NpgsqlDbType.Boolean, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask Put(NpgsqlBinaryImporter w, string? v, CancellationToken ct) =>
        v is not null ? new(w.WriteAsync(v, NpgsqlDbType.Text, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask PutJson(NpgsqlBinaryImporter w, string? v, CancellationToken ct) =>
        v is not null ? new(w.WriteAsync(v, NpgsqlDbType.Jsonb, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask PutUuidArray(NpgsqlBinaryImporter w, IReadOnlyList<Guid>? v, CancellationToken ct) =>
        v is not null ? new(w.WriteAsync(v.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ct)) : new(w.WriteNullAsync(ct));

    private static ValueTask PutTextArray(NpgsqlBinaryImporter w, IReadOnlyList<string>? v, CancellationToken ct) =>
        v is not null ? new(w.WriteAsync(v.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct)) : new(w.WriteNullAsync(ct));

}
