using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Context;
using Watchpoint.Integrations.Internal.Hosting;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Integrations.Internal.Connections;

/// <summary>M1 admission: every connection is allowed (LIC adds the feature check in M7).</summary>
internal sealed class AllowAllConnectionAdmission : IConnectionAdmission
{
    public Result Admit(PackageInfo package, string serverType) => Result.Success();
}

/// <summary>
/// The connection manager (spec IRT §7): configuration and secrets (serialised per connection, persisted with
/// optimistic concurrency), one <see cref="ConnectionRuntime"/> per connection, staggered activation at the
/// <c>Connections</c> start-up phase, the watchdog, and the hooks from the package catalogue, the registry and the ingest.
/// </summary>
internal sealed class ConnectionManager : IConnectionManager, IConnectionCommandChannel, IPackageConnections, IIngestFloodingListener, IAsyncDisposable
{
    public const int MaxNameLength = 128;
    public static readonly TimeSpan MaxProtocolTrace = TimeSpan.FromHours(1);
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<ConnectionId, ConnectionRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<ConnectionId, SemaphoreSlim> _locks = new();
    private readonly IConnectionRepository _repository;
    private readonly IPackageCatalog _packages;
    private readonly IConnectionHost _host;
    private readonly ISecretVault _vault;
    private readonly IRegistryReader _registry;
    private readonly IRegistryWriter _registryWriter;
    private readonly IConnectionAdmission _admission;
    private readonly ConnectorContextFactory _contexts;
    private readonly ConnectionRuntimeServices _services;
    private readonly ConnectionsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly List<ITimer> _staggerTimers = [];
    private readonly CancellationTokenSource _shutdown = new();
    private ITimer? _watchdog;
    private DateTimeOffset _lastPurge;
    private bool _loaded;
    private bool _activated;
    private int _disposed;

    public ConnectionManager(IConnectionRepository repository, IConnectionStoreRepository storeRepository, IPackageCatalog packages, IConnectionHost host,
        ISecretVault vault, IRegistryReader registry, IRegistryWriter registryWriter, IConnectionAdmission admission, ConnectorContextFactory contexts,
        InventoryCoordinator inventory, ConnectionStateRecorder recorder, ConnectionsOptions options, TimeProvider time, ILogger<ConnectionManager> logger,
        Func<double>? random = null)
    {
        _repository = repository;
        _packages = packages;
        _host = host;
        _vault = vault;
        _registry = registry;
        _registryWriter = registryWriter;
        _admission = admission;
        _contexts = contexts;
        _options = options;
        _time = time;
        _logger = logger;
        _services = new ConnectionRuntimeServices
        {
            Host = host,
            Packages = packages,
            Contexts = contexts,
            StoreRepository = storeRepository,
            Inventory = inventory,
            Recorder = recorder,
            Registry = registry,
            Options = options,
            Time = time,
            Logger = logger,
            Random = random ?? Random.Shared.NextDouble,
        };
        recorder.StatusChanged += (sender, args) => StatusChanged?.Invoke(this, args);
        packages.Changed += OnPackageChanged;
        registry.Changed += OnRegistryChanged;
    }

    public event EventHandler<ConnectionStatusChangedEventArgs>? StatusChanged;

    internal IEnumerable<ConnectionRuntime> Runtimes => _runtimes.Values;

    internal ConnectionRuntime? Runtime(ConnectionId id) => _runtimes.TryGetValue(id, out var runtime) ? runtime : null;

    /// <summary>Loads the stored connections (start-up phase <c>Catalogue</c>, after the package scan); idempotent.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var connection in await _repository.ListAsync(ct).ConfigureAwait(false))
            {
                var runtime = new ConnectionRuntime(connection, _services, this);
                _runtimes[connection.Id] = runtime;
                if (!IsPackageActive(connection))
                {
                    IntegrationLog.ConnectionLoadFailed(_logger, connection.Id.Value, $"package {connection.PackageId}@{connection.PackageVersion} is not active");
                    await runtime.SetPackageAvailableAsync(false, "packageUnavailable").ConfigureAwait(false);
                }
            }

            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Start-up phase <c>Connections</c>: enabled connections start one <c>Connections:StartStagger</c> apart (0, 200,
    /// 400 ms…), the watchdog starts. Returns once the starts are scheduled.
    /// </summary>
    public async Task ActivateAsync(CancellationToken ct)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        lock (_staggerTimers)
        {
            if (_activated)
            {
                return;
            }

            _activated = true;
        }

        var index = 0;
        foreach (var runtime in _runtimes.Values.OrderBy(r => r.Connection.Name.Default, StringComparer.Ordinal).ThenBy(r => r.Connection.Id.Value))
        {
            var delay = runtime.Connection.Enabled ? _options.StartStagger * index++ : TimeSpan.Zero;
            if (delay <= TimeSpan.Zero)
            {
                await runtime.ActivateAsync().ConfigureAwait(false);
                continue;
            }

            lock (_staggerTimers)
            {
                _staggerTimers.Add(_time.CreateTimer(_ => Observe(runtime.ActivateAsync(), runtime.Connection.Id, "activate"), null, delay, Timeout.InfiniteTimeSpan));
            }
        }

        _lastPurge = _time.GetUtcNow();
        _watchdog = _time.CreateTimer(_ => Watchdog(), null, _options.WatchdogInterval, _options.WatchdogInterval);
    }

    public IReadOnlyList<Connection> List() => [.. _runtimes.Values.Select(r => r.Connection).OrderBy(c => c.Name.Default, StringComparer.Ordinal)];

    public Result<Connection> Get(ConnectionId id) =>
        _runtimes.TryGetValue(id, out var runtime) ? runtime.Connection : IntegrationErrors.ConnectionNotFoundError(id);

    public ConnectionStatus GetStatus(ConnectionId id) =>
        _runtimes.TryGetValue(id, out var runtime)
            ? runtime.Status
            : new ConnectionStatus(id, ConnectionState.Disabled, null, _time.GetUtcNow(), 0, null, ConnectionCounters.Zero);

    public async Task<Result<ConnectionId>> CreateAsync(NewConnection request, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ctx);
        await LoadAsync(ct).ConfigureAwait(false);
        var package = ResolvePackage(request.PackageId, request.PackageVersion);
        if (!package.TryGetValue(out var info, out var error))
        {
            return error;
        }

        var fields = new List<FieldError>();
        fields.AddRange(NameRules.Validate(request.Name.Default, "/name"));
        var schema = CheckConnector(info, request.ServerType, fields);
        CheckSettings(schema, request.Settings, fields);
        CheckSecrets(schema, request.SecretValues, creating: true, fields);
        CheckLocation(request.LocationId, fields);
        if (fields.Count > 0)
        {
            return CommonErrors.ValidationFailed(fields);
        }

        if (CheckHostMode(info, request.HostMode) is { } refused)
        {
            return refused;
        }

        var admitted = _admission.Admit(info, request.ServerType);
        if (!admitted.IsSuccess)
        {
            return admitted.Error;
        }

        var id = ConnectionId.New();
        var created = await StoreSecretsAsync(id, schema!, request.SecretValues, ct).ConfigureAwait(false);
        if (!created.TryGetValue(out var secrets, out error))
        {
            return error;
        }

        var connection = new Connection(id, request.Name, info.Id, info.Version, request.ServerType, request.Settings.Clone(), secrets, request.LocationId,
            request.HostMode, null, request.StartNow, request.AutoImport, Connection.DefaultDescribeInterval, null, 1);
        try
        {
            await _repository.InsertAsync(connection, ct).ConfigureAwait(false);
        }
        catch
        {
            await DeleteSecretsAsync(id, secrets.Values).ConfigureAwait(false);
            throw;
        }

        var runtime = new ConnectionRuntime(connection, _services, this);
        _runtimes[id] = runtime;
        LogChanged(id, connection, "created", ctx);
        if (_activated)
        {
            await runtime.ActivateAsync().ConfigureAwait(false);
        }

        return id;
    }

    public async Task<Result> UpdateAsync(ConnectionId id, ConnectionUpdate update, long expectedVersion, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(ctx);
        return await WithConnectionAsync(id, async runtime =>
        {
            var current = runtime.Connection;
            if (current.Version != expectedVersion)
            {
                return CommonErrors.VersionConflict(expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    current.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var fields = new List<FieldError>();
            if (update.Name is { } name)
            {
                fields.AddRange(NameRules.Validate(name.Default, "/name"));
            }

            var schema = _packages.GetSettingsSchema(current.PackageId, current.PackageVersion, current.ServerType).TryGetValue(out var s, out _) ? s : null;
            if (update.Settings is { } settings)
            {
                CheckSettings(schema, settings, fields);
            }

            if (update.SecretValues is { } secretValues)
            {
                CheckSecrets(schema, secretValues, creating: false, fields);
            }

            if (update.LocationId is { } location)
            {
                CheckLocation(location, fields);
            }

            if (update.DescribeInterval is { } interval && interval < TimeSpan.FromMinutes(1))
            {
                fields.Add(FieldError.Create("/describeInterval", CommonErrorCodes.ValueOutOfRange, ("min", "PT1M")));
            }

            if (fields.Count > 0)
            {
                return CommonErrors.ValidationFailed(fields);
            }

            if (update.HostMode is { } mode && _packages.Get(current.PackageId, current.PackageVersion).TryGetValue(out var info, out _)
                && CheckHostMode(info, mode) is { } refused)
            {
                return refused;
            }

            // New secrets are stored first; the old ones are deleted only once the new row is committed.
            var secrets = new Dictionary<string, SecretId>(current.Secrets, StringComparer.Ordinal);
            var replaced = new List<SecretId>();
            var added = new List<SecretId>();
            if (update.SecretValues is { } values)
            {
                var toStore = values.Where(v => v.Value.Length > 0).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
                var stored = await StoreSecretsAsync(id, schema!, toStore, ct).ConfigureAwait(false);
                if (!stored.TryGetValue(out var newSecrets, out var storeError))
                {
                    return storeError;
                }

                added.AddRange(newSecrets.Values);
                foreach (var (field, value) in values)
                {
                    if (secrets.Remove(field, out var old))
                    {
                        replaced.Add(old);
                    }

                    if (newSecrets.TryGetValue(field, out var fresh))
                    {
                        secrets[field] = fresh;
                    }
                }
            }

            var next = current with
            {
                Name = update.Name ?? current.Name,
                Settings = update.Settings?.Clone() ?? current.Settings,
                Secrets = secrets,
                LocationId = update.LocationId ?? current.LocationId,
                Enabled = update.Enabled ?? current.Enabled,
                AutoImport = update.AutoImport ?? current.AutoImport,
                HostMode = update.HostMode ?? current.HostMode,
                DescribeInterval = update.DescribeInterval ?? current.DescribeInterval,
            };
            Connection? saved;
            try
            {
                saved = await _repository.UpdateAsync(next, expectedVersion, ct).ConfigureAwait(false);
            }
            catch
            {
                await DeleteSecretsAsync(id, added).ConfigureAwait(false);
                throw;
            }

            if (saved is null)
            {
                await DeleteSecretsAsync(id, added).ConfigureAwait(false);
                return CommonErrors.VersionConflict(expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "newer");
            }

            await DeleteSecretsAsync(id, replaced).ConfigureAwait(false);
            await runtime.ReconfigureAsync(saved, update.NeedsRestart).ConfigureAwait(false);
            if (update.Enabled == true)
            {
                await runtime.StartAsync().ConfigureAwait(false);
            }

            LogChanged(id, saved, "updated", ctx);
            return Result.Success();
        }, ct).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(ConnectionId id, bool deleteDevices, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return await WithConnectionAsync(id, async runtime =>
        {
            await runtime.DeleteAsync().ConfigureAwait(false);
            var connection = runtime.Connection;
            if (deleteDevices)
            {
                var servers = _registry.GetServersOfConnection(id).OrderByDescending(s => s.ParentId is null ? 0 : 1).ToList();
                if (servers.Count > 0)
                {
                    var deleted = await _registryWriter.ApplyAsync(new RegistryChangeBatch([.. servers.Select(s => new RegistryChange.DeleteServer(s.Id, DeleteDevices: true))]), ctx, ct)
                        .ConfigureAwait(false);
                    if (!deleted.IsSuccess)
                    {
                        return deleted;
                    }
                }
            }

            await _repository.DeleteAsync(id, ct).ConfigureAwait(false);
            await DeleteSecretsAsync(id, connection.Secrets.Values).ConfigureAwait(false);
            _runtimes.TryRemove(id, out _);
            await runtime.DisposeAsync().ConfigureAwait(false);
            LogChanged(id, connection, "deleted", ctx);
            return Result.Success();
        }, ct).ConfigureAwait(false);
    }

    public Task<Result> StartAsync(ConnectionId id, CancellationToken ct) => SetEnabledAsync(id, true, ct);

    public Task<Result> StopAsync(ConnectionId id, CancellationToken ct) => SetEnabledAsync(id, false, ct);

    public async Task<Result> RestartAsync(ConnectionId id, CancellationToken ct)
    {
        if (!_runtimes.TryGetValue(id, out var runtime))
        {
            return IntegrationErrors.ConnectionNotFoundError(id);
        }

        if (!runtime.Connection.Enabled)
        {
            return IntegrationErrors.NotRunning(id, ConnectionColumns.StateText(runtime.Status.State));
        }

        await runtime.RestartAsync().WaitAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<ConnectionTestResult> TestAsync(TestConnectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ResolvePackage(request.PackageId, request.PackageVersion).TryGetValue(out var info, out var error))
        {
            return ConnectionTestResult.Failed(FailureCategory.InvalidSettings, ConnectorMessage.Of("packageUnavailable", ("code", error.Code.Value)));
        }

        var fields = new List<FieldError>();
        var schema = CheckConnector(info, request.ServerType, fields);
        CheckSettings(schema, request.Settings, fields);
        if (schema is null || fields.Count > 0)
        {
            return ConnectionTestResult.Failed(FailureCategory.InvalidSettings, ConnectorMessage.Of("invalidSettings", ("fields", string.Join(",", fields.Select(f => f.Path)))));
        }

        var stored = request.ExistingConnection is { } existing && _runtimes.TryGetValue(existing, out var runtime) ? runtime.Connection.Secrets : new Dictionary<string, SecretId>();
        var id = request.ExistingConnection ?? ConnectionId.New();
        var connection = new Connection(id, LocalizedName.Of("test"), info.Id, info.Version, request.ServerType, request.Settings, stored, default,
            HostMode.InProcess, null, true, false, Connection.DefaultDescribeInterval, null, 0);
        if (!(await ConnectorCall.RunAsync(() => Task.FromResult(_host.Create(connection)), _options.InitializeTimeout, _time).ConfigureAwait(false))
            .TryGetValue(out var hosted, out error))
        {
            return ConnectionTestResult.Failed(FailureCategory.InvalidSettings, ConnectorMessage.Of("packageUnavailable", ("code", error.Code.Value)));
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var store = new ConnectionStore(id, new InMemoryConnectionRepository(), _logger);
        var services = _contexts.Create(connection, schema, hosted, store, new ConnectionCounterSource(), (_, _) => { }, _ => { }, stopping.Token,
            request.SecretValues, loadDevices: false);
        try
        {
            var initialized = await ConnectorCall.RunAsync(() => hosted.Connector.InitializeAsync(services.Context, stopping.Token).AsTask(), _options.InitializeTimeout, _time)
                .ConfigureAwait(false);
            if (!initialized.IsSuccess)
            {
                return ConnectionTestResult.Failed(FailureCategory.InvalidSettings, ConnectorMessage.Of("invalidSettings", ("code", initialized.Error.Code.Value)));
            }

            return await ConnectorCall.RunAsync(() => hosted.Connector.TestAsync(stopping.Token).AsTask(), _options.TestTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ConnectionTestResult.Failed(FailureCategory.Timeout, ConnectorMessage.Of("testTimeout"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            IntegrationLog.ConnectorFaulted(_logger, id.Value, "test: " + ex.GetType().FullName + ": " + ex.Message);
            return ConnectionTestResult.Failed(FailureCategory.InternalError, ConnectorMessage.Of("connectorError", ("exception", ex.GetType().Name)));
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await ConnectorCall.RunAsync(async () =>
                {
                    await hosted.Connector.DisposeAsync().ConfigureAwait(false);
                    return true;
                }, _options.StopTimeout, _time).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or not OperationCanceledException)
            {
                IntegrationLog.ConnectorAbandoned(_logger, id.Value);
            }

            await services.ReleaseAsync(TimeSpan.Zero).ConfigureAwait(false);
            hosted.Release?.Dispose();
        }
    }

    public async Task<Result<InventorySnapshot>> DiscoverAsync(ConnectionId id, IProgress<DiscoveryProgress>? progress, CancellationToken ct)
    {
        if (!_runtimes.TryGetValue(id, out var runtime))
        {
            return IntegrationErrors.ConnectionNotFoundError(id);
        }

        return await runtime.CallAsync("discover", (connector, token) => connector is IDiscovery discovery
            ? discovery.DiscoverAsync(progress ?? new Progress<DiscoveryProgress>(), token).AsTask()
            : connector.DescribeAsync(token).AsTask(), _options.DescribeTimeout, ct).ConfigureAwait(false);
    }

    public async Task<Result<ReconciliationResult>> DescribeAndReconcileAsync(ConnectionId id, CancellationToken ct) =>
        _runtimes.TryGetValue(id, out var runtime)
            ? await runtime.DescribeAndReconcileAsync(ct).ConfigureAwait(false)
            : IntegrationErrors.ConnectionNotFoundError(id);

    public async Task<Result> SetProtocolTraceAsync(ConnectionId id, TimeSpan duration, CancellationToken ct)
    {
        if (duration < TimeSpan.Zero || duration > MaxProtocolTrace)
        {
            return CommonErrors.ValidationFailed([FieldError.Create("/duration", CommonErrorCodes.ValueOutOfRange, ("min", "PT0S"), ("max", "PT1H"))]);
        }

        return await WithConnectionAsync(id, async runtime =>
        {
            var current = runtime.Connection;
            var next = current with { ProtocolTraceUntil = duration == TimeSpan.Zero ? null : _time.GetUtcNow() + duration };
            var saved = await _repository.UpdateAsync(next, current.Version, ct).ConfigureAwait(false);
            if (saved is null)
            {
                return CommonErrors.VersionConflict(current.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), "newer");
            }

            await runtime.ReconfigureAsync(saved, restart: false).ConfigureAwait(false);
            return Result.Success();
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConnectionEvent>> GetEventsAsync(ConnectionId id, DateTimeOffset from, DateTimeOffset until, CancellationToken ct) =>
        _repository.ListEventsAsync(id, from, until, ct);

    public async Task<Result<IReadOnlyList<DiagnosticItem>>> GetDiagnosticsAsync(ConnectionId id, CancellationToken ct)
    {
        if (!_runtimes.TryGetValue(id, out var runtime))
        {
            return IntegrationErrors.ConnectionNotFoundError(id);
        }

        if (runtime.Running is { Hosted.Connector: not IDiagnostics })
        {
            return Error.Unprocessable(IntegrationErrors.NotSupported, ("feature", "diagnostics"));
        }

        return await runtime.CallAsync("diagnostics", (connector, token) => ((IDiagnostics)connector).GetDiagnosticsAsync(token).AsTask(), _options.TestTimeout, ct)
            .ConfigureAwait(false);
    }

    // ---- IConnectionCommandChannel ----

    public ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectionId connection, ConnectorCommand command, CancellationToken ct) =>
        _runtimes.TryGetValue(connection, out var runtime)
            ? runtime.ExecuteAsync(command, ct)
            : ValueTask.FromResult(ConnectorCommandResult.Failed(FailureCategory.ConnectionDown, ConnectorMessage.Of("connectionDown")));

    public async ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(ConnectionId connection, IReadOnlyList<ConnectorCommand> commands, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return _runtimes.TryGetValue(connection, out var runtime)
            ? await runtime.ExecuteBatchAsync(commands, ct).ConfigureAwait(false)
            : [.. commands.Select(_ => ConnectorCommandResult.Failed(FailureCategory.ConnectionDown, ConnectorMessage.Of("connectionDown")))];
    }

    public bool SupportsBatch(ConnectionId connection) => _runtimes.TryGetValue(connection, out var runtime) && runtime.SupportsBatch;

    // ---- IPackageConnections (the catalogue) ----

    public bool IsInUse(PackageId id, SemVersion version) =>
        _runtimes.Values.Any(r => r.Connection.PackageId == id && r.Connection.PackageVersion == version);

    public async Task StopForPackageAsync(PackageId id, SemVersion version, string reason, CancellationToken ct)
    {
        var affected = _runtimes.Values.Where(r => r.Connection.PackageId == id && r.Connection.PackageVersion == version).ToList();
        await Task.WhenAll(affected.Select(r => r.SetPackageAvailableAsync(false, reason))).WaitAsync(ct).ConfigureAwait(false);
    }

    // ---- IIngestFloodingListener ----

    public void OnFloodingChanged(ConnectionId connection, bool flooding, int pending)
    {
        if (_runtimes.TryGetValue(connection, out var runtime))
        {
            runtime.OnFlooding(flooding, pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _packages.Changed -= OnPackageChanged;
        _registry.Changed -= OnRegistryChanged;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _watchdog?.Dispose();
        lock (_staggerTimers)
        {
            _staggerTimers.ForEach(t => t.Dispose());
            _staggerTimers.Clear();
        }

        await Task.WhenAll(_runtimes.Values.Select(r => r.ShutdownAsync())).ConfigureAwait(false);
        foreach (var runtime in _runtimes.Values)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }

        _runtimes.Clear();
        _shutdown.Dispose();
        _loadGate.Dispose();
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }
    }

    // ---- Helpers ----

    private void LogChanged(ConnectionId id, Connection connection, string action, ChangeContext ctx)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var actor = ctx.Actor?.Value.ToString("D") ?? ctx.Origin;
            IntegrationLog.ConnectionChanged(_logger, id.Value, connection.Name.Default, action, actor);
        }
    }

    private async Task<Result> SetEnabledAsync(ConnectionId id, bool enabled, CancellationToken ct)
    {
        ConnectionRuntime? target = null;
        var result = await WithConnectionAsync(id, async runtime =>
        {
            target = runtime;
            var current = runtime.Connection;
            if (current.Enabled != enabled)
            {
                var saved = await _repository.UpdateAsync(current with { Enabled = enabled }, current.Version, ct).ConfigureAwait(false);
                if (saved is null)
                {
                    return CommonErrors.VersionConflict(current.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), "newer");
                }

                await runtime.ReconfigureAsync(saved, restart: false).ConfigureAwait(false);
            }

            if (enabled)
            {
                await runtime.StartAsync().ConfigureAwait(false);
            }

            return Result.Success();
        }, ct).ConfigureAwait(false);

        // The stop is awaited outside the connection's lock: a stop takes up to the stop timeout.
        if (result.IsSuccess && !enabled && target is not null)
        {
            await target.StopAsync().WaitAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<Result> WithConnectionAsync(ConnectionId id, Func<ConnectionRuntime, Task<Result>> action, CancellationToken ct)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _runtimes.TryGetValue(id, out var runtime) ? await action(runtime).ConfigureAwait(false) : IntegrationErrors.ConnectionNotFoundError(id);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsPackageActive(Connection connection) =>
        _packages.Get(connection.PackageId, connection.PackageVersion).TryGetValue(out var info, out _) && info.State == PackageState.Active;

    private Result<PackageInfo> ResolvePackage(PackageId id, SemVersion? version)
    {
        if (version is null)
        {
            return _packages.GetLatestActive(id);
        }

        if (!_packages.Get(id, version).TryGetValue(out var info, out var error))
        {
            return error;
        }

        return info.State == PackageState.Active ? info : IntegrationErrors.NotActive(id, version.ToString());
    }

    private ConnectionSettingsSchema? CheckConnector(PackageInfo package, string serverType, List<FieldError> fields)
    {
        if (package.Connectors.All(c => c.ServerType != serverType)
            || !_packages.GetSettingsSchema(package.Id, package.Version, serverType).TryGetValue(out var schema, out _))
        {
            fields.Add(FieldError.Create("/serverType", ErrorCode.FromTrusted(IntegrationErrors.ServerTypeUnknown), ("serverType", serverType)));
            return null;
        }

        return schema;
    }

    private static void CheckSettings(ConnectionSettingsSchema? schema, JsonElement settings, List<FieldError> fields)
    {
        if (schema is null)
        {
            return;
        }

        foreach (var error in new ParameterSchema(schema.Settings).Validate(settings))
        {
            fields.Add(error with { Path = "/settings" + error.Path });
        }
    }

    private static void CheckSecrets(ConnectionSettingsSchema? schema, IReadOnlyDictionary<string, string> values, bool creating, List<FieldError> fields)
    {
        if (schema is null)
        {
            return;
        }

        foreach (var field in values.Keys.Where(k => schema.Secrets.All(s => s.Id != k)))
        {
            fields.Add(FieldError.Create("/secrets/" + field, ErrorCode.FromTrusted(IntegrationErrors.SecretUnknown)));
        }

        foreach (var field in schema.Secrets.Where(s => s.Required))
        {
            var given = values.TryGetValue(field.Id, out var value);
            if ((creating && (!given || string.IsNullOrEmpty(value))) || (!creating && given && string.IsNullOrEmpty(value)))
            {
                fields.Add(FieldError.Create("/secrets/" + field.Id, ErrorCode.FromTrusted(IntegrationErrors.SecretRequired)));
            }
        }
    }

    private void CheckLocation(LocationId location, List<FieldError> fields)
    {
        if (!_registry.GetLocation(location).IsSuccess)
        {
            fields.Add(FieldError.Create("/locationId", ErrorCode.FromTrusted(IntegrationErrors.LocationUnknown), ("locationId", location.Value.ToString("D"))));
        }
    }

    private static Error? CheckHostMode(PackageInfo package, HostMode mode)
    {
        if (mode != HostMode.InProcess)
        {
            // IHO (M4) brings isolated and remote hosts.
            return IntegrationErrors.HostModeUnavailable(ConnectionColumns.HostModeText(mode));
        }

        return string.Equals(package.Isolation, "isolatedRequired", StringComparison.Ordinal)
            ? Error.Create(ErrorKind.Validation, IntegrationErrors.ConnectionIsolationRequired, ("package", package.Id.Value))
            : null;
    }

    private async Task<Result<Dictionary<string, SecretId>>> StoreSecretsAsync(ConnectionId id, ConnectionSettingsSchema schema,
        IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var stored = new Dictionary<string, SecretId>(StringComparer.Ordinal);
        foreach (var (field, value) in values.Where(v => v.Value.Length > 0).OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var kind = schema.Secrets.FirstOrDefault(s => s.Id == field)?.Kind;
            var bytes = Encoding.UTF8.GetBytes(value);
            Result<SecretId> created;
            try
            {
                created = await _vault.CreateAsync($"connection.{id.Value:D}.{field}", SecretKindOf(kind), SecretAccessor.OwnerOf(id), bytes, null, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }

            if (!created.TryGetValue(out var secretId, out var error))
            {
                await DeleteSecretsAsync(id, stored.Values).ConfigureAwait(false);
                return error;
            }

            stored[field] = secretId;
        }

        return stored;
    }

    private async Task DeleteSecretsAsync(ConnectionId id, IEnumerable<SecretId> secrets)
    {
        foreach (var secret in secrets.ToList())
        {
            try
            {
                await _vault.DeleteAsync(secret, SecretAccessor.OwnerOf(id), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                IntegrationLog.SecretUnavailable(_logger, id.Value, secret.Value.ToString("D"), ex.GetType().Name);
            }
        }
    }

    private static SecretKind SecretKindOf(string? kind) => kind switch
    {
        "password" => SecretKind.Password,
        "apiKey" => SecretKind.ApiKey,
        "token" => SecretKind.Token,
        "certificate" => SecretKind.Certificate,
        "privateKey" => SecretKind.PrivateKey,
        _ => SecretKind.Generic,
    };

    private void OnPackageChanged(object? sender, PackageChangedEventArgs e)
    {
        if (e.Package.State != PackageState.Active)
        {
            return;
        }

        foreach (var runtime in _runtimes.Values.Where(r => r.Connection.PackageId == e.Package.Id && r.Connection.PackageVersion == e.Package.Version))
        {
            Observe(runtime.SetPackageAvailableAsync(true, "packageEnabled"), runtime.Connection.Id, "package enabled");
        }
    }

    private void OnRegistryChanged(object? sender, RegistryChangedEventArgs e)
    {
        if (!e.Batch.Changes.Any(c => c is RegistryChange.UpsertDevice or RegistryChange.PatchDevice or RegistryChange.DeleteDevice
            or RegistryChange.UpsertServer or RegistryChange.DeleteServer))
        {
            return;
        }

        foreach (var runtime in _runtimes.Values)
        {
            runtime.RefreshDeviceMap();
        }
    }

    private void Watchdog()
    {
        try
        {
            foreach (var runtime in _runtimes.Values)
            {
                runtime.Check();
            }

            var now = _time.GetUtcNow();
            if (now - _lastPurge >= TimeSpan.FromDays(1))
            {
                _lastPurge = now;
                Observe(_repository.PurgeEventsAsync(now - EventRetention, _shutdown.Token), default, "purge events");
            }
        }
        catch (Exception ex)
        {
            IntegrationLog.WatchdogFailed(_logger, ex);
        }
    }

    private void Observe(Task task, ConnectionId id, string operation) =>
        task.ContinueWith(t => IntegrationLog.RuntimeOperationFailed(_logger, t.Exception!, id.Value, operation),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
