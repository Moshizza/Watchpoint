using System.Collections.Concurrent;
using Npgsql;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres;

/// <summary>One <see cref="NpgsqlDataSource"/> per database definition (spec STO §5.1).</summary>
internal sealed class PostgresDataSources(IDatabasePasswordProvider passwords) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _sources = new(StringComparer.Ordinal);

    public NpgsqlDataSource Get(DatabaseDefinition db) =>
        _sources.GetOrAdd(db.Key, _ => new Lazy<NpgsqlDataSource>(() => Build(db))).Value;

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values.Where(s => s.IsValueCreated))
        {
            await source.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static NpgsqlConnectionStringBuilder ConnectionString(DatabaseDefinition db)
    {
        var hosts = new List<string> { db.Host.Contains(':', StringComparison.Ordinal) && !db.Host.StartsWith('[') ? $"[{db.Host}]" : db.Host };
        hosts.AddRange(db.AdditionalHosts);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = string.Join(',', hosts),
            Port = db.Port,
            Database = db.Database,
            Username = db.Username,
            ApplicationName = "watchpoint",
            Pooling = true,
            MaxPoolSize = db.MaxPoolSize,
            Timeout = 15,
            CommandTimeout = 30,
            KeepAlive = 30,
            SslMode = db.Tls switch
            {
                TlsMode.Disable => SslMode.Disable,
                TlsMode.Prefer => SslMode.Prefer,
                TlsMode.Require => SslMode.Require,
                TlsMode.VerifyCa => SslMode.VerifyCA,
                TlsMode.VerifyFull => SslMode.VerifyFull,
                _ => throw new ArgumentOutOfRangeException(nameof(db)),
            },
        };
        if (db.AdditionalHosts.Count > 0)
        {
            builder.TargetSessionAttributes = "primary";
        }

        if (!string.IsNullOrWhiteSpace(db.ExtraOptions))
        {
            // Extra options are applied last: they may override the defaults above (e.g. Timeout for tests).
            var extra = new NpgsqlConnectionStringBuilder(db.ExtraOptions);
            foreach (var key in extra.Keys)
            {
                if (!string.Equals(key, "Password", StringComparison.OrdinalIgnoreCase))
                {
                    builder[key] = extra[key];
                }
            }
        }

        return builder;
    }

    private NpgsqlDataSource Build(DatabaseDefinition db)
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString(db).ConnectionString) { Name = $"watchpoint:{db.Key}" };
        builder.UsePasswordProvider(
            _ => throw new NotSupportedException("Watchpoint opens database connections asynchronously only."),
            async (_, ct) => await passwords.GetPasswordAsync(db, ct).ConfigureAwait(false) ?? string.Empty);
        return builder.Build();
    }
}
