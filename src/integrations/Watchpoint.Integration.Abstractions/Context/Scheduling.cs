using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>Schedules periodic work of a connector (INT §7.3).</summary>
public interface IConnectorScheduler
{
    /// <summary>Schedules polling; runs never overlap. Dispose the result to stop.</summary>
    /// <param name="name">A name for logs and metrics.</param>
    /// <param name="interval">The interval between runs.</param>
    /// <param name="work">The work.</param>
    /// <param name="options">Options; defaults when <see langword="null"/>.</param>
    /// <returns>A handle stopping the polling when disposed.</returns>
    IDisposable SchedulePolling(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions? options = null);
}

/// <summary>Polling options.</summary>
/// <param name="Jitter">Relative jitter of the interval (0.1 = ±10 %).</param>
/// <param name="RunWhenDisconnected">Whether to run while the connection reports Disconnected.</param>
/// <param name="MaxBackoff">Maximum delay after repeated failures; <see langword="null"/> = 5 minutes.</param>
/// <param name="Priority">The request gate priority of the work.</param>
public sealed record PollingOptions(double Jitter = 0.1, bool RunWhenDisconnected = false, TimeSpan? MaxBackoff = null,
    CommandPriority Priority = CommandPriority.Background);

/// <summary>Per-connection outbound concurrency with priorities (INT §7.3).</summary>
public interface IRequestGate
{
    /// <summary>Runs a request when a slot is free; higher priorities go first.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="priority">The priority.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation (also while waiting).</param>
    /// <returns>The request's result.</returns>
    ValueTask<T> RunAsync<T>(CommandPriority priority, Func<CancellationToken, ValueTask<T>> request, CancellationToken ct);

    /// <summary>Gets the number of concurrent requests (settings or manifest limits; default 4).</summary>
    int MaxConcurrency { get; }
}

/// <summary>Status of the connection as reported by the connector; the runtime maps it to the connection state (IRT §7).</summary>
public enum ConnectionStatus
{
    /// <summary>Connecting.</summary>
    Connecting,

    /// <summary>Connected and working.</summary>
    Connected,

    /// <summary>Connected but partly failing.</summary>
    Degraded,

    /// <summary>Not connected.</summary>
    Disconnected,
}

/// <summary>Reports the connection's status (INT §7.3).</summary>
public interface IConnectionHealth
{
    /// <summary>Reports a status.</summary>
    /// <param name="status">The status.</param>
    /// <param name="reason">Why, for administrators.</param>
    void Report(ConnectionStatus status, ConnectorMessage? reason = null);
}

/// <summary>Connection metrics (INT §7.3).</summary>
public interface IConnectorMetrics
{
    /// <summary>Counts an outbound request.</summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="success">Whether it succeeded.</param>
    /// <param name="duration">Its duration.</param>
    void CountRequest(string operation, bool success, TimeSpan duration);

    /// <summary>Sets a gauge.</summary>
    /// <param name="name">The name, pattern <c>^[a-z][a-z0-9_]{1,40}$</c>.</param>
    /// <param name="value">The value.</param>
    void SetGauge(string name, double value);
}
