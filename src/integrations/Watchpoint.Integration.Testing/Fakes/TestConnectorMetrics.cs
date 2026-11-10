using System.Collections.Concurrent;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Recorded metrics entry.
/// </summary>
public sealed record RecordedRequestMetric(string Operation, bool Success, TimeSpan Duration);

/// <summary>
/// Fake implementation of <see cref="IConnectorMetrics"/>.
/// </summary>
public sealed class TestConnectorMetrics : IConnectorMetrics
{
    private readonly ConcurrentBag<RecordedRequestMetric> _requests = [];
    private readonly ConcurrentDictionary<string, double> _gauges = new(StringComparer.Ordinal);

    /// <summary>All recorded request counts.</summary>
    public IReadOnlyCollection<RecordedRequestMetric> Requests => _requests;

    /// <summary>All recorded gauges.</summary>
    public IReadOnlyDictionary<string, double> Gauges => _gauges;

    /// <inheritdoc />
    public void CountRequest(string operation, bool success, TimeSpan duration)
    {
        _requests.Add(new RecordedRequestMetric(operation, success, duration));
    }

    /// <inheritdoc />
    public void SetGauge(string name, double value)
    {
        _gauges[name] = value;
    }
}
