using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Watchpoint.Storage.Internal;

/// <summary>Readiness check (spec STO §6.4): <c>SELECT 1</c> with a 2 s timeout on every database used by a store.</summary>
internal sealed class StorageHealthCheck(StoreMapHolder holder) : IHealthCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var unhealthy = new List<string>();
        foreach (var db in holder.Map.UsedDatabases)
        {
            var provider = holder.Map.ProviderOf(db);
            data[$"{db.Key}.provider"] = provider.ProviderId;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);
                await using var connection = await provider.OpenConnectionAsync(db, timeout.Token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
                data[$"{db.Key}.version"] = connection.ServerVersion;
                if (!data.ContainsKey("provider"))
                {
                    // Summary of the first database (the configuration store's when all stores share it).
                    data["provider"] = provider.ProviderId;
                    data["version"] = connection.ServerVersion;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                unhealthy.Add(db.Key);
                data[$"{db.Key}.error"] = ex.GetType().Name;
            }

            if (provider.GetPoolStatistics(db) is { } pool)
            {
                data[$"{db.Key}.pool.total"] = pool.Total;
                data[$"{db.Key}.pool.idle"] = pool.Idle;
                data[$"{db.Key}.pool.busy"] = pool.Busy;
            }
        }

        return unhealthy.Count == 0
            ? HealthCheckResult.Healthy("storage", data)
            : HealthCheckResult.Unhealthy($"database unreachable: {string.Join(", ", unhealthy)}", data: data);
    }
}
