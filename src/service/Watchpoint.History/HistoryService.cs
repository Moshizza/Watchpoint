using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal;
using Watchpoint.History.Internal.Policies;
using Watchpoint.History.Internal.Writers;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History;

/// <summary>
/// Hosted service of history (spec HIS §5–§6): creates the schema, runs the five consumers (events from <c>WP_LOG</c> and
/// <c>WP_SYS</c>, alarms, commands, properties) and, when a state source is registered, the periodic sampler.
/// </summary>
internal sealed class HistoryService : BackgroundService
{
    private readonly IHistoryStore _store;
    private readonly HistoryWriters _writers;
    private readonly ConsumerRunner _runner;
    private readonly HistorySettings _settings;
    private readonly TimeProvider _time;
    private readonly PeriodicSampler? _sampler;
    private readonly ILogger<HistoryService> _logger;

    public HistoryService(
        IHistoryStore store,
        HistoryWriters writers,
        ConsumerRunner runner,
        HistorySettings settings,
        TimeProvider time,
        ILogRecordTypeRegistry registry,
        ILogger<HistoryService> logger,
        ILoggerFactory loggers,
        IPeriodicPropertySource? periodicSource = null)
    {
        _store = store;
        _writers = writers;
        _runner = runner;
        _settings = settings;
        _time = time;
        _logger = logger;
        _sampler = periodicSource is null ? null : new PeriodicSampler(periodicSource, store, time, loggers.CreateLogger<PeriodicSampler>());

        // Record kinds are registered before the host starts any other service, so that anything appended after start-up can use them.
        HistoryJson.RegisterKinds(registry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);
        List<Task> tasks = [.. _writers.Create().Select(d => _runner.RunAsync(d, stoppingToken)), MaintainSchemaAsync(stoppingToken)];
        if (_sampler is not null)
        {
            tasks.Add(_sampler.RunAsync(stoppingToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var delay = _settings.RetryDelayMin;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _store.EnsureSchemaAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The database may still be starting: the schema is retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HistoryLog.SchemaFailed(_logger, ex.GetType().Name);
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay + delay < _settings.RetryDelayMax ? delay + delay : _settings.RetryDelayMax;
        }
    }

    /// <summary>Tops up the monthly partitions (no TimescaleDB) until the retention manager takes over (T5.01); a no-op with hypertables.</summary>
    private async Task MaintainSchemaAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_settings.SchemaMaintenanceInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await EnsureSchemaAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
    }
}
