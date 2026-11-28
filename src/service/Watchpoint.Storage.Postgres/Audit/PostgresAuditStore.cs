using System.Text;
using Npgsql;
using NpgsqlTypes;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres.Audit;

/// <summary>The audit store on PostgreSQL (spec AUD §8): append-only records written with binary COPY in one transaction.</summary>
internal sealed class PostgresAuditStore(NpgsqlDataSource source) : IAuditStore
{
    private const string Columns =
        "id, chain_id, sequence, occurred_at, recorded_at, action, outcome, severity, actor::text, actor_id, objects::text, object_ids, reason, details::text, correlation_id, prev_hash, hash";

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('wp_audit_schema'))", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var command = new NpgsqlCommand(PostgresProvider.EmbeddedSql("audit-schema.sql"), connection, transaction))
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<AuditChainHead?> GetHeadAsync(string chainId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT sequence, hash FROM wp_audit.records WHERE chain_id = $1 ORDER BY sequence DESC LIMIT 1");
        command.Parameters.Add(new NpgsqlParameter { Value = chainId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new AuditChainHead(reader.GetInt64(0), (byte[])reader[1]) : null;
    }

    public async Task AppendAsync(IReadOnlyList<AuditRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var importer = await connection.BeginBinaryImportAsync(
            "COPY wp_audit.records (chain_id, sequence, occurred_at, recorded_at, action, outcome, severity, actor, actor_id, objects, object_ids, reason, details, correlation_id, prev_hash, hash) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
        {
            foreach (var row in rows)
            {
                await importer.StartRowAsync(ct).ConfigureAwait(false);
                await importer.WriteAsync(row.ChainId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.Sequence, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.OccurredAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.RecordedAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.Action, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.Outcome, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.Severity, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.ActorJson, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
                await WriteNullableAsync(importer, row.ActorId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.ObjectsJson, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.ObjectIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                await WriteNullableAsync(importer, row.Reason, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await WriteNullableAsync(importer, row.DetailsJson, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
                await WriteNullableAsync(importer, row.CorrelationId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.PrevHash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
                await importer.WriteAsync(row.Hash, NpgsqlDbType.Bytea, ct).ConfigureAwait(false);
            }

            await importer.CompleteAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditRow>> ReadChainAsync(string chainId, long fromSequence, long? toSequence, int limit, CancellationToken ct)
    {
        await using var command = source.CreateCommand(
            $"SELECT {Columns} FROM wp_audit.records WHERE chain_id = $1 AND sequence >= $2 AND ($3::bigint IS NULL OR sequence <= $3) ORDER BY sequence LIMIT $4");
        command.Parameters.Add(new NpgsqlParameter { Value = chainId });
        command.Parameters.Add(new NpgsqlParameter { Value = fromSequence });
        command.Parameters.Add(new NpgsqlParameter<long?> { TypedValue = toSequence, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        return await ReadRowsAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListChainsAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT chain_id FROM wp_audit.records UNION SELECT chain_id FROM wp_audit.checkpoints ORDER BY 1");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<string> chains = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            chains.Add(reader.GetString(0));
        }

        return chains;
    }

    public async Task<IReadOnlyList<AuditRow>> QueryAsync(AuditRowFilter filter, AuditCursorPosition? after, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var sql = new StringBuilder($"SELECT {Columns} FROM wp_audit.records WHERE occurred_at >= @from AND occurred_at < @to");
        await using var command = source.CreateCommand(string.Empty);
        command.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, filter.From.UtcDateTime);
        command.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, filter.To.UtcDateTime);
        if (filter.Actors is { Count: > 0 })
        {
            sql.Append(" AND actor_id = ANY(@actors)");
            command.Parameters.AddWithValue("actors", NpgsqlDbType.Array | NpgsqlDbType.Uuid, filter.Actors.ToArray());
        }

        if (filter.ActionPrefixes is { Count: > 0 })
        {
            sql.Append(" AND action LIKE ANY(@prefixes)");
            command.Parameters.AddWithValue("prefixes", NpgsqlDbType.Array | NpgsqlDbType.Text, filter.ActionPrefixes.Select(p => EscapeLike(p) + "%").ToArray());
        }

        if (filter.ObjectType is not null || filter.ObjectId is not null)
        {
            var element = new StringBuilder("{");
            if (filter.ObjectType is not null)
            {
                element.Append("\"type\":").Append(System.Text.Json.JsonSerializer.Serialize(filter.ObjectType));
            }

            if (filter.ObjectId is { } objectId)
            {
                element.Append(filter.ObjectType is null ? string.Empty : ",").Append("\"id\":\"").Append(objectId.ToString("D")).Append('"');
            }

            element.Append('}');
            sql.Append(" AND objects @> @object::jsonb");
            command.Parameters.AddWithValue("object", NpgsqlDbType.Text, "[" + element + "]");
        }

        if (filter.Outcome is { } outcome)
        {
            sql.Append(" AND outcome = @outcome");
            command.Parameters.AddWithValue("outcome", NpgsqlDbType.Smallint, outcome);
        }

        if (filter.MinSeverity is { } severity)
        {
            sql.Append(" AND severity >= @severity");
            command.Parameters.AddWithValue("severity", NpgsqlDbType.Smallint, severity);
        }

        if (filter.CorrelationId is { } correlation)
        {
            sql.Append(" AND correlation_id = @correlation");
            command.Parameters.AddWithValue("correlation", NpgsqlDbType.Uuid, correlation);
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            sql.Append(" AND (reason ILIKE @text OR objects::text ILIKE @text)");
            command.Parameters.AddWithValue("text", NpgsqlDbType.Text, "%" + EscapeLike(filter.Text) + "%");
        }

        var comparison = filter.Descending ? "<" : ">";
        if (after is not null)
        {
            sql.Append(System.Globalization.CultureInfo.InvariantCulture, $" AND (occurred_at, id) {comparison} (@afterAt, @afterId)");
            command.Parameters.AddWithValue("afterAt", NpgsqlDbType.TimestampTz, after.OccurredAt.UtcDateTime);
            command.Parameters.AddWithValue("afterId", NpgsqlDbType.Bigint, after.Id);
        }

        var direction = filter.Descending ? "DESC" : "ASC";
        sql.Append(System.Globalization.CultureInfo.InvariantCulture, $" ORDER BY occurred_at {direction}, id {direction} LIMIT @limit");
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        command.CommandText = sql.ToString();
        return await ReadRowsAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<AuditRow?> GetAsync(long id, CancellationToken ct)
    {
        await using var command = source.CreateCommand($"SELECT {Columns} FROM wp_audit.records WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var rows = await ReadRowsAsync(command, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task AddCheckpointAsync(AuditCheckpointRow checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var command = source.CreateCommand(
            "INSERT INTO wp_audit.checkpoints (chain_id, sequence, hash, at, signature, key_id) VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING");
        command.Parameters.Add(new NpgsqlParameter { Value = checkpoint.ChainId });
        command.Parameters.Add(new NpgsqlParameter { Value = checkpoint.Sequence });
        command.Parameters.Add(new NpgsqlParameter { Value = checkpoint.Hash });
        command.Parameters.Add(new NpgsqlParameter { Value = checkpoint.At.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = checkpoint.Signature });
        command.Parameters.Add(new NpgsqlParameter { Value = checkpoint.KeyId });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditCheckpointRow>> GetCheckpointsAsync(string chainId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT chain_id, sequence, hash, at, signature, key_id FROM wp_audit.checkpoints WHERE chain_id = $1 ORDER BY sequence");
        command.Parameters.Add(new NpgsqlParameter { Value = chainId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<AuditCheckpointRow> result = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AuditCheckpointRow(reader.GetString(0), reader.GetInt64(1), (byte[])reader[2], reader.GetFieldValue<DateTimeOffset>(3), (byte[])reader[4], reader.GetString(5)));
        }

        return result;
    }

    public async Task<AuditVerificationState?> GetVerificationStateAsync(string chainId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("SELECT chain_id, last_verified_sequence, last_full_verification_at, last_result::text FROM wp_audit.verification_state WHERE chain_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = chainId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new AuditVerificationState(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task SaveVerificationStateAsync(AuditVerificationState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var command = source.CreateCommand(
            "INSERT INTO wp_audit.verification_state (chain_id, last_verified_sequence, last_full_verification_at, last_result) VALUES ($1, $2, $3, $4::jsonb) " +
            "ON CONFLICT (chain_id) DO UPDATE SET last_verified_sequence = EXCLUDED.last_verified_sequence, last_full_verification_at = EXCLUDED.last_full_verification_at, last_result = EXCLUDED.last_result");
        command.Parameters.Add(new NpgsqlParameter { Value = state.ChainId });
        command.Parameters.Add(new NpgsqlParameter { Value = state.LastVerifiedSequence });
        command.Parameters.Add(new NpgsqlParameter { Value = state.LastFullVerificationAt?.UtcDateTime ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = state.LastResultJson ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> PurgeUpToAsync(string chainId, long sequence, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE wp_audit_maint", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long deleted;
        await using (var command = new NpgsqlCommand("DELETE FROM wp_audit.records WHERE chain_id = $1 AND sequence <= $2", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = chainId });
            command.Parameters.Add(new NpgsqlParameter { Value = sequence });
            deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter importer, T? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is null)
        {
            await importer.WriteNullAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await importer.WriteAsync(value, type, ct).ConfigureAwait(false);
        }
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<IReadOnlyList<AuditRow>> ReadRowsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<AuditRow> rows = [];
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AuditRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                reader.GetInt16(6),
                reader.GetInt16(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.GetString(10),
                reader.GetFieldValue<Guid[]>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                (byte[])reader[15],
                (byte[])reader[16]));
        }

        return rows;
    }
}
