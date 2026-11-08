using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Secrets.Internal;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;

namespace Watchpoint.Secrets.Tests;

/// <summary>A secrets module on a temporary data folder and an in-memory SQLite database.</summary>
internal sealed class SecretsHarness : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly ILoggerFactory _loggers;

    public SecretsHarness(Dictionary<string, string>? environment = null)
    {
        Options = new SecretsOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "wp-sec-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))),
            NodeId = "node-1",
            SiteName = "Test site",
            MasterKeyProvider = environment is null ? FileMasterKeyProvider.ProviderName : EnvironmentMasterKeyProvider.ProviderName,
        };
        Environment = environment ?? [];
        _loggers = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Keys = new MasterKeyAccessor(Options, name => Environment.GetValueOrDefault(name), _loggers.CreateLogger<MasterKeyAccessor>());
        Metrics = new SecretsMetrics(_meters.GetRequiredService<IMeterFactory>());
        Bootstrap = new BootstrapSecretStore(Options, Keys, _loggers.CreateLogger<BootstrapSecretStore>());
        _connection.Open();
    }

    public SecretsOptions Options { get; }

    public Dictionary<string, string> Environment { get; }

    public TestClock Clock { get; } = new();

    public LogCollector Logs { get; } = new();

    public RecordingAuditSink Audit { get; } = new();

    public MasterKeyAccessor Keys { get; }

    public SecretsMetrics Metrics { get; }

    public BootstrapSecretStore Bootstrap { get; }

    public SecretVault CreateVault()
    {
        var factory = new SqliteFactory(_connection, Clock);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        return new SecretVault(factory, Keys, Options, Audit, Metrics, Clock, _loggers.CreateLogger<SecretVault>());
    }

    public SiteCertificateService CreateCertificates() =>
        new(Options, Bootstrap, Metrics, Clock, _loggers.CreateLogger<SiteCertificateService>());

    public ILogger Logger(string category) => _loggers.CreateLogger(category);

    public void Dispose()
    {
        _connection.Dispose();
        _meters.Dispose();
        _loggers.Dispose();
        Logs.Dispose();
        if (Directory.Exists(Options.DataDirectory))
        {
            Directory.Delete(Options.DataDirectory, recursive: true);
        }
    }

    private sealed class SqliteFactory(SqliteConnection connection, TimeProvider clock) : IDbContextFactory<SecretsDbContext>
    {
        public SecretsDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder().UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options,
                new StorageContextInfo(SecretsDbContext.ModuleName, StorageNaming.TablePrefixes, clock));
    }
}

internal sealed class RecordingAuditSink : ISecretsAuditSink
{
    public List<SecretAuditRecord> Records { get; } = [];

    public void Record(SecretAuditRecord record) => Records.Add(record);
}
