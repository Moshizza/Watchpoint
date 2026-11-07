using Microsoft.Extensions.Logging;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

internal sealed class StorageStartup(StoreMapHolder holder, MigrationRunner migrations, TimeProvider clock, ILogger<StorageStartup> logger)
    : IStorageStartup
{
    private static readonly TimeSpan[] _backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    public async Task WaitForDatabasesAsync(CancellationToken ct)
    {
        var map = holder.Map;
        holder.LogWarnings(logger);
        migrations.ValidateRegistrations();
        var deadline = clock.GetUtcNow() + map.StartupWait;
        foreach (var db in map.UsedDatabases)
        {
            var provider = map.ProviderOf(db);
            for (var attempt = 0; ; attempt++)
            {
                var probe = await provider.ProbeAsync(db, ct).ConfigureAwait(false);
                if (probe.TryGetValue(out var info, out var error))
                {
                    StorageLog.DatabaseReachable(logger, db.Key, db.ProviderId, info.ServerVersion);
                    if (!provider.CapabilitiesFor(info).TimeSeriesExtension)
                    {
                        StorageLog.TimescaleMissing(logger, db.Key);
                    }

                    break;
                }

                if (error.Code.Value != StorageErrors.UnavailableCode)
                {
                    throw new StorageStartupException(error, StorageStartupException.Software);
                }

                var remaining = deadline - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new StorageStartupException(StorageErrors.Unavailable(db.Key), StorageStartupException.TempFail);
                }

                var delay = _backoff[Math.Min(attempt, _backoff.Length - 1)];
                delay = delay < remaining ? delay : remaining;
                StorageLog.DatabaseUnreachable(logger, db.Key, (long)delay.TotalMilliseconds, db.ToString(), error.DeveloperDetail ?? error.Code.Value);
                await Task.Delay(delay, clock, ct).ConfigureAwait(false);
            }
        }
    }

    public Task MigrateAsync(CancellationToken ct) => migrations.RunAsync(ct);
}
