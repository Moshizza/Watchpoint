using Microsoft.Extensions.Logging;
using Watchpoint.History.Contracts;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Internal.Lag;

/// <summary>Measures how far a consumer is behind and raises the <c>history.lagWarning</c> system event (spec HIS §5).</summary>
internal sealed class LagMonitor(
    HistorySettings settings,
    TimeProvider time,
    HistoryMetrics metrics,
    ILogger<LagMonitor> logger,
    IEventLogWriter? log,
    ILogRecordTypeRegistry? registry)
{
    /// <summary>The system event kind raised when a consumer is too far behind.</summary>
    public const string WarningKind = "history.lagWarning";

    private readonly Dictionary<string, DateTimeOffset> _warnedAt = new(StringComparer.Ordinal);

    /// <summary>Records the lag of a consumer: the age of the oldest record it has not written; warns at most once per warning period.</summary>
    /// <param name="consumer">The consumer.</param>
    /// <param name="oldestRecord">The received time of the oldest record not written yet.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ObserveAsync(string consumer, DateTimeOffset oldestRecord, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var lag = now - oldestRecord;
        metrics.SetLag(consumer, lag);
        if (lag <= settings.LagWarning)
        {
            _warnedAt.Remove(consumer);
            return;
        }

        if (_warnedAt.TryGetValue(consumer, out var last) && now - last < settings.LagWarning)
        {
            return;
        }

        _warnedAt[consumer] = now;
        HistoryLog.Lag(logger, (long)lag.TotalSeconds, consumer);
        if (log is null || registry is null)
        {
            return;
        }

        try
        {
            var body = new SysEventBody(
                WarningKind,
                new Dictionary<string, WpValue> { ["consumer"] = WpValue.String(consumer), ["lagSeconds"] = WpValue.Int((long)lag.TotalSeconds) },
                Severity.Warning);
            var envelope = new LogEnvelope(
                Guid.CreateVersion7(), HistoryRecordKinds.SysEvent, now, null, 0, null, null, RecordOrigin.System, RecordCorrelation.None,
                registry.SerializeBody(HistoryRecordKinds.SysEvent, body));
            await log.AppendGlobalAsync(LogStream.Sys, envelope, ct).ConfigureAwait(false);
        }
        catch (PipelineException)
        {
            // The log is unreachable too: the warning in the node log above is all that can be said now; the next period tries again.
        }
    }

    /// <summary>Records the lag after a batch was written: the age of its newest record.</summary>
    /// <param name="consumer">The consumer.</param>
    /// <param name="newestRecord">The received time of the newest record of the batch.</param>
    public void Processed(string consumer, DateTimeOffset newestRecord)
    {
        var lag = time.GetUtcNow() - newestRecord;
        metrics.SetLag(consumer, lag);
        if (lag <= settings.LagWarning)
        {
            _warnedAt.Remove(consumer);
        }
    }
}
