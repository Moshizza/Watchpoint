using System.Diagnostics.Metrics;

namespace Watchpoint.State.Internal;

/// <summary>Metrics of the snapshotter (spec STA §11): meter <c>Watchpoint.State</c>.</summary>
internal sealed class StateMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.State";

    private readonly Meter _meter;

    public StateMetrics(Snapshotter snapshotter, IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge("watchpoint.state.snapshot.lag", () => snapshotter.Lag, "{records}", "Sequences between applied and snapshotted, over all partitions.");
    }

    public void Dispose() => _meter.Dispose();
}
