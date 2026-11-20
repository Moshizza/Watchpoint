using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Integrations.Contracts;

/// <summary>
/// Manages connections: configuration, secrets, lifecycle and inventory (spec IRT §7). Lifecycle operations are
/// serialised per connection and never wait on connector code longer than the runtime's timeouts.
/// </summary>
public interface IConnectionManager
{
    /// <summary>Lists the connections.</summary>
    /// <returns>The connections.</returns>
    IReadOnlyList<Connection> List();

    /// <summary>Gets a connection.</summary>
    /// <param name="id">The connection.</param>
    /// <returns>The connection, or <c>irt.connection.not_found</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec IRT §7.1.")]
    Result<Connection> Get(ConnectionId id);

    /// <summary>Gets the live status of a connection (a deleted or unknown connection reads as <see cref="ConnectionState.Disabled"/>).</summary>
    /// <param name="id">The connection.</param>
    /// <returns>The status.</returns>
    ConnectionStatus GetStatus(ConnectionId id);

    /// <summary>Creates a connection: validates it, stores its secrets in the vault and starts it when <see cref="NewConnection.StartNow"/>.</summary>
    /// <param name="request">The connection.</param>
    /// <param name="ctx">Who and why.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The new connection id, or the validation errors.</returns>
    Task<Result<ConnectionId>> CreateAsync(NewConnection request, ChangeContext ctx, CancellationToken ct);

    /// <summary>Updates a connection atomically; a running connector restarts with the new settings and secrets.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="update">The change.</param>
    /// <param name="expectedVersion">The version the caller read.</param>
    /// <param name="ctx">Who and why.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Success, the validation errors, or <c>common.version.conflict</c>.</returns>
    Task<Result> UpdateAsync(ConnectionId id, ConnectionUpdate update, long expectedVersion, ChangeContext ctx, CancellationToken ct);

    /// <summary>Stops and deletes a connection with its secrets, optionally with its servers and devices.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="deleteDevices">Whether its servers and devices are deleted from the registry.</param>
    /// <param name="ctx">Who and why.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Success or <c>irt.connection.not_found</c>.</returns>
    Task<Result> DeleteAsync(ConnectionId id, bool deleteDevices, ChangeContext ctx, CancellationToken ct);

    /// <summary>Enables a connection and starts it (it also clears a fault without automatic restart).</summary>
    /// <param name="id">The connection.</param>
    /// <param name="ct">Cancels the wait (not the start).</param>
    /// <returns>Success or <c>irt.connection.not_found</c>.</returns>
    Task<Result> StartAsync(ConnectionId id, CancellationToken ct);

    /// <summary>Disables a connection and waits until its connector is stopped (or abandoned after the stop timeout).</summary>
    /// <param name="id">The connection.</param>
    /// <param name="ct">Cancels the wait (not the stop).</param>
    /// <returns>Success or <c>irt.connection.not_found</c>.</returns>
    Task<Result> StopAsync(ConnectionId id, CancellationToken ct);

    /// <summary>Restarts an enabled connection with a new connector instance.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="ct">Cancels the wait (not the restart).</param>
    /// <returns>Success, <c>irt.connection.not_found</c> or <c>irt.connection.not_running</c> for a disabled connection.</returns>
    Task<Result> RestartAsync(ConnectionId id, CancellationToken ct);

    /// <summary>Tests unsaved settings on a separate connector instance (never started), within 30 s.</summary>
    /// <param name="request">The settings to test.</param>
    /// <param name="ct">Cancels the test.</param>
    /// <returns>The test result (validation problems are failed results with category <see cref="FailureCategory.InvalidSettings"/>).</returns>
    Task<ConnectionTestResult> TestAsync(TestConnectionRequest request, CancellationToken ct);

    /// <summary>Discovers the inventory of a running connection (<see cref="IDiscovery"/>, or <c>DescribeAsync</c> without it).</summary>
    /// <param name="id">The connection.</param>
    /// <param name="progress">Receives the progress.</param>
    /// <param name="ct">Cancels the discovery.</param>
    /// <returns>The inventory, or <c>irt.connection.not_running</c>.</returns>
    Task<Result<InventorySnapshot>> DiscoverAsync(ConnectionId id, IProgress<DiscoveryProgress>? progress, CancellationToken ct);

    /// <summary>Describes the inventory of a running connection and reconciles it with the registry now.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The reconciliation result, or <c>irt.connection.not_running</c>.</returns>
    Task<Result<ReconciliationResult>> DescribeAndReconcileAsync(ConnectionId id, CancellationToken ct);

    /// <summary>Enables protocol trace logging of a connection for a while (at most one hour).</summary>
    /// <param name="id">The connection.</param>
    /// <param name="duration">How long; zero disables the trace.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Success, <c>irt.connection.not_found</c> or a validation error.</returns>
    Task<Result> SetProtocolTraceAsync(ConnectionId id, TimeSpan duration, CancellationToken ct);

    /// <summary>Lists the state timeline of a connection.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="from">The earliest time (inclusive).</param>
    /// <param name="until">The latest time (exclusive).</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The events in time order.</returns>
    Task<IReadOnlyList<ConnectionEvent>> GetEventsAsync(ConnectionId id, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>Gets the diagnostics of a running connector that implements <see cref="IDiagnostics"/>.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The items, <c>irt.connection.not_running</c>, or <c>common.not_supported</c>.</returns>
    Task<Result<IReadOnlyList<DiagnosticItem>>> GetDiagnosticsAsync(ConnectionId id, CancellationToken ct);

    /// <summary>Raised after every state change, in order per connection.</summary>
    event EventHandler<ConnectionStatusChangedEventArgs>? StatusChanged;
}

/// <summary>Executes commands on connectors (used by CMD, spec IRT §7.5). Never throws for connector failures.</summary>
public interface IConnectionCommandChannel
{
    /// <summary>Executes a command; it is cancelled at its deadline.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="command">The command.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The result: <see cref="FailureCategory.ConnectionDown"/> when not connected, <see cref="FailureCategory.Timeout"/> at the deadline.</returns>
    ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectionId connection, ConnectorCommand command, CancellationToken ct);

    /// <summary>Executes commands as one batch when the connector supports it, one by one otherwise.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="commands">The commands.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>One result per command, in order.</returns>
    ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(ConnectionId connection, IReadOnlyList<ConnectorCommand> commands, CancellationToken ct);

    /// <summary>Tells whether the running connector of a connection executes batches.</summary>
    /// <param name="connection">The connection.</param>
    /// <returns><see langword="true"/> when it implements <see cref="IBatchCommandExecutor"/>.</returns>
    bool SupportsBatch(ConnectionId connection);
}

/// <summary>Admission of a connection by the licence (LIC adds the feature check in M7; M1 allows all).</summary>
public interface IConnectionAdmission
{
    /// <summary>Checks that a connection on this package may be created or started.</summary>
    /// <param name="package">The package.</param>
    /// <param name="serverType">The server type.</param>
    /// <returns>Success or the refusal.</returns>
    Result Admit(PackageInfo package, string serverType);
}

/// <summary>Start and stop of the connections, called by the host's start-up and shutdown tasks.</summary>
public interface IConnectionsLifecycle
{
    /// <summary>Loads the stored connections (phase <c>Catalogue</c>, after the package scan); idempotent.</summary>
    /// <param name="ct">Cancels the load.</param>
    /// <returns>A task.</returns>
    Task LoadAsync(CancellationToken ct);

    /// <summary>Starts the enabled connections, <c>Connections:StartStagger</c> apart, and the watchdog (phase <c>Connections</c>).</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task completing once the starts are scheduled.</returns>
    Task ActivateAsync(CancellationToken ct);

    /// <summary>Stops every connector (node shutdown); the connections stay enabled.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task completing when the connectors are stopped or abandoned.</returns>
    Task StopAllAsync(CancellationToken ct);
}
