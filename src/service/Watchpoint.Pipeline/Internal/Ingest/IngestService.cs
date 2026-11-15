using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Partitions;

namespace Watchpoint.Pipeline.Internal.Ingest;

/// <summary>
/// The entry point of observations (spec PIP §6–§8): validation, connection quota and coalescing, routing to the
/// partition workers. Bad observations are rejected in the result, never thrown.
/// </summary>
internal sealed class IngestService : IObservationIngest, IDisposable
{
    private static readonly IngestResult _empty = new(0, 0, 0, []);

    private readonly ConcurrentDictionary<ConnectionId, ConnectionQuota> _quotas = new();
    private readonly ConcurrentDictionary<(ConnectionId Connection, string Code), RejectionCount> _rejections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ObservationValidator _validator;
    private readonly PartitionRouter _router;
    private readonly IngestOptions _options;
    private readonly TimeProvider _time;
    private readonly PipelineMetrics _metrics;
    private readonly IReadOnlyList<IIngestFloodingListener> _listeners;
    private readonly ILogger<IngestService> _logger;
    private readonly Func<IngestItem, CancellationToken, ValueTask> _route;
    private int _disposed;

    public IngestService(
        ObservationValidator validator,
        PartitionRouter router,
        IngestOptions options,
        TimeProvider timeProvider,
        PipelineMetrics metrics,
        IEnumerable<IIngestFloodingListener> listeners,
        ILogger<IngestService> logger)
    {
        _validator = validator;
        _router = router;
        _options = options;
        _time = timeProvider;
        _metrics = metrics;
        _listeners = listeners.ToList();
        _logger = logger;
        _route = router.RouteAsync;
    }

    public async ValueTask<IngestResult> IngestAsync(IReadOnlyList<Observation> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return _empty;
        }

        var receivedTime = _time.GetUtcNow();
        var call = new IngestCall(batch.Count, _time.GetTimestamp(), _metrics, batch[0]?.Connection ?? default);
        try
        {
            var size = IngestLimits.CheckBatchSize(batch);
            ConnectionQuota? quota = null;
            ConnectionId quotaOf = default;
            for (var i = 0; i < batch.Count; i++)
            {
                var observation = batch[i];
                if (observation is null)
                {
                    Reject(call, default, Guid.Empty, PipelineErrors.ObservationMalformed("The observation is null."));
                    continue;
                }

                if (!size.IsSuccess)
                {
                    Reject(call, observation.Connection, observation.Id, size.Error);
                    continue;
                }

                var outcome = _validator.Validate(observation, receivedTime, call);
                if (outcome.Error is { } error)
                {
                    Reject(call, observation.Connection, observation.Id, error);
                    continue;
                }

                if (outcome.Item is not { } item)
                {
                    call.Complete(IngestOutcome.Disabled);
                    continue;
                }

                if (quota is null || quotaOf != item.Connection)
                {
                    quotaOf = item.Connection;
                    quota = _quotas.GetOrAdd(quotaOf, CreateQuota);
                    await quota.WaitWhileFloodedAsync(ct).ConfigureAwait(false);
                }

                if (quota.TryPass(item, out var replaced))
                {
                    await _router.RouteAsync(item, ct).ConfigureAwait(false);
                }

                replaced?.Settle(IngestOutcome.Coalesced);
            }
        }
        finally
        {
            call.Seal();
        }

        return await call.Completion.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // The container disposes the service once per registration (class and interface).
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    /// <summary>The quota of a connection (for tests).</summary>
    internal ConnectionQuota? QuotaOf(ConnectionId connection) => _quotas.GetValueOrDefault(connection);

    private ConnectionQuota CreateQuota(ConnectionId connection) =>
        new(connection, _options, _time, _route, _listeners, _logger, _stop.Token);

    /// <summary>Rejects an observation and logs at most once per interval per connection and code (log 1620).</summary>
    private void Reject(IngestCall call, ConnectionId connection, Guid observationId, Error error)
    {
        call.Reject(observationId, error);
        var count = _rejections.GetOrAdd((connection, error.Code.Value), static _ => new RejectionCount());
        long toLog;
        lock (count)
        {
            count.Pending++;
            var now = _time.GetTimestamp();
            if (count.Logged && _time.GetElapsedTime(count.LastLog, now) < _options.RejectionLogInterval)
            {
                return;
            }

            count.Logged = true;
            count.LastLog = now;
            toLog = count.Pending;
            count.Pending = 0;
        }

        PipelineLog.ObservationsRejected(_logger, connection.Value, toLog, error.Code.Value, observationId);
    }

    private sealed class RejectionCount
    {
        public long Pending { get; set; }

        public bool Logged { get; set; }

        public long LastLog { get; set; }
    }
}
