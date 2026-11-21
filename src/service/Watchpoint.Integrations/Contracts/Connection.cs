using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations.Contracts;

/// <summary>A configured connector instance (spec IRT §7.1). Secrets are referenced by vault id, never held in clear.</summary>
/// <param name="Id">The connection id.</param>
/// <param name="Name">The display name.</param>
/// <param name="PackageId">The package that provides the connector.</param>
/// <param name="PackageVersion">The package version the connection is pinned to.</param>
/// <param name="ServerType">The connector's server type in the package.</param>
/// <param name="Settings">The settings document, valid against the connector's settings schema.</param>
/// <param name="Secrets">The secret fields that are set: field id → vault secret id.</param>
/// <param name="LocationId">The default location of the connection's servers and devices.</param>
/// <param name="HostMode">Where the connector runs.</param>
/// <param name="HostId">The isolated or remote host, if any (M4).</param>
/// <param name="Enabled">Whether the connection should run.</param>
/// <param name="AutoImport">Whether newly discovered devices are imported automatically.</param>
/// <param name="DescribeInterval">How often the inventory is described and reconciled while connected.</param>
/// <param name="ProtocolTraceUntil">Protocol trace logging is enabled until this time.</param>
/// <param name="Version">The row version (optimistic concurrency).</param>
public sealed record Connection(ConnectionId Id, LocalizedName Name, PackageId PackageId, SemVersion PackageVersion,
    string ServerType, JsonElement Settings, IReadOnlyDictionary<string, SecretId> Secrets, LocationId LocationId,
    HostMode HostMode, HostId? HostId, bool Enabled, bool AutoImport, TimeSpan DescribeInterval,
    DateTimeOffset? ProtocolTraceUntil, long Version)
{
    /// <summary>The default <see cref="DescribeInterval"/>.</summary>
    public static readonly TimeSpan DefaultDescribeInterval = TimeSpan.FromHours(1);
}

/// <summary>Where a connector runs.</summary>
public enum HostMode
{
    /// <summary>In the node process, in the package's load context.</summary>
    InProcess,

    /// <summary>In an isolated integration host process on the node (IHO, M4).</summary>
    Isolated,

    /// <summary>In a remote integration host (IHO, M4).</summary>
    Remote,
}

/// <summary>The state of a connection (spec IRT §7.2).</summary>
public enum ConnectionState
{
    /// <summary>Enabled but not running (before the <c>Connections</c> start-up phase, after a stop for a restart, or its package is disabled).</summary>
    Configured,

    /// <summary>Not enabled.</summary>
    Disabled,

    /// <summary>The connector instance is being created, initialised and started, and has not reported connected yet.</summary>
    Starting,

    /// <summary>The connector reported connected.</summary>
    Connected,

    /// <summary>The connector reported degraded.</summary>
    Degraded,

    /// <summary>The connector lost its connection and reconnects by itself.</summary>
    Reconnecting,

    /// <summary>The connector failed (restarted with back-off), its initialisation was refused, or it has been reconnecting for too long.</summary>
    Faulted,

    /// <summary>The connector instance is being stopped and disposed.</summary>
    Stopping,
}

/// <summary>The live status of a connection.</summary>
/// <param name="Id">The connection.</param>
/// <param name="State">The state.</param>
/// <param name="Reason">Why the connection is in this state (connector messages are qualified with <c>packages.&lt;packageId&gt;.</c>, runtime reasons are <c>irt.connection.*</c>).</param>
/// <param name="Since">When the state was entered.</param>
/// <param name="RestartCount">How many times the runtime restarted the connector after a fault.</param>
/// <param name="LastConnectedAt">When the connector last reported connected.</param>
/// <param name="Counters">The counters since the node started.</param>
public sealed record ConnectionStatus(ConnectionId Id, ConnectionState State, Message? Reason,
    DateTimeOffset Since, int RestartCount, DateTimeOffset? LastConnectedAt, ConnectionCounters Counters);

/// <summary>The counters of a connection since the node started.</summary>
/// <param name="ObservationsAccepted">Observations accepted, unchanged or coalesced by the ingest.</param>
/// <param name="ObservationsRejected">Observations rejected by the ingest or refused by the sink.</param>
/// <param name="UnknownDevices">Reports dropped because the device or server is not imported.</param>
/// <param name="CommandsSucceeded">Commands that succeeded.</param>
/// <param name="CommandsFailed">Commands that failed or were not supported.</param>
/// <param name="CommandLatencyP95Ms">The 95th percentile of the last 100 command durations, in milliseconds.</param>
public sealed record ConnectionCounters(long ObservationsAccepted, long ObservationsRejected,
    long UnknownDevices, long CommandsSucceeded, long CommandsFailed, double CommandLatencyP95Ms)
{
    /// <summary>All counters at zero.</summary>
    public static ConnectionCounters Zero { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>A state change of a connection.</summary>
/// <param name="previous">The previous state.</param>
/// <param name="status">The new status.</param>
public sealed class ConnectionStatusChangedEventArgs(ConnectionState previous, ConnectionStatus status) : EventArgs
{
    /// <summary>Gets the previous state.</summary>
    public ConnectionState Previous { get; } = previous;

    /// <summary>Gets the new status.</summary>
    public ConnectionStatus Status { get; } = status;
}

/// <summary>A request to create a connection.</summary>
/// <param name="Name">The display name.</param>
/// <param name="PackageId">The package.</param>
/// <param name="PackageVersion">The package version; <see langword="null"/> takes the latest active one.</param>
/// <param name="ServerType">The connector's server type.</param>
/// <param name="Settings">The settings document.</param>
/// <param name="SecretValues">The secret values by field id, stored in the vault (never persisted in clear).</param>
/// <param name="LocationId">The default location.</param>
/// <param name="AutoImport">Whether discovered devices are imported automatically.</param>
/// <param name="StartNow">Whether the connection is enabled (it starts now when the runtime is running).</param>
public sealed record NewConnection(LocalizedName Name, PackageId PackageId, SemVersion? PackageVersion,
    string ServerType, JsonElement Settings, IReadOnlyDictionary<string, string> SecretValues,
    LocationId LocationId, bool AutoImport, bool StartNow)
{
    /// <summary>Gets where the connector runs (M1: in-process only).</summary>
    public HostMode HostMode { get; init; } = HostMode.InProcess;
}

/// <summary>A change to a connection; <see langword="null"/> members are kept.</summary>
/// <param name="Name">A new name.</param>
/// <param name="Settings">New settings (the whole document).</param>
/// <param name="SecretValues">Secrets to replace (field id → value; an empty value clears an optional field).</param>
/// <param name="LocationId">A new default location.</param>
/// <param name="Enabled">Start or stop the connection.</param>
/// <param name="AutoImport">A new auto-import flag.</param>
/// <param name="HostMode">A new host mode.</param>
/// <param name="DescribeInterval">A new describe interval.</param>
public sealed record ConnectionUpdate(LocalizedName? Name = null, JsonElement? Settings = null,
    IReadOnlyDictionary<string, string>? SecretValues = null, LocationId? LocationId = null, bool? Enabled = null,
    bool? AutoImport = null, HostMode? HostMode = null, TimeSpan? DescribeInterval = null)
{
    /// <summary>Gets a value indicating whether the update needs a running connector to restart.</summary>
    public bool NeedsRestart => Settings is not null || SecretValues is not null || HostMode is not null;
}

/// <summary>A connection test with unsaved settings (the wizard): the secrets are used for the test only.</summary>
/// <param name="PackageId">The package.</param>
/// <param name="PackageVersion">The package version; <see langword="null"/> takes the latest active one.</param>
/// <param name="ServerType">The connector's server type.</param>
/// <param name="Settings">The settings document.</param>
/// <param name="SecretValues">The secret values by field id.</param>
/// <param name="ExistingConnection">An existing connection whose stored secrets fill the fields not given.</param>
public sealed record TestConnectionRequest(PackageId PackageId, SemVersion? PackageVersion, string ServerType,
    JsonElement Settings, IReadOnlyDictionary<string, string> SecretValues, ConnectionId? ExistingConnection = null);

/// <summary>A state change of a connection as recorded in its timeline (<c>connection_events</c>).</summary>
/// <param name="ConnectionId">The connection.</param>
/// <param name="At">When the state was entered.</param>
/// <param name="State">The state.</param>
/// <param name="Reason">Why, if known.</param>
public sealed record ConnectionEvent(ConnectionId ConnectionId, DateTimeOffset At, ConnectionState State, Message? Reason);
