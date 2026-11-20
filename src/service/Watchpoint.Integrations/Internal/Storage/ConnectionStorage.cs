using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Documents;
using Watchpoint.Storage.Contracts;
using KernelMessage = Watchpoint.Kernel.Text.Message;

namespace Watchpoint.Integrations.Internal.Storage;

/// <summary>A row of <c>wp_integrations.connections</c> (spec IRT §6).</summary>
internal sealed class ConnectionRecord : IVersioned, ITimestamped
{
    public Guid Id { get; set; }

    public LocalizedName Name { get; set; } = LocalizedName.Of("-");

    public string PackageId { get; set; } = string.Empty;

    public string PackageVersion { get; set; } = string.Empty;

    public string ServerType { get; set; } = string.Empty;

    public string Settings { get; set; } = "{}";

    public string Secrets { get; set; } = "{}";

    public Guid LocationId { get; set; }

    public string HostMode { get; set; } = "inProcess";

    public Guid? HostId { get; set; }

    public bool Enabled { get; set; }

    public bool AutoImport { get; set; }

    public TimeSpan DescribeInterval { get; set; }

    public DateTimeOffset? ProtocolTraceUntil { get; set; }

    public long RowVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A row of <c>wp_integrations.connection_store</c>.</summary>
internal sealed class ConnectionStoreRecord
{
    public Guid ConnectionId { get; set; }

    public string Key { get; set; } = string.Empty;

    public byte[] Value { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A row of <c>wp_integrations.connection_events</c> (the state timeline, retained 30 days).</summary>
internal sealed class ConnectionEventRecord
{
    public long Id { get; set; }

    public Guid ConnectionId { get; set; }

    public DateTimeOffset At { get; set; }

    public string State { get; set; } = string.Empty;

    public string? Reason { get; set; }
}

[DbContext(typeof(IntegrationsPostgresDbContext))]
[Migration("20261120000000_Connections")]
internal sealed class ConnectionsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "connections",
            schema: "wp_integrations",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "jsonb", nullable: false),
                package_id = table.Column<string>(type: "text", nullable: false),
                package_version = table.Column<string>(type: "text", nullable: false),
                server_type = table.Column<string>(type: "text", nullable: false),
                settings = table.Column<string>(type: "jsonb", nullable: false),
                secrets = table.Column<string>(type: "jsonb", nullable: false),
                location_id = table.Column<Guid>(type: "uuid", nullable: false),
                host_mode = table.Column<string>(type: "text", nullable: false),
                host_id = table.Column<Guid>(type: "uuid", nullable: true),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                auto_import = table.Column<bool>(type: "boolean", nullable: false),
                describe_interval = table.Column<TimeSpan>(type: "interval", nullable: false),
                protocol_trace_until = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_connections", x => x.id));
        migrationBuilder.CreateIndex(
            name: "ix_connections_package_id_package_version",
            schema: "wp_integrations",
            table: "connections",
            columns: ["package_id", "package_version"]);

        migrationBuilder.CreateTable(
            name: "connection_store",
            schema: "wp_integrations",
            columns: table => new
            {
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "text", nullable: false),
                value = table.Column<byte[]>(type: "bytea", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_connection_store", x => new { x.connection_id, x.key });
                table.ForeignKey("fk_connection_store_connections_connection_id", x => x.connection_id,
                    principalSchema: "wp_integrations", principalTable: "connections", principalColumn: "id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "connection_events",
            schema: "wp_integrations",
            columns: table => new
            {
                // An identity column: two state changes can share a millisecond (the PK of IRT §6 was (connection_id, at)).
                id = table.Column<long>(type: "bigint generated by default as identity", nullable: false),
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                state = table.Column<string>(type: "text", nullable: false),
                reason = table.Column<string>(type: "jsonb", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_connection_events", x => x.id));
        migrationBuilder.CreateIndex(
            name: "ix_connection_events_connection_id_at",
            schema: "wp_integrations",
            table: "connection_events",
            columns: ["connection_id", "at"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "connection_events", schema: "wp_integrations");
        migrationBuilder.DropTable(name: "connection_store", schema: "wp_integrations");
        migrationBuilder.DropTable(name: "connections", schema: "wp_integrations");
    }
}

/// <summary>Persistence of connections and their timeline.</summary>
internal interface IConnectionRepository
{
    Task<IReadOnlyList<Connection>> ListAsync(CancellationToken ct);

    Task InsertAsync(Connection connection, CancellationToken ct);

    /// <summary>Replaces the row when its version is <paramref name="expectedVersion"/>; returns the stored connection (version + 1), or <see langword="null"/> on a version conflict.</summary>
    Task<Connection?> UpdateAsync(Connection connection, long expectedVersion, CancellationToken ct);

    Task DeleteAsync(ConnectionId id, CancellationToken ct);

    Task AppendEventAsync(ConnectionEvent connectionEvent, CancellationToken ct);

    Task<IReadOnlyList<ConnectionEvent>> ListEventsAsync(ConnectionId id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<int> PurgeEventsAsync(DateTimeOffset before, CancellationToken ct);
}

/// <summary>Persistence of the connection stores (<see cref="Integration.Abstractions.Context.IConnectionStore"/>).</summary>
internal interface IConnectionStoreRepository
{
    Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(ConnectionId id, CancellationToken ct);

    Task SetAsync(ConnectionId id, string key, byte[] value, CancellationToken ct);

    Task DeleteAsync(ConnectionId id, string key, CancellationToken ct);
}

/// <summary>JSON forms of the connection columns.</summary>
internal static class ConnectionColumns
{
    public static string HostModeText(HostMode mode) => mode switch
    {
        HostMode.InProcess => "inProcess",
        HostMode.Isolated => "isolated",
        _ => "remote",
    };

    public static HostMode ParseHostMode(string text) => text switch
    {
        "inProcess" => HostMode.InProcess,
        "isolated" => HostMode.Isolated,
        _ => HostMode.Remote,
    };

    public static string StateText(ConnectionState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());

    public static ConnectionState ParseState(string text) =>
        Enum.TryParse<ConnectionState>(text, ignoreCase: true, out var state) ? state : ConnectionState.Configured;

    public static string SecretsJson(IReadOnlyDictionary<string, SecretId> secrets)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (field, id) in secrets.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                writer.WriteString(field, id.Value.ToString("D"));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static Dictionary<string, SecretId> ParseSecrets(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => SecretId.From(Guid.Parse(p.Value.GetString()!)), StringComparer.Ordinal);
    }

    public static string? ReasonJson(KernelMessage? reason)
    {
        if (reason is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", reason.Key);
            writer.WriteStartObject("args");
            foreach (var (name, value) in reason.Args)
            {
                if (value is null)
                {
                    writer.WriteNull(name);
                }
                else
                {
                    writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static KernelMessage? ParseReason(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var args = root.TryGetProperty("args", out var a)
            ? a.EnumerateObject().Select(p => (p.Name, (object?)p.Value.GetString())).ToArray()
            : [];
        return KernelMessage.Of(root.GetProperty("key").GetString()!, args);
    }

    public static Connection ToConnection(ConnectionRecord r)
    {
        using var settings = JsonDocument.Parse(r.Settings);
        return new Connection(ConnectionId.From(r.Id), r.Name, Kernel.Ids.PackageId.FromTrusted(r.PackageId), SemVersion.Parse(r.PackageVersion).Value,
            r.ServerType, settings.RootElement.Clone(), ParseSecrets(r.Secrets), Kernel.Ids.LocationId.From(r.LocationId), ParseHostMode(r.HostMode),
            r.HostId is { } host ? Kernel.Ids.HostId.From(host) : null, r.Enabled, r.AutoImport, r.DescribeInterval, r.ProtocolTraceUntil, r.RowVersion);
    }

    public static void Fill(ConnectionRecord r, Connection c)
    {
        r.Name = c.Name;
        r.PackageId = c.PackageId.Value;
        r.PackageVersion = c.PackageVersion.ToString();
        r.ServerType = c.ServerType;
        r.Settings = c.Settings.GetRawText();
        r.Secrets = SecretsJson(c.Secrets);
        r.LocationId = c.LocationId.Value;
        r.HostMode = HostModeText(c.HostMode);
        r.HostId = c.HostId?.Value;
        r.Enabled = c.Enabled;
        r.AutoImport = c.AutoImport;
        r.DescribeInterval = c.DescribeInterval;
        r.ProtocolTraceUntil = c.ProtocolTraceUntil?.ToUniversalTime();
    }
}

internal sealed class EfConnectionRepository(IDbContextFactory<IntegrationsDbContext> factory) : IConnectionRepository
{
    public async Task<IReadOnlyList<Connection>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var records = await db.Connections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. records.Select(ConnectionColumns.ToConnection)];
    }

    public async Task InsertAsync(Connection connection, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = new ConnectionRecord { Id = connection.Id.Value, RowVersion = connection.Version };
        ConnectionColumns.Fill(record, connection);
        db.Connections.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Connection?> UpdateAsync(Connection connection, long expectedVersion, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.Connections.AsTracking().FirstOrDefaultAsync(c => c.Id == connection.Id.Value, ct).ConfigureAwait(false);
        if (record is null || record.RowVersion != expectedVersion)
        {
            return null;
        }

        ConnectionColumns.Fill(record, connection);
        try
        {
            // RowVersion is a concurrency token: a writer that committed meanwhile makes this update fail.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (StorageException)
        {
            return null;
        }

        return ConnectionColumns.ToConnection(record);
    }

    public async Task DeleteAsync(ConnectionId id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Connections.Where(c => c.Id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.ConnectionStore.Where(c => c.ConnectionId == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task AppendEventAsync(ConnectionEvent connectionEvent, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.ConnectionEvents.Add(new ConnectionEventRecord
        {
            ConnectionId = connectionEvent.ConnectionId.Value,
            At = connectionEvent.At.ToUniversalTime(),
            State = ConnectionColumns.StateText(connectionEvent.State),
            Reason = ConnectionColumns.ReasonJson(connectionEvent.Reason),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionEvent>> ListEventsAsync(ConnectionId id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var records = await db.ConnectionEvents.AsNoTracking()
            .Where(e => e.ConnectionId == id.Value && e.At >= fromUtc && e.At < toUtc)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. records.Select(r => new ConnectionEvent(id, r.At, ConnectionColumns.ParseState(r.State), ConnectionColumns.ParseReason(r.Reason)))];
    }

    public async Task<int> PurgeEventsAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var limit = before.ToUniversalTime();
        return await db.ConnectionEvents.Where(e => e.At < limit).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class EfConnectionStoreRepository(IDbContextFactory<IntegrationsDbContext> factory, TimeProvider time) : IConnectionStoreRepository
{
    public async Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(ConnectionId id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var records = await db.ConnectionStore.AsNoTracking().Where(s => s.ConnectionId == id.Value).ToListAsync(ct).ConfigureAwait(false);
        return records.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    public async Task SetAsync(ConnectionId id, string key, byte[] value, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.ConnectionStore.AsTracking().FirstOrDefaultAsync(s => s.ConnectionId == id.Value && s.Key == key, ct).ConfigureAwait(false);
        if (record is null)
        {
            record = new ConnectionStoreRecord { ConnectionId = id.Value, Key = key };
            db.ConnectionStore.Add(record);
        }

        record.Value = value;
        record.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(ConnectionId id, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ConnectionStore.Where(s => s.ConnectionId == id.Value && s.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>In-memory persistence (unit tests, and nodes without a database).</summary>
internal sealed class InMemoryConnectionRepository : IConnectionRepository, IConnectionStoreRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ConnectionId, Connection> _connections = [];
    private readonly List<ConnectionEvent> _events = [];
    private readonly Dictionary<(ConnectionId, string), byte[]> _store = [];

    public int StoreWrites { get; private set; }

    public Task<IReadOnlyList<Connection>> ListAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Connection>>([.. _connections.Values]);
        }
    }

    public Task InsertAsync(Connection connection, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_connections.TryAdd(connection.Id, connection))
            {
                throw new InvalidOperationException("Duplicate connection id.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<Connection?> UpdateAsync(Connection connection, long expectedVersion, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(connection.Id, out var stored) || stored.Version != expectedVersion)
            {
                return Task.FromResult<Connection?>(null);
            }

            var updated = connection with { Version = expectedVersion + 1 };
            _connections[connection.Id] = updated;
            return Task.FromResult<Connection?>(updated);
        }
    }

    public Task DeleteAsync(ConnectionId id, CancellationToken ct)
    {
        lock (_lock)
        {
            _connections.Remove(id);
            foreach (var key in _store.Keys.Where(k => k.Item1 == id).ToList())
            {
                _store.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task AppendEventAsync(ConnectionEvent connectionEvent, CancellationToken ct)
    {
        lock (_lock)
        {
            _events.Add(connectionEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectionEvent>> ListEventsAsync(ConnectionId id, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ConnectionEvent>>([.. _events.Where(e => e.ConnectionId == id && e.At >= from && e.At < to)]);
        }
    }

    public Task<int> PurgeEventsAsync(DateTimeOffset before, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult(_events.RemoveAll(e => e.At < before));
        }
    }

    public Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(ConnectionId id, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(_store.Where(s => s.Key.Item1 == id).ToDictionary(s => s.Key.Item2, s => s.Value, StringComparer.Ordinal));
        }
    }

    public Task SetAsync(ConnectionId id, string key, byte[] value, CancellationToken ct)
    {
        lock (_lock)
        {
            _store[(id, key)] = value;
            StoreWrites++;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(ConnectionId id, string key, CancellationToken ct)
    {
        lock (_lock)
        {
            _store.Remove((id, key));
        }

        return Task.CompletedTask;
    }
}
