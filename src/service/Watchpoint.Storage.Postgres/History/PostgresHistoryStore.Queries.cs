using System.Data.Common;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres.History;

internal sealed partial class PostgresHistoryStore
{
    private const string QueryTimedOut = "57014";

    public async Task<IReadOnlyList<HistoryEventRow>> QueryEventsAsync(HistoryEventFilter f, int limit, TimeSpan timeout, CancellationToken ct)
    {
        var q = new QueryBuilder();
        q.Where("time >= @from AND time < @to", ("from", Utc(f.From), NpgsqlDbType.TimestampTz), ("to", Utc(f.To), NpgsqlDbType.TimestampTz));
        if (f.Categories is { Count: > 0 })
        {
            q.Where("category = ANY(@categories)", ("categories", f.Categories.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Smallint));
        }

        if (f.KindsExact is { Count: > 0 } || f.KindPrefixes is { Count: > 0 })
        {
            var parts = new List<string>();
            if (f.KindsExact is { Count: > 0 })
            {
                parts.Add("kind = ANY(@kinds)");
                q.Param("kinds", f.KindsExact.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
            }

            if (f.KindPrefixes is { Count: > 0 })
            {
                parts.Add("kind LIKE ANY(@kindLikes)");
                q.Param("kindLikes", f.KindPrefixes.Select(p => EscapeLike(p) + "%").ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
            }

            q.Where("(" + string.Join(" OR ", parts) + ")");
        }

        if (f.MinSeverity is { } severity)
        {
            q.Where("severity >= @severity", ("severity", severity, NpgsqlDbType.Smallint));
        }

        if (f.LocationsSubtree is { Count: > 0 })
        {
            q.Where("location_ids && @subtree", ("subtree", f.LocationsSubtree.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        if (f.Servers is { Count: > 0 })
        {
            q.Where("server_id = ANY(@servers)", ("servers", f.Servers.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        if (f.Entities is { Count: > 0 })
        {
            q.Where("entity_id = ANY(@entities)", ("entities", f.Entities.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        if (f.TagKeys is { Count: > 0 })
        {
            q.Where("tag_keys && @tags", ("tags", f.TagKeys.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        if (f.PersonRef is not null)
        {
            q.Where("person_refs @> @personRef", ("personRef", new[] { f.PersonRef }, NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        if (f.OperatorUser is { } user)
        {
            q.Where("origin_type = 1 AND origin_id = @operator", ("operator", user, NpgsqlDbType.Uuid));
        }

        if (!string.IsNullOrEmpty(f.Text))
        {
            q.Where("(payload::text ILIKE @text OR title_args::text ILIKE @text)", ("text", "%" + EscapeLike(f.Text) + "%", NpgsqlDbType.Text));
        }

        q.Scope(f.Scope, "entity_id");
        q.Keyset(f.After, f.Descending);
        var sql = $"SELECT {SelectEvents} FROM wp_history.events{q.WhereSql} ORDER BY time {Direction(f.Descending)}, id {Direction(f.Descending)} LIMIT @limit";
        q.Param("limit", limit, NpgsqlDbType.Integer);
        return await ReadAsync(sql, q, timeout, ReadEvent, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryAlarmRow>> QueryAlarmsAsync(HistoryAlarmFilter f, int limit, TimeSpan timeout, CancellationToken ct)
    {
        var q = new QueryBuilder();
        q.Where("time >= @from AND time < @to", ("from", Utc(f.From), NpgsqlDbType.TimestampTz), ("to", Utc(f.To), NpgsqlDbType.TimestampTz));
        if (f.RuleIds is { Count: > 0 })
        {
            q.Where("rule_id = ANY(@ruleIds)", ("ruleIds", f.RuleIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        if (f.RuleKeys is { Count: > 0 })
        {
            q.Where("rule_key = ANY(@ruleKeys)", ("ruleKeys", f.RuleKeys.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        if (f.Sources is { Count: > 0 })
        {
            q.Where("source_id = ANY(@sources)", ("sources", f.Sources.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        if (f.States is { Count: > 0 })
        {
            q.Where("state = ANY(@states)", ("states", f.States.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Smallint));
        }

        if (f.MinPriority is { } priority)
        {
            q.Where("priority_final >= @minPriority", ("minPriority", priority, NpgsqlDbType.Integer));
        }

        if (f.FalseAlarm is { } falseAlarm)
        {
            q.Where("false_alarm IS NOT DISTINCT FROM @falseAlarm", ("falseAlarm", falseAlarm, NpgsqlDbType.Boolean));
        }

        if (f.LocationsSubtree is { Count: > 0 })
        {
            q.Where("location_ids && @subtree", ("subtree", f.LocationsSubtree.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        q.Scope(f.Scope, "source_id");
        q.Keyset(f.After, f.Descending);
        var sql = $"SELECT {SelectAlarms} FROM wp_history.alarms{q.WhereSql} ORDER BY time {Direction(f.Descending)}, id {Direction(f.Descending)} LIMIT @limit";
        q.Param("limit", limit, NpgsqlDbType.Integer);
        return await ReadAsync(sql, q, timeout, ReadAlarm, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryAlarmTimelineRow>> GetAlarmTimelineAsync(Guid alarmId, TimeSpan timeout, CancellationToken ct)
    {
        var q = new QueryBuilder();
        q.Param("alarm", alarmId, NpgsqlDbType.Uuid);
        return await ReadAsync(
            "SELECT time, alarm_id, record_id, kind, actor_user, actor_session, client_kind, body::text FROM wp_history.alarm_timeline WHERE alarm_id = @alarm ORDER BY time, record_id",
            q,
            timeout,
            r => new HistoryAlarmTimelineRow(
                Time(r, 0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), Nullable<Guid>(r, 4), Nullable<Guid>(r, 5), Nullable<short>(r, 6), r.GetString(7)),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryCommandRow>> QueryCommandsAsync(HistoryCommandFilter f, int limit, TimeSpan timeout, CancellationToken ct)
    {
        var q = new QueryBuilder();
        q.Where("time >= @from AND time < @to", ("from", Utc(f.From), NpgsqlDbType.TimestampTz), ("to", Utc(f.To), NpgsqlDbType.TimestampTz));
        if (f.Devices is { Count: > 0 })
        {
            q.Where("device_id = ANY(@devices)", ("devices", f.Devices.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        if (f.IssuerUser is { } user)
        {
            q.Where("issuer_user = @issuer", ("issuer", user, NpgsqlDbType.Uuid));
        }

        if (f.States is { Count: > 0 })
        {
            q.Where("state = ANY(@states)", ("states", f.States.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Smallint));
        }

        if (f.Path is not null)
        {
            q.Where("path = @path", ("path", f.Path, NpgsqlDbType.Text));
        }

        if (f.MinRisk is { } risk)
        {
            q.Where("risk >= @minRisk", ("minRisk", risk, NpgsqlDbType.Smallint));
        }

        if (f.LocationsSubtree is { Count: > 0 })
        {
            q.Where("location_ids && @subtree", ("subtree", f.LocationsSubtree.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
        }

        q.Scope(f.Scope, "device_id");
        q.Keyset(f.After, f.Descending);
        var sql = $"SELECT {SelectCommands} FROM wp_history.commands{q.WhereSql} ORDER BY time {Direction(f.Descending)}, id {Direction(f.Descending)} LIMIT @limit";
        q.Param("limit", limit, NpgsqlDbType.Integer);
        return await ReadAsync(sql, q, timeout, ReadCommand, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistorySeriesRow>> QuerySeriesAsync(HistorySeriesRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var q = new QueryBuilder();
        q.Param("device", request.DeviceId, NpgsqlDbType.Uuid);
        q.Param("path", request.Path, NpgsqlDbType.Text);
        q.Param("from", Utc(request.From), NpgsqlDbType.TimestampTz);
        q.Param("to", Utc(request.To), NpgsqlDbType.TimestampTz);
        q.Param("limit", request.Limit, NpgsqlDbType.Integer);
        const string filter = "WHERE device_id = @device AND path = @path AND time >= @from AND time < @to";
        if (request.Bucket is not { } bucket)
        {
            return await ReadAsync(
                $"SELECT time, v_num, v_text, v_bool, v_json::text FROM wp_history.property_changes {filter} ORDER BY time, record_id LIMIT @limit",
                q,
                timeout,
                r => new HistorySeriesRow(Time(r, 0), Nullable<decimal>(r, 1), r.IsDBNull(2) ? null : r.GetString(2), Nullable<bool>(r, 3), r.IsDBNull(4) ? null : r.GetString(4), null, null, null, 1),
                ct).ConfigureAwait(false);
        }

        q.Param("bucket", bucket, NpgsqlDbType.Interval);
        q.Param("origin", _binOrigin, NpgsqlDbType.TimestampTz);
        return await ReadAsync(
            $"SELECT date_bin(@bucket, time, @origin) AS b, min(v_num), avg(v_num), max(v_num), count(*) FROM wp_history.property_changes {filter} GROUP BY 1 ORDER BY 1 LIMIT @limit",
            q,
            timeout,
            r => new HistorySeriesRow(Time(r, 0), null, null, null, null, Nullable<decimal>(r, 1), Nullable<decimal>(r, 2), Nullable<decimal>(r, 3), checked((int)r.GetInt64(4))),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoryBucketRow>> CountAsync(HistoryCountRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (!HistoryCountColumns.TryGetColumns(request.Table, out var columns))
        {
            throw new ArgumentException($"Unknown history table '{request.Table}'.", nameof(request));
        }

        foreach (var column in request.GroupBy.Concat(request.Filters.Select(f => f.Column)))
        {
            if (!columns.ContainsKey(column))
            {
                throw new ArgumentException($"Column '{column}' is not whitelisted for {request.Table}.", nameof(request));
            }
        }

        var q = new QueryBuilder();
        q.Where("time >= @from AND time < @to", ("from", Utc(request.From), NpgsqlDbType.TimestampTz), ("to", Utc(request.To), NpgsqlDbType.TimestampTz));
        var n = 0;
        foreach (var filter in request.Filters)
        {
            var name = "f" + (n++).ToString(CultureInfo.InvariantCulture);
            q.Where($"{filter.Column}::text = ANY(@{name})", (name, filter.Values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        q.Scope(request.Scope, request.Table switch { "alarms" => "source_id", "commands" => "device_id", _ => "entity_id" });
        q.Param("bucket", request.Bucket, NpgsqlDbType.Interval);
        q.Param("origin", _binOrigin, NpgsqlDbType.TimestampTz);
        var groups = request.GroupBy.Distinct(StringComparer.Ordinal).ToList();
        var select = new StringBuilder("date_bin(@bucket, time, @origin) AS bucket");
        foreach (var g in groups)
        {
            select.Append(", ").Append(g).Append("::text AS ").Append(g);
        }

        select.Append(", count(*) AS n");
        var groupBy = new StringBuilder("1");
        for (var i = 0; i < groups.Count; i++)
        {
            groupBy.Append(", ").Append((i + 2).ToString(CultureInfo.InvariantCulture));
        }

        return await ReadAsync(
            $"SELECT {select} FROM wp_history.{request.Table}{q.WhereSql} GROUP BY {groupBy} ORDER BY {groupBy}",
            q,
            timeout,
            r =>
            {
                var group = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < groups.Count; i++)
                {
                    group[groups[i]] = r.IsDBNull(i + 1) ? null : r.GetString(i + 1);
                }

                return new HistoryBucketRow(Time(r, 0), group, r.GetInt64(groups.Count + 1));
            },
            ct).ConfigureAwait(false);
    }

    private const string SelectEvents =
        "time, id, seq, kind, category, severity, source_time, entity_id, entity_kind, server_id, location_id, location_ids, type_id, tag_keys, " +
        "origin_type, origin_id, payload::text, personal::text, person_refs, title_key, title_args::text, source_name, location_names, correlation::text";

    private static readonly string SelectAlarms = AlarmColumns.Replace("\"owner\"", "\"owner\"::text", StringComparison.Ordinal);

    private const string SelectCommands =
        "time, id, seq, group_id, device_id, device_name, location_ids, path, arguments::text, issuer::text, issuer_user, reason, risk, priority, state, failure::text, " +
        "queued_at, dispatched_at, completed_at, verified_at, attempt, approval::text, correlation::text";

    private static DateTime Utc(DateTimeOffset value) => value.UtcDateTime;

    private static string Direction(bool descending) => descending ? "DESC" : "ASC";

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static DateTimeOffset Time(DbDataReader r, int i) => new(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc), TimeSpan.Zero);

    private static DateTimeOffset? NullableTime(DbDataReader r, int i) => r.IsDBNull(i) ? null : Time(r, i);

    private static T? Nullable<T>(DbDataReader r, int i)
        where T : struct => r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);

    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static Guid[]? GuidArray(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<Guid[]>(i);

    private static string[]? TextArray(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<string[]>(i);

    private static HistoryPropertyRow ReadProperty(DbDataReader r) => new(
        Time(r, 0), r.GetGuid(1), r.GetString(2), Nullable<decimal>(r, 3), Text(r, 4), Nullable<bool>(r, 5), Text(r, 6), r.GetInt16(7), NullableTime(r, 8), r.GetGuid(9));

    private static HistoryEventRow ReadEvent(DbDataReader r) => new(
        Time(r, 0), r.GetGuid(1), r.GetInt64(2), r.GetString(3), r.GetInt16(4), r.GetInt16(5), NullableTime(r, 6), Nullable<Guid>(r, 7), Nullable<short>(r, 8),
        Nullable<Guid>(r, 9), Nullable<Guid>(r, 10), GuidArray(r, 11), Text(r, 12), TextArray(r, 13), r.GetInt16(14), Nullable<Guid>(r, 15), r.GetString(16),
        Text(r, 17), TextArray(r, 18), r.GetString(19), r.GetString(20), Text(r, 21), TextArray(r, 22), Text(r, 23));

    private static HistoryAlarmRow ReadAlarm(DbDataReader r) => new(
        Time(r, 0), r.GetGuid(1), r.GetInt64(2), r.GetGuid(3), r.GetString(4), r.GetInt64(5), Nullable<Guid>(r, 6), Nullable<short>(r, 7), Text(r, 8), Nullable<Guid>(r, 9),
        GuidArray(r, 10), Text(r, 11), TextArray(r, 12), r.GetInt16(13), r.GetInt32(14), r.GetInt32(15), r.GetInt32(16), Time(r, 17), NullableTime(r, 18),
        Nullable<Guid>(r, 19), NullableTime(r, 20), NullableTime(r, 21), Nullable<Guid>(r, 22), Text(r, 23), Text(r, 24), r.GetInt32(25), Nullable<Guid>(r, 26),
        Nullable<long>(r, 27), Nullable<bool>(r, 28));

    private static HistoryCommandRow ReadCommand(DbDataReader r) => new(
        Time(r, 0), r.GetGuid(1), r.GetInt64(2), Nullable<Guid>(r, 3), r.GetGuid(4), Text(r, 5), GuidArray(r, 6), r.GetString(7), r.GetString(8), r.GetString(9),
        Nullable<Guid>(r, 10), Text(r, 11), r.GetInt16(12), r.GetInt16(13), r.GetInt16(14), Text(r, 15), NullableTime(r, 16), NullableTime(r, 17), NullableTime(r, 18),
        NullableTime(r, 19), r.GetInt32(20), Text(r, 21), Text(r, 22));

    private async Task<IReadOnlyList<T>> ReadAsync<T>(string sql, QueryBuilder q, TimeSpan timeout, Func<DbDataReader, T> map, CancellationToken ct)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var set = new NpgsqlCommand("SELECT set_config('statement_timeout', @ms, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("ms", ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            q.AddTo(command);
            var result = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(map(reader));
            }

            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == QueryTimedOut)
        {
            throw new HistoryQueryTimeoutException(ex);
        }
    }

    /// <summary>Collects the conditions and parameters of one query; names are chosen by the caller, values are never concatenated into the SQL.</summary>
    private sealed class QueryBuilder
    {
        private readonly List<string> _conditions = [];
        private readonly List<NpgsqlParameter> _parameters = [];

        public string WhereSql => _conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", _conditions);

        public void Where(string condition, params (string Name, object Value, NpgsqlDbType Type)[] parameters)
        {
            _conditions.Add(condition);
            foreach (var (name, value, type) in parameters)
            {
                Param(name, value, type);
            }
        }

        public void Param(string name, object value, NpgsqlDbType type) => _parameters.Add(new NpgsqlParameter(name, type) { Value = value });

        public void Scope(HistoryScopeClause scope, string entityColumn)
        {
            if (scope.LocationIds is not null)
            {
                Where("location_ids && @scopeLocations", ("scopeLocations", scope.LocationIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
            }
            else if (scope.EntityIds is not null)
            {
                Where($"{entityColumn} = ANY(@scopeEntities)", ("scopeEntities", scope.EntityIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
            }
        }

        public void Keyset(HistoryKeyset? after, bool descending)
        {
            if (after is null)
            {
                return;
            }

            Where($"(time, id) {(descending ? "<" : ">")} (@afterTime, @afterId)", ("afterTime", after.Time.UtcDateTime, NpgsqlDbType.TimestampTz), ("afterId", after.Id, NpgsqlDbType.Uuid));
        }

        public void AddTo(NpgsqlCommand command)
        {
            foreach (var p in _parameters)
            {
                command.Parameters.Add(p);
            }
        }
    }
}
