using Microsoft.Extensions.Hosting;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Secrets.Internal;

internal sealed class SecretsStartup(MasterKeyAccessor keys, BootstrapSecretStore bootstrap, SiteCertificateService certificates) : ISecretsStartup
{
    public Task InitializeAsync(CancellationToken ct)
    {
        _ = keys.Ring;
        bootstrap.ImportLegacyNatsToken();
        certificates.Initialize();
        return Task.CompletedTask;
    }
}

/// <summary>Every hour: flush aggregated read audit records; every 12 hours: certificate renewal check.</summary>
internal sealed class SecretsMaintenanceService(SecretVault vault, SiteCertificateService certificates, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                vault.FlushReadAudit(all: false);
                if (++ticks % 12 == 0)
                {
                    certificates.CheckRenewal();
                }
            }
        }
        finally
        {
            vault.FlushReadAudit(all: true);
        }
    }
}

/// <summary>Database passwords for the storage module: <c>PasswordDev</c> (Development) or a <c>bootstrap:</c> secret.</summary>
internal sealed class BootstrapDatabasePasswordProvider(IBootstrapSecretStore bootstrap) : IDatabasePasswordProvider
{
    public ValueTask<string?> GetPasswordAsync(DatabaseDefinition db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.PasswordDev is { } dev)
        {
            return ValueTask.FromResult<string?>(dev);
        }

        if (db.PasswordRef is { } text && SecretRef.Parse(text).TryGetValue(out var reference, out _) && reference.IsBootstrap
            && bootstrap.Read(reference.BootstrapName).TryGetValue(out var value, out _))
        {
            using (value)
            {
                return ValueTask.FromResult<string?>(value.UseAsString(p => p));
            }
        }

        return ValueTask.FromResult<string?>(null);
    }
}

/// <summary>Source of environment variables (replaceable in tests).</summary>
internal sealed record MasterKeyAccessorEnvironment(Func<string, string?> Read);
