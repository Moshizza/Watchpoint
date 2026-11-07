using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>Everything the runtime gives a connection's connector (INT §7). Implemented by IRT.</summary>
public interface IConnectorContext
{
    /// <summary>Gets the connection described by this context.</summary>
    ConnectionInfo Connection { get; }

    /// <summary>Gets the settings, validated against the manifest's settings schema, defaults applied.</summary>
    JsonElement Settings { get; }

    /// <summary>Deserialises the settings with a source-generated type info.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <param name="typeInfo">The type info.</param>
    /// <returns>The settings.</returns>
    T GetSettings<T>(JsonTypeInfo<T> typeInfo);

    /// <summary>Gets access to the secrets declared in the manifest's secrets schema.</summary>
    ISecretAccessor Secrets { get; }

    /// <summary>Gets the sink for observations.</summary>
    IObservationSink Sink { get; }

    /// <summary>Gets the imported, enabled devices of this connection.</summary>
    IDeviceMap Devices { get; }

    /// <summary>Gets the connection's persisted key-value store (replicated with the connection).</summary>
    IConnectionStore Store { get; }

    /// <summary>Gets the polling scheduler.</summary>
    IConnectorScheduler Scheduler { get; }

    /// <summary>Gets the outbound request gate (concurrency and priorities).</summary>
    IRequestGate Gate { get; }

    /// <summary>Gets the connection health reporter.</summary>
    IConnectionHealth Health { get; }

    /// <summary>Gets the HTTP client factory applying the connection's TLS policy.</summary>
    ISecureHttp Http { get; }

    /// <summary>Gets inbound endpoints (webhooks, TCP and UDP listeners).</summary>
    IInboundEndpoints Inbound { get; }

    /// <summary>Gets the connection's metrics.</summary>
    IConnectorMetrics Metrics { get; }

    /// <summary>Gets the logger factory; loggers redact declared secret fields.</summary>
    ILoggerFactory Logging { get; }

    /// <summary>Gets the clock.</summary>
    TimeProvider Time { get; }

    /// <summary>Gets a token cancelled when <see cref="IConnector.StopAsync"/> begins.</summary>
    CancellationToken Stopping { get; }
}

/// <summary>Description of the connection a connector instance serves.</summary>
/// <param name="Id">The connection id.</param>
/// <param name="Name">The connection name.</param>
/// <param name="PackageId">The integration package id.</param>
/// <param name="PackageVersion">The integration package version.</param>
/// <param name="ServerType">The server type id.</param>
/// <param name="Culture">The site's default culture, for vendor-language hints.</param>
public sealed record ConnectionInfo(Guid Id, string Name, string PackageId, string PackageVersion, string ServerType, string Culture);
