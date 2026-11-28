using System.Diagnostics.Metrics;

namespace Watchpoint.Api.Internal.Pipeline;

/// <summary>Metrics of the REST pipeline (meter <c>Watchpoint.Api</c>, spec API §10).</summary>
internal sealed class ApiMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.Api";

    private readonly Meter _meter;

    public ApiMetrics(IMeterFactory factory)
    {
        _meter = factory.Create(MeterName);
        Requests = _meter.CreateCounter<long>("watchpoint.api.requests", description: "REST requests by route and status");
        Duration = _meter.CreateHistogram<double>("watchpoint.api.request.duration", "ms", "REST request duration by route");
    }

    public Counter<long> Requests { get; }

    public Histogram<double> Duration { get; }

    public void Dispose() => _meter.Dispose();
}
