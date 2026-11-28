using System.Diagnostics.Metrics;

namespace Watchpoint.Audit.Internal;

/// <summary>Metrics of the audit module (spec AUD §12): meter <c>Watchpoint.Audit</c>.</summary>
internal sealed class AuditMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.Audit";

    private readonly Meter _meter;
    private readonly Counter<long> _records;
    private readonly Counter<long> _fallback;
    private readonly Counter<long> _issues;
    private Func<int> _depth = static () => 0;

    public AuditMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _records = _meter.CreateCounter<long>("watchpoint.audit.records", "{records}", "Audit records written, by action module and outcome.");
        _fallback = _meter.CreateCounter<long>("watchpoint.audit.fallback", "{records}", "Audit records written to the fallback file.");
        _issues = _meter.CreateCounter<long>("watchpoint.audit.verify.issues", "{issues}", "Integrity issues found by verification.");
        _meter.CreateObservableGauge("watchpoint.audit.queue.depth", () => _depth(), "{records}", "Records waiting in the writer channel.");
    }

    public void BindQueueDepth(Func<int> depth) => _depth = depth;

    public void Written(string action, short outcome)
    {
        var dot = action.IndexOf('.', StringComparison.Ordinal);
        _records.Add(1, new KeyValuePair<string, object?>("module", dot > 0 ? action[..dot] : action), new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void Fallback() => _fallback.Add(1);

    public void Issues(int count) => _issues.Add(count);

    public void Dispose() => _meter.Dispose();
}
