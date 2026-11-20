using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>Feeds the connector's health reports to its connection runtime (spec IRT §7.2).</summary>
internal sealed class ConnectionHealthReporter(Action<ConnectionStatus, ConnectorMessage?> report) : IConnectionHealth
{
    public void Report(ConnectionStatus status, ConnectorMessage? reason = null) => report(status, reason);
}

/// <summary>The context given to one connector instance (spec IRT §8, INT §7).</summary>
internal sealed class ConnectorContext(
    ConnectionInfo connection,
    JsonElement settings,
    ISecretAccessor secrets,
    IObservationSink sink,
    IDeviceMap devices,
    IConnectionStore store,
    IConnectorScheduler scheduler,
    IRequestGate gate,
    IConnectionHealth health,
    ISecureHttp http,
    IInboundEndpoints inbound,
    IConnectorMetrics metrics,
    ILoggerFactory logging,
    TimeProvider time,
    CancellationToken stopping) : IConnectorContext
{
    public ConnectionInfo Connection { get; } = connection;

    public JsonElement Settings { get; } = settings;

    public ISecretAccessor Secrets { get; } = secrets;

    public IObservationSink Sink { get; } = sink;

    public IDeviceMap Devices { get; } = devices;

    public IConnectionStore Store { get; } = store;

    public IConnectorScheduler Scheduler { get; } = scheduler;

    public IRequestGate Gate { get; } = gate;

    public IConnectionHealth Health { get; } = health;

    public ISecureHttp Http { get; } = http;

    public IInboundEndpoints Inbound { get; } = inbound;

    public IConnectorMetrics Metrics { get; } = metrics;

    public ILoggerFactory Logging { get; } = logging;

    public TimeProvider Time { get; } = time;

    public CancellationToken Stopping { get; } = stopping;

    public T GetSettings<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Settings.Deserialize(typeInfo) ?? throw new InvalidOperationException("The connection settings are null.");
    }
}
