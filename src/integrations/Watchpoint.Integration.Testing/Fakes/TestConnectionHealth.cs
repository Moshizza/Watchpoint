using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Recorded health transition.
/// </summary>
public sealed record HealthReport(ConnectionStatus Status, ConnectorMessage? Reason);

/// <summary>
/// Fake implementation of <see cref="IConnectionHealth"/> tracking state transitions.
/// </summary>
public sealed class TestConnectionHealth : IConnectionHealth
{
    private readonly List<HealthReport> _history = [];
    private readonly object _lock = new();

    /// <summary>Current connection status.</summary>
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Connecting;

    /// <summary>Current failure/status reason.</summary>
    public ConnectorMessage? Reason { get; private set; }

    /// <summary>Chronological list of all reported statuses.</summary>
    public IReadOnlyList<HealthReport> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToList();
            }
        }
    }

    /// <inheritdoc />
    public void Report(ConnectionStatus status, ConnectorMessage? reason = null)
    {
        lock (_lock)
        {
            Status = status;
            Reason = reason;
            _history.Add(new HealthReport(status, reason));
        }
    }
}
