using System.Diagnostics.Metrics;

namespace Watchpoint.Commands.Internal;

/// <summary>Metrics of the commands module (meter <c>Watchpoint.Commands</c>, spec CMD §14).</summary>
internal sealed class CommandMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.Commands";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _latency;
    private readonly Counter<long> _verification;
    private readonly Counter<long> _ignored;

    public CommandMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _requests = _meter.CreateCounter<long>("watchpoint.commands.requests", "{requests}", "Command requests, by path and outcome (accepted, rejected, duplicate).");
        _latency = _meter.CreateHistogram<double>("watchpoint.commands.latency", "ms", "Time from request to the terminal state, by path.");
        _verification = _meter.CreateCounter<long>("watchpoint.commands.verification", "{commands}", "Verification outcomes, by result.");
        _ignored = _meter.CreateCounter<long>("watchpoint.commands.transitions.ignored", "{records}", "Late or invalid lifecycle records ignored, by kind.");
    }

    public void Request(string path, string outcome) =>
        _requests.Add(1, new KeyValuePair<string, object?>("path", path), new KeyValuePair<string, object?>("outcome", outcome));

    public void Finished(string path, TimeSpan latency) =>
        _latency.Record(latency.TotalMilliseconds, new KeyValuePair<string, object?>("path", path));

    public void Verification(string result) => _verification.Add(1, new KeyValuePair<string, object?>("result", result));

    public void Ignored(string kind) => _ignored.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void Dispose() => _meter.Dispose();
}
