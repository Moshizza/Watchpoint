using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;

namespace Watchpoint.Storage.Internal;

/// <summary>Metrics of meter <c>Watchpoint.Storage</c> (spec STO §10); snapshot metrics are added with the state store (T1.04).</summary>
internal sealed class StorageMetrics
{
    public const string MeterName = "Watchpoint.Storage";

    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.Ordinal);

    public StorageMetrics(IMeterFactory meters, StoreMapHolder holder)
    {
        var meter = meters.Create(MeterName);
        meter.CreateObservableGauge("watchpoint.storage.pool.busy", () => PoolBusy(holder), "{connection}", "Connections in use per database");
        meter.CreateObservableGauge("watchpoint.storage.database.size", () =>
            _sizes.Select(s => new Measurement<long>(s.Value, new KeyValuePair<string, object?>("database", s.Key))), "By", "Database size");
    }

    public void RecordSize(string database, long bytes) => _sizes[database] = bytes;

    private static IEnumerable<Measurement<int>> PoolBusy(StoreMapHolder holder)
    {
        if (!holder.Result.TryGetValue(out var map, out _))
        {
            yield break;
        }

        foreach (var db in map.UsedDatabases)
        {
            if (map.ProviderOf(db).GetPoolStatistics(db) is { } pool)
            {
                yield return new Measurement<int>(pool.Busy, new KeyValuePair<string, object?>("database", db.Key));
            }
        }
    }
}

/// <summary>Polls database sizes every 10 minutes for <c>watchpoint.storage.database.size</c>.</summary>
internal sealed class DatabaseSizePoller(StoreMapHolder holder, StorageMetrics metrics, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!holder.Result.TryGetValue(out var map, out _))
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            foreach (var db in map.UsedDatabases)
            {
                var probe = await map.ProviderOf(db).ProbeAsync(db, stoppingToken).ConfigureAwait(false);
                if (probe.TryGetValue(out var info, out _))
                {
                    metrics.RecordSize(db.Key, info.SizeBytes);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
