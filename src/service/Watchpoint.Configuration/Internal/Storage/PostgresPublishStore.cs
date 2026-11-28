using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Publish;
using Watchpoint.Kernel.Ids;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Configuration.Internal.Storage;

/// <summary>
/// The versions in <c>wp_config</c> and the publish transaction on the configuration database (spec CFG §7.1): the
/// publish lock is <c>pg_advisory_xact_lock</c>, released with the transaction; modules write in the same transaction.
/// </summary>
internal sealed class PostgresPublishStore(IStoreConnectionFactory connections, IDbContextFactory<ConfigDbContext> factory) : IPublishStore
{
    /// <summary>The advisory lock key of the publish lock ("wp_config.publish").</summary>
    public const long PublishLockKey = 0x77705F636F6E6667;

    private (string Versions, string Entries)? _tables;

    public async Task<IPublishTransaction> BeginAsync(CancellationToken ct)
    {
        var tables = await TablesAsync(ct).ConfigureAwait(false);
        var connection = await connections.OpenConnectionAsync(StoreKind.Configuration, ct).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            await using (var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", PublishLockKey)))
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return new PublishTransaction(connection, transaction, tables.Versions, tables.Entries);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<VersionRecord?> GetAsync(long version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Versions.AsNoTracking().FirstOrDefaultAsync(v => v.Version == version, ct).ConfigureAwait(false) is { } row
            ? await RecordAsync(db, row, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<ConfigVersion>> ListAsync(VersionQuery query, int skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var versions = db.Versions.AsNoTracking();
        if (query.Type is not null || query.ObjectId is not null)
        {
            var touched = db.VersionEntries.Where(e => (query.Type == null || e.Type == query.Type) && (query.ObjectId == null || e.ObjectId == query.ObjectId))
                .Select(e => e.Version);
            versions = versions.Where(v => touched.Contains(v.Version));
        }

        if (query.From is { } from)
        {
            versions = versions.Where(v => v.PublishedAt >= from);
        }

        if (query.To is { } to)
        {
            versions = versions.Where(v => v.PublishedAt < to);
        }

        var rows = await versions.OrderByDescending(v => v.Version).Skip(skip).Take(take).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(Version)];
    }

    public async Task<IReadOnlyList<VersionRecord>> AfterAsync(long version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Versions.AsNoTracking().Where(v => v.Version > version).OrderByDescending(v => v.Version).ToListAsync(ct).ConfigureAwait(false);
        var records = new List<VersionRecord>(rows.Count);
        foreach (var row in rows)
        {
            records.Add(await RecordAsync(db, row, ct).ConfigureAwait(false));
        }

        return records;
    }

    public async Task<AppliedEntry?> ObjectAtAsync(ConfigObjectRef obj, long version, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.VersionEntries.AsNoTracking()
            .Where(e => e.Type == obj.Type && e.ObjectId == obj.Id && e.Version <= version)
            .OrderByDescending(e => e.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } row ? Applied(row) : null;
    }

    public async Task<AppliedEntry?> RevisionAsync(ConfigObjectRef obj, long objectVersion, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.VersionEntries.AsNoTracking()
            .Where(e => e.Type == obj.Type && e.ObjectId == obj.Id && e.NewVersion == objectVersion && e.After != null)
            .OrderByDescending(e => e.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } row ? Applied(row) : null;
    }

    private static async Task<VersionRecord> RecordAsync(ConfigDbContext db, VersionRow row, CancellationToken ct)
    {
        var entries = await db.VersionEntries.AsNoTracking().Where(e => e.Version == row.Version).OrderBy(e => e.Position).ToListAsync(ct).ConfigureAwait(false);
        return new VersionRecord(Version(row), ChangeSetId.From(row.ChangeSetId), [.. entries.Select(Applied)],
            ConfigJson.Read<List<DraftEntry>>(row.Inverse), ConfigJson.Read<List<DiffEntry>>(row.Diff));
    }

    private static ConfigVersion Version(VersionRow row) =>
        new(row.Version, row.PublishedAt, UserId.From(row.Author), row.Comment, DraftId.From(row.DraftId), ConfigJson.Read<Dictionary<string, int>>(row.Summary));

    private static AppliedEntry Applied(VersionEntryRow row) =>
        new(new ConfigObjectRef(row.Type, row.ObjectId), Enum.Parse<EntryOperation>(row.Operation), row.NewVersion, ConfigJson.Document(row.Before), ConfigJson.Document(row.After));

    private async Task<(string Versions, string Entries)> TablesAsync(CancellationToken ct)
    {
        if (_tables is { } known)
        {
            return known;
        }

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        string Name(Type type)
        {
            var entity = db.Model.FindEntityType(type)!;
            return entity.GetSchema() is { } schema ? $"\"{schema}\".\"{entity.GetTableName()}\"" : $"\"{entity.GetTableName()}\"";
        }

        var tables = (Name(typeof(VersionRow)), Name(typeof(VersionEntryRow)));
        _tables = tables;
        return tables;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Only schema-derived table names (from the EF model) are interpolated into the SQL; every value is a parameter.
        command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private sealed class PublishTransaction(DbConnection connection, DbTransaction transaction, string versions, string entries) : IPublishTransaction
    {
        public DbConnection Connection => connection;

        public DbTransaction Transaction => transaction;

        public async Task<long> NextVersionAsync(CancellationToken ct)
        {
            // {versions} is the EF model's table name, not request input.
            await using var command = Command(connection, transaction, $"SELECT COALESCE(MAX(version), 0) + 1 FROM {versions}"); // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task WriteAsync(VersionRecord record, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(record);
            var v = record.Version;
            // {versions} / {entries}: the EF model's table names, not request input.
            await using (var command = Command(connection, transaction, // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
                $"INSERT INTO {versions} (version, published_at, author, comment, draft_id, change_set_id, summary, inverse, diff) " +
                "VALUES (@version, @at, @author, @comment, @draft, @changeSet, CAST(@summary AS jsonb), CAST(@inverse AS jsonb), CAST(@diff AS jsonb))",
                ("version", v.Version), ("at", v.PublishedAt), ("author", v.Author.Value), ("comment", v.Comment), ("draft", v.Draft.Value),
                ("changeSet", record.ChangeSet.Value), ("summary", ConfigJson.Write(v.Summary)), ("inverse", ConfigJson.Write(record.Inverse)),
                ("diff", ConfigJson.Write(record.Diff))))
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            for (var i = 0; i < record.Entries.Count; i++)
            {
                var e = record.Entries[i];
                await using var command = Command(connection, transaction, // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
                    $"INSERT INTO {entries} (version, position, type, object_id, operation, new_version, before, after) " +
                    "VALUES (@version, @position, @type, @id, @operation, @newVersion, CAST(@before AS jsonb), CAST(@after AS jsonb))",
                    ("version", v.Version), ("position", i), ("type", e.Ref.Type), ("id", e.Ref.Id), ("operation", e.Operation.ToString()),
                    ("newVersion", e.NewVersion), ("before", ConfigJson.Document(e.Before)), ("after", ConfigJson.Document(e.After)));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
