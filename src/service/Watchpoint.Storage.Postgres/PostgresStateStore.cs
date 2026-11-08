using System.Runtime.CompilerServices;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres;

/// <summary>State snapshots in <c>wp_state.items</c> and <c>wp_state.checkpoints</c> (spec STO §7.3).</summary>
internal sealed class PostgresStateStore(NpgsqlDataSource source) : IStateStore
{
    private const string UpsertSql = """
        INSERT INTO wp_state.items (kind, key, partition, payload, sequence, updated_at)
        SELECT t.kind, t.key, t.partition, t.payload, t.sequence, now()
        FROM unnest(@kinds, @keys, @partitions, @payloads, @sequences) AS t(kind, key, partition, payload, sequence)
        ON CONFLICT (kind, key) DO UPDATE
        SET partition = EXCLUDED.partition, payload = EXCLUDED.payload, sequence = EXCLUDED.sequence, updated_at = now()
        """;

    private const string DeleteSql = """
        DELETE FROM wp_state.items
        WHERE (kind, key) IN (SELECT t.kind, t.key FROM unnest(@kinds, @keys) AS t(kind, key))
        """;

    private const string CheckpointSql = """
        INSERT INTO wp_state.checkpoints (consumer, partition, sequence, written_at)
        VALUES (@consumer, @partition, @sequence, now())
        ON CONFLICT (consumer, partition) DO UPDATE SET sequence = EXCLUDED.sequence, written_at = now()
        """;

    /// <summary>Test hook (STO-I-010): runs after the writes, before the commit.</summary>
    internal Func<CancellationToken, Task>? BeforeCommit { get; set; }

    public async Task WriteSnapshotAsync(StateSnapshotBatch batch, CancellationToken ct)
    {
        StateStoreRules.Validate(batch);

        // The last change of a key wins inside a batch (ON CONFLICT cannot touch a row twice in one statement).
        var upserts = batch.Upserts.GroupBy(i => (i.Kind, i.Key)).Select(g => g.Last()).ToList();
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (upserts.Count > 0)
        {
            await using var command = new NpgsqlCommand(UpsertSql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter<string[]>("kinds", [.. upserts.Select(i => i.Kind)]));
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("keys", [.. upserts.Select(i => i.Key)]));
            command.Parameters.Add(new NpgsqlParameter<short[]>("partitions", [.. upserts.Select(i => checked((short)i.Partition))]));
            command.Parameters.Add(new NpgsqlParameter<string[]>("payloads", [.. upserts.Select(i => Encoding.UTF8.GetString(i.PayloadJson.Span))])
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb,
            });
            command.Parameters.Add(new NpgsqlParameter<long[]>("sequences", [.. upserts.Select(i => i.Sequence)]));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (batch.Deletes.Count > 0)
        {
            await using var command = new NpgsqlCommand(DeleteSql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter<string[]>("kinds", [.. batch.Deletes.Select(d => d.Kind)]));
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("keys", [.. batch.Deletes.Select(d => d.Key)]));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var command = new NpgsqlCommand(CheckpointSql, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<string>("consumer", batch.Consumer));
            command.Parameters.Add(new NpgsqlParameter<short>("partition", checked((short)batch.Partition)));
            command.Parameters.Add(new NpgsqlParameter<long>("sequence", batch.Sequence));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (BeforeCommit is { } hook)
        {
            await hook(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StateItem> ReadItemsAsync(string kind, IReadOnlyCollection<int> partitions,
        [EnumeratorCancellation] CancellationToken ct)
    {
        StateStoreRules.ValidateName(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(partitions);
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT kind, key, partition, payload::text, sequence FROM wp_state.items WHERE kind = @kind AND partition = ANY(@partitions)", connection);
        command.Parameters.Add(new NpgsqlParameter<string>("kind", kind));
        command.Parameters.Add(new NpgsqlParameter<short[]>("partitions", [.. partitions.Select(p => checked((short)p))]));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new StateItem(reader.GetString(0), reader.GetGuid(1), reader.GetInt16(2), Encoding.UTF8.GetBytes(reader.GetString(3)), reader.GetInt64(4));
        }
    }

    public async Task<IReadOnlyDictionary<int, long>> ReadCheckpointsAsync(string consumer, CancellationToken ct)
    {
        StateStoreRules.ValidateName(consumer, nameof(consumer));
        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT partition, sequence FROM wp_state.checkpoints WHERE consumer = @consumer", connection);
        command.Parameters.Add(new NpgsqlParameter<string>("consumer", consumer));
        var checkpoints = new Dictionary<int, long>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            checkpoints[reader.GetInt16(0)] = reader.GetInt64(1);
        }

        return checkpoints;
    }
}
