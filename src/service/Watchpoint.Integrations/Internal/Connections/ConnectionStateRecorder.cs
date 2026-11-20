using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Integrations.Internal.Connections;

/// <summary>Runs asynchronous work one item at a time, in order; a failing item is logged and the next one runs.</summary>
internal sealed class SerialQueue : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _items = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _consumer;
    private readonly ILogger _logger;
    private readonly Guid _connection;
    private long _pending;

    public SerialQueue(ILogger logger, Guid connection)
    {
        _logger = logger;
        _connection = connection;
        _consumer = Task.Run(ConsumeAsync);
    }

    public bool Post(Func<Task> item)
    {
        Interlocked.Increment(ref _pending);
        if (_items.Writer.TryWrite(item))
        {
            return true;
        }

        Interlocked.Decrement(ref _pending);
        return false;
    }

    /// <summary>Completes when every item posted before the call ran.</summary>
    public Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        }))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _items.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _items.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await item().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IntegrationLog.RuntimeOperationFailed(_logger, ex, _connection, "queued work");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }
}

/// <summary>The body of a <c>connection.state</c> record in <c>WP_SYS</c> (spec IRT §7.2).</summary>
internal sealed record ConnectionStateRecordBody(Guid ConnectionId, string State, string? Reason)
{
    public const string Kind = "connection.state";
    public const int SchemaVersion = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConnectionStateRecordBody))]
internal sealed partial class ConnectionJsonContext : JsonSerializerContext;

/// <summary>A state change to record.</summary>
internal sealed record ConnectionTransition(Connection Connection, ConnectionState Previous, ConnectionStatus Status);

/// <summary>
/// Records every transition (spec IRT §7.2): <c>StatusChanged</c>, log 1820, a <c>connection_events</c> row, a
/// <c>connection.state</c> record in <c>WP_SYS</c>, and — on <c>Faulted</c>, <c>Stopping</c>, <c>Disabled</c> — a health
/// observation <c>Unknown</c> for the connection's root servers so their devices become stale (STA §6.1). Called from
/// the connection's ordered queue; every step is independent and its failure is logged.
/// </summary>
internal sealed class ConnectionStateRecorder
{
    private static readonly TimeSpan _ingestTimeout = TimeSpan.FromSeconds(30);

    private readonly IConnectionRepository _repository;
    private readonly IObservationIngest _ingest;
    private readonly IRegistryReader _registry;
    private readonly IEventLogWriter? _log;
    private readonly ILogRecordTypeRegistry? _kinds;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ConnectionStateRecorder(IConnectionRepository repository, IObservationIngest ingest, IRegistryReader registry, TimeProvider time,
        ILogger<ConnectionStateRecorder> logger, IEventLogWriter? log = null, ILogRecordTypeRegistry? kinds = null)
    {
        _repository = repository;
        _ingest = ingest;
        _registry = registry;
        _time = time;
        _logger = logger;
        _log = log;
        _kinds = kinds;
        if (kinds is not null && !kinds.TryGetSchemaVersion(ConnectionStateRecordBody.Kind, out _))
        {
            kinds.Register(ConnectionStateRecordBody.Kind, ConnectionStateRecordBody.SchemaVersion, ConnectionJsonContext.Default.ConnectionStateRecordBody);
        }
    }

    public event EventHandler<ConnectionStatusChangedEventArgs>? StatusChanged;

    public async Task RecordAsync(ConnectionTransition transition, object sender)
    {
        var (connection, previous, status) = transition;
        var reason = status.Reason?.Key;
        try
        {
            StatusChanged?.Invoke(sender, new ConnectionStatusChangedEventArgs(previous, status));
        }
        catch (Exception ex)
        {
            IntegrationLog.StatusHandlerFailed(_logger, ex);
        }

        IntegrationLog.ConnectionStateChanged(_logger, connection.Id.Value, connection.Name.Default, previous, status.State, reason ?? "-");
        await StepAsync(status, () => _repository.AppendEventAsync(new ConnectionEvent(connection.Id, status.Since, status.State, status.Reason), CancellationToken.None)).ConfigureAwait(false);
        if (_log is not null && _kinds is not null)
        {
            await StepAsync(status, async () =>
            {
                var body = _kinds.SerializeBody(ConnectionStateRecordBody.Kind, new ConnectionStateRecordBody(connection.Id.Value, ConnectionColumns.StateText(status.State), reason));
                var envelope = new LogEnvelope(Guid.CreateVersion7(status.Since), ConnectionStateRecordBody.Kind, status.Since, null, 0, null, null,
                    new RecordOrigin(RecordOriginType.Connection, connection.Id.Value), RecordCorrelation.None, body);
                await _log.AppendGlobalAsync(LogStream.Sys, envelope, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        if (status.State is ConnectionState.Faulted or ConnectionState.Stopping or ConnectionState.Disabled)
        {
            await StepAsync(status, () => MarkRootServersUnknownAsync(connection.Id, reason ?? "irt.connection." + ConnectionColumns.StateText(status.State))).ConfigureAwait(false);
        }
    }

    /// <summary>Ingests the health of the connection's root servers (also used for the <c>flooding</c> fault).</summary>
    public async Task ReportRootHealthAsync(ConnectionId connection, Func<string, Connectivity> connectivity, IReadOnlyList<string> faults, string? reason)
    {
        var roots = _registry.GetServersOfConnection(connection).Where(s => s.ParentId is null).ToList();
        if (roots.Count == 0)
        {
            return;
        }

        var batch = roots.Select(s => new Observation
        {
            Id = Guid.CreateVersion7(_time.GetUtcNow()),
            Kind = ObservationKind.Health,
            Server = s.Id,
            Health = new HealthReport(connectivity(s.NativeId), faults, reason),
            Connection = connection,
        }).ToList();
        using var timeout = new CancellationTokenSource(_ingestTimeout, _time);
        await _ingest.IngestAsync(batch, timeout.Token).ConfigureAwait(false);
    }

    private Task MarkRootServersUnknownAsync(ConnectionId connection, string reason) =>
        ReportRootHealthAsync(connection, _ => Connectivity.Unknown, [], reason);

    private async Task StepAsync(ConnectionStatus status, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IntegrationLog.StateRecordFailed(_logger, ex, status.Id.Value, status.State);
        }
    }
}
