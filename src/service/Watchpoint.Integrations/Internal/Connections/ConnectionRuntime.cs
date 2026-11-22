using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Sdk;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Context;
using Watchpoint.Integrations.Internal.Hosting;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Registry.Contracts;
using ConnectorStatus = Watchpoint.Integration.Abstractions.Context.ConnectionStatus;
using KernelMessage = Watchpoint.Kernel.Text.Message;

namespace Watchpoint.Integrations.Internal.Connections;

/// <summary>What a connection runtime needs from the node.</summary>
internal sealed class ConnectionRuntimeServices
{
    public required IConnectionHost Host { get; init; }

    public required IPackageCatalog Packages { get; init; }

    public required ConnectorContextFactory Contexts { get; init; }

    public required Storage.IConnectionStoreRepository StoreRepository { get; init; }

    public required InventoryCoordinator Inventory { get; init; }

    public required ConnectionStateRecorder Recorder { get; init; }

    public required IRegistryReader Registry { get; init; }

    public required ConnectionsOptions Options { get; init; }

    public required TimeProvider Time { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>A number in [0, 1) for the restart jitter.</summary>
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;
}

/// <summary>
/// The state machine of one connection (spec IRT §7.2). Every operation, connector report and timer is a message
/// processed one at a time by the connection's mailbox; the mailbox never awaits connector code, which runs on its own
/// tasks bounded by the runtime's timeouts (a connector that hangs or ignores cancellation is abandoned, ADR-003).
/// Transitions are recorded in order on a separate queue (<see cref="ConnectionStateRecorder"/>), so slow I/O never
/// delays the state machine of this or any other connection.
/// </summary>
internal sealed class ConnectionRuntime : IAsyncDisposable
{
    private static readonly TimeSpan _sinkDrain = TimeSpan.FromSeconds(5);

    private readonly ConnectionRuntimeServices _services;
    private readonly ConnectionsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _sender;
    private readonly Channel<Action> _mailbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly SerialQueue _records;
    private readonly ConnectionStore _store;
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly List<TaskCompletionSource> _stopWaiters = [];

    // Mailbox state: read and written by the mailbox only.
    private Connection _connection;
    private ConnectionState _state;
    private KernelMessage? _reason;
    private Instance? _instance;
    private Instance? _tearingDown;
    private Task _teardown = Task.CompletedTask;
    private int _generation;
    private bool _activated;
    private bool _blocked;
    private bool _restartRequested;
    private bool _deleting;
    private bool _packageAvailable = true;
    private KernelMessage? _stopReason;
    private ITimer? _restartTimer;
    private int _restartToken;
    private int _backoffAttempt;
    private ITimer? _faultAfterTimer;
    private int _faultAfterToken;
    private bool _describedOnce;
    private DateTimeOffset _lastDescribe;
    private bool _flooding;

    // Read from any thread.
    private ConnectionStatus _status;
    private Instance? _running;
    private int _restartCount;
    private long _lastConnectedTicks;

    public ConnectionRuntime(Connection connection, ConnectionRuntimeServices services, object sender)
    {
        _connection = connection;
        _services = services;
        _options = services.Options;
        _time = services.Time;
        _logger = services.Logger;
        _sender = sender;
        _state = connection.Enabled ? ConnectionState.Configured : ConnectionState.Disabled;
        _status = new ConnectionStatus(connection.Id, _state, null, _time.GetUtcNow(), 0, null, ConnectionCounters.Zero);
        _records = new SerialQueue(_logger, connection.Id.Value);
        _store = new ConnectionStore(connection.Id, services.StoreRepository, _logger);
        _loop = Task.Run(RunMailboxAsync);
    }

    public Connection Connection => Volatile.Read(ref _connection);

    public ConnectionCounterSource Counters { get; } = new();

    private DateTimeOffset? LastConnected => Interlocked.Read(ref _lastConnectedTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public ConnectionStatus Status =>
        Volatile.Read(ref _status) with { RestartCount = Volatile.Read(ref _restartCount), LastConnectedAt = LastConnected, Counters = Counters.Snapshot() };

    /// <summary>Gets the instance that accepts commands and inventory calls (state <c>Connected</c> or <c>Degraded</c>), if any.</summary>
    internal Instance? Running => Volatile.Read(ref _running);

    /// <summary>Gets the current instance, whatever its state (tests, watchdog).</summary>
    internal Instance? Current { get; private set; }

    // ---- Operations (each completes once the mailbox processed it) ----

    /// <summary>The runtime may run connectors (start-up phase <c>Connections</c>).</summary>
    public Task ActivateAsync() => Invoke(() =>
    {
        _activated = true;
        Converge();
    });

    public Task StartAsync() => Invoke(() =>
    {
        _connection = _connection with { Enabled = true };
        _blocked = false;
        CancelRestartTimer();
        Converge();
    });

    /// <summary>Disables the connection; completes when its connector is stopped (or abandoned).</summary>
    public async Task StopAsync(KernelMessage? reason = null)
    {
        var stopped = await InvokeAsync(() =>
        {
            _connection = _connection with { Enabled = false };
            _stopReason = reason;
            Converge();
            return WhenStopped();
        }).ConfigureAwait(false);
        await stopped.ConfigureAwait(false);
    }

    /// <summary>Restarts the connector with a new instance; completes when the restart began.</summary>
    public Task RestartAsync() => Invoke(() =>
    {
        _blocked = false;
        CancelRestartTimer();
        if (_instance is not null)
        {
            _restartRequested = true;
            _stopReason = KernelMessage.Of("irt.connection.restart");
        }

        Converge();
    });

    /// <summary>Takes a new configuration; with <paramref name="restart"/>, a running connector restarts with it.</summary>
    public Task ReconfigureAsync(Connection connection, bool restart) => Invoke(() =>
    {
        _connection = connection;
        if (restart)
        {
            _blocked = false;
            _describedOnce = false;
            CancelRestartTimer();
            if (_instance is not null)
            {
                _restartRequested = true;
                _stopReason = KernelMessage.Of("irt.connection.settingsChanged");
            }
        }

        Converge();
    });

    /// <summary>The package version of the connection became unavailable (disabled, removed) or available again.</summary>
    public async Task SetPackageAvailableAsync(bool available, string reason)
    {
        var stopped = await InvokeAsync(() =>
        {
            _packageAvailable = available;
            _stopReason = available ? null : KernelMessage.Of("irt.connection." + reason);
            if (!available)
            {
                CancelRestartTimer();
            }

            Converge();
            return WhenStopped();
        }).ConfigureAwait(false);
        if (!available)
        {
            await stopped.ConfigureAwait(false);
        }
    }

    /// <summary>Stops the connector for a node shutdown (the connection stays enabled).</summary>
    public async Task ShutdownAsync()
    {
        var stopped = await InvokeAsync(() =>
        {
            _activated = false;
            _stopReason = KernelMessage.Of("irt.connection.shutdown");
            CancelRestartTimer();
            Converge();
            return WhenStopped();
        }).ConfigureAwait(false);
        await stopped.ConfigureAwait(false);
        await _records.DrainAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the connector before the connection is deleted.</summary>
    public async Task DeleteAsync()
    {
        var stopped = await InvokeAsync(() =>
        {
            _deleting = true;
            _connection = _connection with { Enabled = false };
            _stopReason = KernelMessage.Of("irt.connection.deleted");
            CancelRestartTimer();
            Converge();
            return WhenStopped();
        }).ConfigureAwait(false);
        await stopped.ConfigureAwait(false);
        await _records.DrainAsync().ConfigureAwait(false);
    }

    /// <summary>The ingest's flooding signal for this connection (fast, thread-safe).</summary>
    public void OnFlooding(bool flooding, int pending) => Post("flooding", () =>
    {
        IntegrationLog.Flooding(_logger, _connection.Id.Value, flooding, pending);
        _flooding = flooding;
        var sink = _instance?.Services?.Sink;
        if (sink is not null)
        {
            sink.Flooding = flooding;
        }

        Transition(_state, flooding ? KernelMessage.Of("irt.connection.flooding", ("pending", pending)) : _instance is null ? _reason : null);
        var id = _connection.Id;
        _records.Post(() => _services.Recorder.ReportRootHealthAsync(id, native => sink?.LastServerConnectivity(native) ?? Connectivity.Online,
            flooding ? [ObservationSink.FloodingFault] : [], flooding ? "irt.connection.flooding" : null));
    });

    /// <summary>The registry changed: rebuilds the device map of the running instance.</summary>
    public void RefreshDeviceMap() => Post("device map", () =>
    {
        if (_instance is { Services: not null } instance)
        {
            RebuildMap(instance);
        }
    });

    /// <summary>The periodic watchdog check (spec IRT §7.3): hung commands, silence, describe interval.</summary>
    public void Check() => Post("watchdog", () =>
    {
        if (_instance is not { } instance)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (instance.Commands.FirstOrDefault(c => now > c.Value + _options.HungCommandGrace) is { Key: var hung } && hung != Guid.Empty)
        {
            IntegrationLog.HungCommand(_logger, _connection.Id.Value, hung);
            _restartRequested = true;
            _stopReason = KernelMessage.Of("irt.connection.hungCommand", ("command", hung.ToString("D")));
            Converge();
            return;
        }

        if (_state == ConnectionState.Connected && instance.Services is { } services)
        {
            var last = Math.Max(Interlocked.Read(ref instance.LastActivity), services.Sink.LastReport);
            var silence = _time.GetElapsedTime(last);
            var silenceLimit = instance.Hosted?.Limits?.SilenceTimeout ?? _options.SilenceTimeout;
            if (silence < silenceLimit)
            {
                instance.SilenceWarned = false;
            }
            else if (!instance.SilenceWarned)
            {
                instance.SilenceWarned = true;
                IntegrationLog.SilentConnector(_logger, _connection.Id.Value, silence);
            }
        }

        if (_state is ConnectionState.Connected or ConnectionState.Degraded && _describedOnce && now - _lastDescribe >= _connection.DescribeInterval)
        {
            TriggerReconcile(instance, null);
        }
    });

    // ---- Calls on the running connector ----

    /// <summary>Describes the inventory now and reconciles it (manual rediscovery).</summary>
    public async Task<Result<ReconciliationResult>> DescribeAndReconcileAsync(CancellationToken ct)
    {
        if (Running is not { } instance)
        {
            return IntegrationErrors.NotRunning(_connection.Id, ConnectionColumns.StateText(Status.State));
        }

        return await ReconcileAsync(instance, null, ct).ConfigureAwait(false);
    }

    /// <summary>Calls the running connector, bounded by <paramref name="timeout"/>; an exception or a hang is a connector fault.</summary>
    public async Task<Result<T>> CallAsync<T>(string operation, Func<IConnector, CancellationToken, Task<T>> call, TimeSpan timeout, CancellationToken ct)
    {
        if (Running is not { Hosted: { } hosted } instance)
        {
            return IntegrationErrors.NotRunning(_connection.Id, ConnectionColumns.StateText(Status.State));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, instance.Stopping.Token);
        try
        {
            return await ConnectorCall.RunAsync(() => call(hosted.Connector, linked.Token), timeout, _time).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return IntegrationErrors.NotRunning(_connection.Id, ConnectionColumns.StateText(ConnectionState.Stopping));
        }
        catch (Exception ex)
        {
            Fault(instance, ex, operation);
            return Error.Internal(operation + " failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Executes a command (spec IRT §7.5): cancelled at its deadline, counted, a connector exception faults the connection.</summary>
    public async ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var started = _time.GetTimestamp();
        var result = await ExecuteCoreAsync(command, ct).ConfigureAwait(false);
        Counters.CountCommand(result.Outcome == CommandOutcome.Succeeded, _time.GetElapsedTime(started));
        return result;
    }

    public async ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(IReadOnlyList<ConnectorCommand> commands, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            return [];
        }

        if (Running is not { Hosted.Connector: IBatchCommandExecutor batch } instance || !TryResolveAll(instance, commands, out var resolved))
        {
            var results = new List<ConnectorCommandResult>(commands.Count);
            foreach (var command in commands)
            {
                results.Add(await ExecuteAsync(command, ct).ConfigureAwait(false));
            }

            return results;
        }

        var started = _time.GetTimestamp();
        var deadline = commands.Min(c => c.Deadline);
        var outcome = await GuardedAsync(instance, commands.Select(c => c.CommandId).ToList(), deadline,
            token => batch.ExecuteBatchAsync(resolved, token).AsTask(), ct).ConfigureAwait(false);
        IReadOnlyList<ConnectorCommandResult> all = outcome.Results ?? [.. commands.Select(_ => outcome.Failure!)];
        foreach (var result in all)
        {
            Counters.CountCommand(result.Outcome == CommandOutcome.Succeeded, _time.GetElapsedTime(started));
        }

        return all;
    }

    public bool SupportsBatch => Running?.Hosted?.Connector is IBatchCommandExecutor;

    /// <summary>Completes when the transitions so far are recorded (StatusChanged raised, timeline row, WP_SYS record).</summary>
    internal async Task RecordedAsync()
    {
        await Invoke(() => { }).ConfigureAwait(false);
        await _records.DrainAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        await Task.WhenAll(_background.Keys).ConfigureAwait(false);
        await _teardown.ConfigureAwait(false);
        await _records.DisposeAsync().ConfigureAwait(false);
        _restartTimer?.Dispose();
        _faultAfterTimer?.Dispose();
        _store.Dispose();
        _inventoryGate.Dispose();
    }

    // ---- Mailbox ----

    private Task Invoke(Action action) => InvokeAsync(() =>
    {
        action();
        return Task.CompletedTask;
    }).Unwrap();

    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_mailbox.Writer.TryWrite(() =>
        {
            try
            {
                done.TrySetResult(action());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
                throw;
            }
        }))
        {
            done.TrySetException(new ObjectDisposedException(nameof(ConnectionRuntime)));
        }

        return done.Task;
    }

    private void Post(string operation, Action action) =>
        _mailbox.Writer.TryWrite(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                IntegrationLog.RuntimeOperationFailed(_logger, ex, _connection.Id.Value, operation);
            }
        });

    private async Task RunMailboxAsync()
    {
        await foreach (var action in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                IntegrationLog.RuntimeOperationFailed(_logger, ex, _connection.Id.Value, "operation");
            }
        }
    }

    // ---- State machine (mailbox only) ----

    private void Converge()
    {
        if (_state == ConnectionState.Stopping)
        {
            return;
        }

        if (_instance is not null)
        {
            if (!_connection.Enabled || _restartRequested || _deleting || !_packageAvailable || !_activated)
            {
                BeginTeardown(stopping: true);
            }

            return;
        }

        if (_tearingDown is not null)
        {
            return;
        }

        if (_connection.Enabled && _activated && !_blocked && _packageAvailable && !_deleting && _restartTimer is null)
        {
            BeginStart();
            return;
        }

        var target = _connection.Enabled && !_deleting ? ConnectionState.Configured : ConnectionState.Disabled;
        if (_state != ConnectionState.Faulted || target == ConnectionState.Disabled || !_packageAvailable || !_activated)
        {
            if (_state != target)
            {
                CancelRestartTimer();
                Transition(target, _stopReason);
            }
        }

        CompleteStopWaiters();
    }

    private void BeginStart()
    {
        var instance = new Instance(++_generation, _connection);
        _instance = instance;
        Current = instance;
        _stopReason = null;
        Transition(ConnectionState.Starting, null);
        var previous = _teardown;
        instance.Launch = Track(LaunchAsync(instance, previous));
    }

    private void BeginTeardown(bool stopping)
    {
        var instance = _instance!;
        _instance = null;
        Volatile.Write(ref _running, null);
        CancelFaultAfter();
        _tearingDown = instance;
        if (stopping)
        {
            Transition(ConnectionState.Stopping, _stopReason);
        }

        _teardown = Track(TeardownAsync(instance));
    }

    private void OnTornDown(Instance instance)
    {
        if (ReferenceEquals(_tearingDown, instance))
        {
            _tearingDown = null;
        }

        if (_state == ConnectionState.Stopping)
        {
            _restartRequested = false;
            Transition(_connection.Enabled && !_deleting ? ConnectionState.Configured : ConnectionState.Disabled, _stopReason);
        }

        // A stop is done when the teardown it caused is done, even if a later start runs the connection again.
        if (_tearingDown is null)
        {
            _stopWaiters.ForEach(w => w.TrySetResult());
            _stopWaiters.Clear();
        }

        Converge();
    }

    private void OnHealth(Instance instance, ConnectorStatus status, ConnectorMessage? reason)
    {
        if (!ReferenceEquals(instance, _instance))
        {
            return;
        }

        var message = reason?.ToMessage(_connection.PackageId.Value);
        var services = instance.Services!;
        switch (status)
        {
            case ConnectorStatus.Connected or ConnectorStatus.Degraded:
                {
                    var next = status == ConnectorStatus.Connected ? ConnectionState.Connected : ConnectionState.Degraded;
                    if (_state == next && message == _reason)
                    {
                        return;
                    }

                    var firstConnect = _state is ConnectionState.Starting or ConnectionState.Reconnecting or ConnectionState.Faulted;
                    CancelFaultAfter();
                    _backoffAttempt = 0;
                    Interlocked.Exchange(ref _lastConnectedTicks, _time.GetUtcNow().UtcTicks);
                    Transition(next, _flooding ? _reason : message);
                    services.Scheduler.SetConnected(true);
                    Volatile.Write(ref _running, instance);
                    if (firstConnect)
                    {
                        OnConnected(instance);
                    }

                    break;
                }

            default:
                if (_state is ConnectionState.Connected or ConnectionState.Degraded
                    || (_state == ConnectionState.Starting && status == ConnectorStatus.Disconnected))
                {
                    Volatile.Write(ref _running, null);
                    services.Scheduler.SetConnected(false);
                    Transition(ConnectionState.Reconnecting, message);
                    StartFaultAfter(instance);
                }

                break;
        }
    }

    private void OnConnected(Instance instance)
    {
        if (!_describedOnce || _time.GetUtcNow() - _lastDescribe >= _connection.DescribeInterval)
        {
            TriggerReconcile(instance, null);
        }

        // ConnectorBase refreshes and catches up by itself (INT §9).
        if (instance.Hosted?.Connector is not ConnectorBase and var connector && connector is IStateRefresh or IEventCatchUp)
        {
            Track(RefreshAsync(instance, connector));
        }
    }

    private void OnLaunchRefused(Instance instance, Error error)
    {
        if (!ReferenceEquals(instance, _instance))
        {
            return;
        }

        IntegrationLog.ConnectorInitializeRefused(_logger, _connection.Id.Value, error.Code.Value);
        _blocked = true;
        BeginTeardown(stopping: false);
        Transition(ConnectionState.Faulted, KernelMessage.Of("irt.connection.initializeRefused", ("code", error.Code.Value)));
    }

    /// <summary>A connector call threw or hung (L4): Faulted, the instance is disposed and a restart is scheduled with back-off.</summary>
    private void Fault(Instance instance, Exception exception, string operation) => Post("fault", () =>
    {
        if (!ReferenceEquals(instance, _instance))
        {
            return;
        }

        IntegrationLog.ConnectorFaulted(_logger, _connection.Id.Value, operation + ": " + exception.GetType().FullName + ": " + exception.Message);
        BeginTeardown(stopping: false);
        var attempt = ++_backoffAttempt;
        var delay = Backoff(attempt);
        var token = ++_restartToken;
        _restartTimer?.Dispose();
        _restartTimer = _time.CreateTimer(_ => Post("restart timer", () => OnRestartTimer(token)), null, delay, Timeout.InfiniteTimeSpan);
        IntegrationLog.RestartScheduled(_logger, _connection.Id.Value, delay, attempt);
        Transition(ConnectionState.Faulted, KernelMessage.Of("irt.connection.faulted", ("operation", operation), ("exception", exception.GetType().Name)));
    });

    internal TimeSpan Backoff(int attempt)
    {
        var max = _options.MaxRestartBackoff;
        var initial = _options.InitialRestartBackoff;
        var factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(initial.Ticks * factor, max.Ticks));
        var jitter = 1 + (((_services.Random() * 2) - 1) * _options.RestartJitter);
        return TimeSpan.FromTicks((long)Math.Min(delay.Ticks * jitter, max.Ticks));
    }

    private void OnRestartTimer(int token)
    {
        if (token != _restartToken || _restartTimer is null)
        {
            return;
        }

        _restartTimer.Dispose();
        _restartTimer = null;
        Interlocked.Increment(ref _restartCount);
        Converge();
    }

    private void CancelRestartTimer()
    {
        _restartToken++;
        _restartTimer?.Dispose();
        _restartTimer = null;
    }

    private void StartFaultAfter(Instance instance)
    {
        CancelFaultAfter();
        var token = _faultAfterToken;
        _faultAfterTimer = _time.CreateTimer(_ => Post("fault after", () =>
        {
            if (token == _faultAfterToken && ReferenceEquals(instance, _instance) && _state == ConnectionState.Reconnecting)
            {
                // Display only: the connector keeps trying (spec IRT §7.2).
                Transition(ConnectionState.Faulted, KernelMessage.Of("irt.connection.reconnectTimeout"));
            }
        }), null, _options.FaultAfter, Timeout.InfiniteTimeSpan);
    }

    private void CancelFaultAfter()
    {
        _faultAfterToken++;
        _faultAfterTimer?.Dispose();
        _faultAfterTimer = null;
    }

    private void Transition(ConnectionState next, KernelMessage? reason)
    {
        if (next == _state && Equals(reason, _reason))
        {
            return;
        }

        var previous = _state;
        _state = next;
        _reason = reason;
        var status = new ConnectionStatus(_connection.Id, next, reason, _time.GetUtcNow(), Volatile.Read(ref _restartCount),
            LastConnected, ConnectionCounters.Zero);
        Volatile.Write(ref _status, status);
        var transition = new ConnectionTransition(_connection, previous, status with { Counters = Counters.Snapshot() });
        _records.Post(() => _services.Recorder.RecordAsync(transition, _sender));
    }

    private Task WhenStopped()
    {
        if (_instance is null && _tearingDown is null && _state != ConnectionState.Stopping)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopWaiters.Add(done);
        return done.Task;
    }

    private void CompleteStopWaiters()
    {
        if (_instance is null && _tearingDown is null && _state != ConnectionState.Stopping)
        {
            _stopWaiters.ForEach(w => w.TrySetResult());
            _stopWaiters.Clear();
        }
    }

    // ---- Background work (never on the mailbox) ----

    private Task Track(Task task)
    {
        _background.TryAdd(task, 0);
        _ = task.ContinueWith(t =>
        {
            _background.TryRemove(t, out _);
            if (t.IsFaulted)
            {
                // No fire-and-forget without a trace: every background failure is logged.
                IntegrationLog.RuntimeOperationFailed(_logger, t.Exception!.GetBaseException(), _connection.Id.Value, "background work");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task LaunchAsync(Instance instance, Task previous)
    {
        await previous.ConfigureAwait(false);
        var ct = instance.Stopping.Token;
        var connection = instance.Connection;
        try
        {
            if (!_services.Packages.GetSettingsSchema(connection.PackageId, connection.PackageVersion, connection.ServerType).TryGetValue(out var schema, out var error)
                || !(await ConnectorCall.RunAsync(() => Task.FromResult(_services.Host.Create(connection)), _options.InitializeTimeout, _time).ConfigureAwait(false))
                    .TryGetValue(out var hosted, out error))
            {
                Post("launch", () => OnLaunchRefused(instance, error!));
                return;
            }

            lock (instance)
            {
                if (instance.Abandoned)
                {
                    // The teardown gave up waiting for this launch: nobody else releases the instance.
                    _ = Track(DisposeLateAsync(hosted));
                    return;
                }

                instance.Hosted = hosted;
                instance.Services = _services.Contexts.Create(connection, schema, hosted, _store, Counters,
                    (status, reason) =>
                    {
                        Interlocked.Exchange(ref instance.LastActivity, _time.GetTimestamp());
                        Post("health", () => OnHealth(instance, status, reason));
                    },
                    snapshot => Track(ReconcileAsync(instance, snapshot, CancellationToken.None)),
                    ct);
                instance.InitializeCalled = true;
            }

            var initialized = await ConnectorCall.RunAsync(() => hosted.Connector.InitializeAsync(instance.Services.Context, ct).AsTask(), _options.InitializeTimeout, _time)
                .ConfigureAwait(false);
            if (!initialized.IsSuccess)
            {
                Post("launch", () => OnLaunchRefused(instance, initialized.Error));
                return;
            }

            try
            {
                await ConnectorCall.RunAsync(async () =>
                {
                    await hosted.Connector.StartAsync(ct).ConfigureAwait(false);
                    return true;
                }, _options.StartTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                IntegrationLog.HungStart(_logger, connection.Id.Value, _options.StartTimeout);
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped while starting.
        }
        catch (Exception ex)
        {
            Fault(instance, ex, "start");
        }
    }

    private async Task TeardownAsync(Instance instance)
    {
        try
        {
            await instance.Stopping.CancelAsync().ConfigureAwait(false);
            var abandoned = false;
            try
            {
                await instance.Launch.WaitAsync(_options.StopTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                abandoned = true;
            }

            HostedConnector? hosted;
            InstanceServices? services;
            bool initialized;
            lock (instance)
            {
                instance.Abandoned = abandoned;
                hosted = instance.Hosted;
                services = instance.Services;
                initialized = instance.InitializeCalled;
            }

            if (!abandoned && hosted is not null && initialized)
            {
                abandoned = !await BoundedAsync(token => hosted.Connector.StopAsync(token).AsTask(), "stop").ConfigureAwait(false);
            }

            if (!abandoned && hosted is not null)
            {
                abandoned = !await BoundedAsync(_ => hosted.Connector.DisposeAsync().AsTask(), "dispose").ConfigureAwait(false);
            }

            if (abandoned)
            {
                IntegrationLog.ConnectorAbandoned(_logger, instance.Connection.Id.Value);
            }

            if (services is not null)
            {
                await services.ReleaseAsync(_sinkDrain).ConfigureAwait(false);
            }

            // Released even when abandoned: an in-process instance that hangs keeps its load context alive by itself.
            hosted?.Release?.Dispose();
        }
        catch (Exception ex)
        {
            IntegrationLog.RuntimeOperationFailed(_logger, ex, instance.Connection.Id.Value, "teardown");
        }
        finally
        {
            Post("torn down", () => OnTornDown(instance));
        }
    }

    /// <summary>Runs a stop or dispose call within the stop timeout; <see langword="false"/> when it did not complete in time.</summary>
    private async Task<bool> BoundedAsync(Func<CancellationToken, Task> call, string operation)
    {
        using var timeout = new CancellationTokenSource(_options.StopTimeout, _time);
        try
        {
            await ConnectorCall.RunAsync(async () =>
            {
                await call(timeout.Token).ConfigureAwait(false);
                return true;
            }, _options.StopTimeout, _time).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            IntegrationLog.ConnectorFaulted(_logger, _connection.Id.Value, operation + ": " + ex.GetType().FullName + ": " + ex.Message);
            return true;
        }
    }

    private async Task DisposeLateAsync(HostedConnector hosted)
    {
        await BoundedAsync(_ => hosted.Connector.DisposeAsync().AsTask(), "dispose").ConfigureAwait(false);
        hosted.Release?.Dispose();
    }

    private void TriggerReconcile(Instance instance, InventorySnapshot? snapshot)
    {
        _describedOnce = true;
        _lastDescribe = _time.GetUtcNow();
        Track(ReconcileWithRetryAsync(instance, snapshot));
    }

    /// <summary>An automatic describe and reconcile; when it fails, the watchdog tries again after <see cref="ReconcileRetry"/>.</summary>
    private async Task ReconcileWithRetryAsync(Instance instance, InventorySnapshot? snapshot)
    {
        var result = await ReconcileAsync(instance, snapshot, CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Post("reconcile retry", () =>
            {
                if (ReferenceEquals(instance, _instance))
                {
                    _lastDescribe = _time.GetUtcNow() - _connection.DescribeInterval + ReconcileRetry;
                }
            });
        }
    }

    /// <summary>The delay before a failed automatic reconciliation is tried again.</summary>
    internal static readonly TimeSpan ReconcileRetry = TimeSpan.FromSeconds(30);

    /// <summary>Describes (unless a snapshot is given) and reconciles; one at a time per connection.</summary>
    private async Task<Result<ReconciliationResult>> ReconcileAsync(Instance instance, InventorySnapshot? snapshot, CancellationToken ct)
    {
        try
        {
            await _inventoryGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return IntegrationErrors.NotRunning(_connection.Id, "deleted");
        }

        try
        {
            if (snapshot is null)
            {
                if (instance.Hosted is not { } hosted)
                {
                    return IntegrationErrors.NotRunning(_connection.Id, ConnectionColumns.StateText(Status.State));
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, instance.Stopping.Token);
                try
                {
                    snapshot = await ConnectorCall.RunAsync(() => hosted.Connector.DescribeAsync(linked.Token).AsTask(), _options.DescribeTimeout, _time)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return IntegrationErrors.NotRunning(_connection.Id, ConnectionColumns.StateText(ConnectionState.Stopping));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fault(instance, ex, "describe");
                    return Error.Internal("describe failed: " + ex.GetType().Name);
                }
            }

            Result<ReconciliationResult> result;
            try
            {
                result = await _services.Inventory.ReconcileAsync(Connection, snapshot, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                IntegrationLog.RuntimeOperationFailed(_logger, ex, _connection.Id.Value, "reconcile");
                return Error.Internal("reconcile failed: " + ex.GetType().Name);
            }

            if (result.IsSuccess && instance.Services is not null)
            {
                RebuildMap(instance);
            }

            return result;
        }
        finally
        {
            _inventoryGate.Release();
        }
    }

    private async Task RefreshAsync(Instance instance, IConnector connector)
    {
        try
        {
            var ct = instance.Stopping.Token;
            if (connector is IStateRefresh refresh)
            {
                await ConnectorCall.RunAsync(async () =>
                {
                    await refresh.RefreshAsync(RefreshScope.All, ct).ConfigureAwait(false);
                    return true;
                }, _options.DescribeTimeout, _time).ConfigureAwait(false);
            }

            if (connector is IEventCatchUp catchUp)
            {
                await ConnectorCall.RunAsync(async () =>
                {
                    await catchUp.CatchUpAsync(ct).ConfigureAwait(false);
                    return true;
                }, _options.DescribeTimeout, _time).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (instance.Stopping.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            Fault(instance, ex, "refresh");
        }
    }

    /// <summary>Reloads the device map off the mailbox; concurrent requests coalesce into one more reload.</summary>
    private void RebuildMap(Instance instance)
    {
        if (Interlocked.Exchange(ref instance.MapPending, 1) == 1)
        {
            return;
        }

        Track(Task.Run(() =>
        {
            while (Interlocked.Exchange(ref instance.MapPending, 0) == 1 && !instance.Stopping.IsCancellationRequested)
            {
                try
                {
                    instance.Services!.Map.Update(DeviceMapSnapshot.Load(_services.Registry, _connection.Id));
                }
                catch (Exception ex)
                {
                    IntegrationLog.DeviceMapFailed(_logger, ex, _connection.Id.Value);
                }
            }
        }));
    }

    private async Task<ConnectorCommandResult> ExecuteCoreAsync(ConnectorCommand command, CancellationToken ct)
    {
        if (Running is not { Hosted: { } hosted, Services: { } services } instance)
        {
            return ConnectorCommandResult.Failed(FailureCategory.ConnectionDown, ConnectorMessage.Of("connectionDown"));
        }

        if (hosted.Connector is not ICommandExecutor executor)
        {
            return ConnectorCommandResult.NotSupported();
        }

        if (!services.Map.TryGet(command.Target.Key, out var handle))
        {
            return ConnectorCommandResult.Failed(FailureCategory.InvalidState, ConnectorMessage.Of("deviceNotImported"));
        }

        var outcome = await GuardedAsync(instance, [command.CommandId], command.Deadline,
            async token => (IReadOnlyList<ConnectorCommandResult>)[await executor.ExecuteAsync(command with { Target = handle }, token).ConfigureAwait(false)], ct)
            .ConfigureAwait(false);
        return outcome.Results?[0] ?? outcome.Failure!;
    }

    private static bool TryResolveAll(Instance instance, IReadOnlyList<ConnectorCommand> commands, out List<ConnectorCommand> resolved)
    {
        resolved = new List<ConnectorCommand>(commands.Count);
        foreach (var command in commands)
        {
            if (instance.Services is null || !instance.Services.Map.TryGet(command.Target.Key, out var handle))
            {
                return false;
            }

            resolved.Add(command with { Target = handle });
        }

        return true;
    }

    /// <summary>
    /// Calls the connector through its request gate, cancelled at the deadline. The caller gets its answer at the
    /// deadline even if the connector ignores cancellation; the call stays tracked until it really ends, so the
    /// watchdog restarts a connector whose call hangs past deadline + 30 s.
    /// </summary>
    private async Task<(IReadOnlyList<ConnectorCommandResult>? Results, ConnectorCommandResult? Failure)> GuardedAsync(Instance instance,
        IReadOnlyList<Guid> ids, DateTimeOffset deadline, Func<CancellationToken, Task<IReadOnlyList<ConnectorCommandResult>>> call, CancellationToken ct)
    {
        var remaining = deadline - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return (null, ConnectorCommandResult.Failed(FailureCategory.Timeout, ConnectorMessage.Of("deadlineExceeded")));
        }

        using var atDeadline = new CancellationTokenSource(remaining, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, atDeadline.Token, instance.Stopping.Token);
        foreach (var id in ids)
        {
            instance.Commands[id] = deadline;
        }

        var token = linked.Token;
        var task = Task.Run(() => instance.Services!.Gate.RunAsync(CommandPriority.Normal, async t => await call(t).ConfigureAwait(false), token).AsTask(), CancellationToken.None);
        _ = task.ContinueWith(_ =>
        {
            foreach (var id in ids)
            {
                instance.Commands.TryRemove(id, out DateTimeOffset _);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return (await task.WaitAsync(token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (atDeadline.IsCancellationRequested)
        {
            return (null, ConnectorCommandResult.Failed(FailureCategory.Timeout, ConnectorMessage.Of("deadlineExceeded")));
        }
        catch (OperationCanceledException)
        {
            return (null, ConnectorCommandResult.Failed(FailureCategory.ConnectionDown, ConnectorMessage.Of("connectionDown")));
        }
        catch (Exception ex)
        {
            Fault(instance, ex, "command");
            return (null, ConnectorCommandResult.Failed(FailureCategory.InternalError, ConnectorMessage.Of("connectorError")));
        }
    }

    /// <summary>One connector instance and its services.</summary>
    internal sealed class Instance(int generation, Connection connection)
    {
        public int Generation { get; } = generation;

        public Connection Connection { get; } = connection;

        public CancellationTokenSource Stopping { get; } = new();

        public Task Launch { get; set; } = Task.CompletedTask;

        public HostedConnector? Hosted { get; set; }

        public InstanceServices? Services { get; set; }

        public bool InitializeCalled { get; set; }

        public bool Abandoned { get; set; }

        public bool SilenceWarned { get; set; }

        public ConcurrentDictionary<Guid, DateTimeOffset> Commands { get; } = new();

#pragma warning disable SA1401 // Fields used with Interlocked.
        public long LastActivity;

        public int MapPending;
#pragma warning restore SA1401
    }
}

/// <summary>Runs connector code off the caller's thread, bounded by a timeout measured with the node's <see cref="TimeProvider"/>.</summary>
internal static class ConnectorCall
{
    /// <summary>Runs <paramref name="call"/>; throws <see cref="TimeoutException"/> when it has not completed within <paramref name="timeout"/> (the call goes on, abandoned).</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> call, TimeSpan timeout, TimeProvider time)
    {
        // Task.Run: a connector that blocks synchronously before its first await is bounded too.
        var task = Task.Run(call);
        try
        {
            return await task.WaitAsync(timeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Observe a late failure of the abandoned call.
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }
}
