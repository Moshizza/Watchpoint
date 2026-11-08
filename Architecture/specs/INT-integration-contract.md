# INT — Integration Contract and SDK

| | |
|---|---|
| Spec id | INT |
| Projects | `src/integrations/Watchpoint.Integration.Abstractions` (contract), `src/integrations/Watchpoint.Integration.Sdk` (helpers), `src/integrations/Watchpoint.Integration.Testing` (conformance suite, §12) |
| Milestone | M1 (contract, SDK), M4 (conformance suite) |
| Tasks | T1.13 (§4–§8), T1.14 (§9–§11), T4.03 (§12) |
| Design docs | [Integrations §4](../../docs/modules/integrations.md#4-the-connector-contract) |

## 1. Purpose and scope

The **contract** is what every connector implements and everything the runtime gives it. It is
small, stable and versioned (contract **1.x**). The **SDK** contains helpers so connectors stay
small and behave consistently. The **conformance suite** verifies any connector against the
contract's behavioural rules.

Not in scope: how the runtime hosts connectors (IRT, IHO).

## 2. Dependencies

| Uses | Used by |
|---|---|
| Abstractions: K only (+ `Microsoft.Extensions.Logging.Abstractions`; `Microsoft.CodeAnalysis.PublicApiAnalyzers` build-only). SDK: Abstractions, Polly (BSD-3), `System.IO.Pipelines` | All connector packages; IRT (implements the context) |

ARCH-6 and ARCH-7 (TST §6) enforce these limits.

## 3. Project layout

```
Watchpoint.Integration.Abstractions/     namespaces follow folders (…Abstractions, .Features, .Context, .Model)
├── IConnector.cs, ConnectorAttribute.cs, IntegrationJson.cs (source-generated JSON resolver)
├── ContractVersion.cs                     public const string Current = "1.0"; parsing and compatibility (§11)
├── Features/IServerLocator.cs, IDiscovery.cs, IStateRefresh.cs, ICommandExecutor.cs (+ IBatchCommandExecutor),
│            IEventCatchUp.cs, ITimeSync.cs, ILookup.cs, IDiagnostics.cs   (IVideoProvider: added by VID, M6)
├── Context/IConnectorContext.cs (+ ConnectionInfo), IObservationSink.cs, IDeviceMap.cs, IConnectionStore.cs,
│           Scheduling.cs (IConnectorScheduler, IRequestGate, IConnectionHealth, IConnectorMetrics),
│           Secrets.cs (ISecretAccessor, ConnectorSecret), ISecureHttp.cs, IInboundEndpoints.cs (+ InboundAuth)
├── Model/DeviceKey.cs, DeviceHandle.cs, InventorySnapshot.cs, CommandModel.cs (ConnectorCommand,
│         ConnectorCommandResult, RefreshScope), ConnectionTestResult.cs, FailureCategory.cs, ConnectorMessage.cs
└── PublicAPI.Shipped.txt, PublicAPI.Unshipped.txt   (Microsoft.CodeAnalysis.PublicApiAnalyzers, §11)

Watchpoint.Integration.Sdk/
├── ConnectorBase.cs                       lifecycle + reconnect loop template
├── Polling/PollingLoop.cs
├── Paging/BoundedPager.cs
├── Mapping/EnumMap.cs, ValueConverters.cs
├── Net/TcpRequestClient.cs, FramedStream.cs
├── Resilience/ResiliencePresets.cs
└── Logging/RedactionExtensions.cs
```

## 4. Connector lifecycle contract

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class ConnectorAttribute(string serverType) : Attribute { public string ServerType { get; } = serverType; }

public interface IConnector : IAsyncDisposable
{
    // 1. Called once per connection instance. Validate settings beyond the schema; keep the context.
    ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct);

    // 2. Short connectivity test (≤ 30 s); may be called before StartAsync (wizard) on a separate instance.
    ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct);

    // 3. Begin communication. Must return quickly (≤ 5 s): connecting happens in the background,
    //    progress is reported through context.Health.
    ValueTask StartAsync(CancellationToken ct);

    // 4. Stop all activity within the given token's lifetime (runtime gives 15 s). Idempotent.
    ValueTask StopAsync(CancellationToken ct);

    // 5. Return the complete inventory currently known (servers and devices). May contact the server.
    ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct);
}
```

Lifecycle rules (verified by the conformance suite):

| # | Rule |
|---|---|
| L1 | The runtime creates one connector instance per connection; instances never share mutable static state |
| L2 | Call order: `InitializeAsync` → (`TestAsync`)\* → `StartAsync` → (feature calls)\* → `StopAsync` → `DisposeAsync`. A stopped instance is not restarted; the runtime creates a new one |
| L3 | No blocking calls; all I/O async and cancellable |
| L4 | Exceptions escaping a method are treated as connector faults (connection → `Faulted`, restart with back-off); business failures are returned as results |
| L5 | `StartAsync` returns within 5 s; `StopAsync` completes within its token; `TestAsync` within 30 s |
| L6 | After `StopAsync` returns, the connector does not call the sink anymore |

```csharp
public sealed record ConnectionTestResult(bool Success, FailureCategory? Failure, ConnectorMessage? Message,
    ServerIdentity? Identity)
{
    public static ConnectionTestResult Succeeded(ServerIdentity? identity = null);
    public static ConnectionTestResult Failed(FailureCategory failure, ConnectorMessage message);
}
public sealed record ServerIdentity(string? Model, string? Version, string? Serial, IReadOnlyDictionary<string, string> Extra);

public enum FailureCategory
{
    Unreachable, AuthenticationFailed, IncompatibleVersion, VendorLicenceMissing, TlsError,
    InvalidSettings, DeviceOffline, ConnectionDown, RefusedBySystem, InvalidState, NotSupported,
    Timeout, Conflict, InternalError
}

// A translatable message from the package's i18n bundle: key relative to "packages.<packageId>."
// Key: one or more dot-separated segments, each ^[a-z][a-zA-Z0-9_]*$ (e.g. "connected", "errors.loginRefused").
// Args: the Kernel rules (K §5.4) — camelCase names, allowed types only; violations throw ArgumentException.
public sealed record ConnectorMessage(string Key, IReadOnlyDictionary<string, object?> Args)
{
    public static ConnectorMessage Of(string key, params (string Name, object? Value)[] args);
    public Message ToMessage(string packageId);   // K Message with key "packages.<packageId>.<key>"
}
```

## 5. Inventory

```csharp
public sealed record InventorySnapshot(IReadOnlyList<InventoryServerItem> Servers,
    IReadOnlyList<InventoryDeviceItem> Devices, bool IsComplete)
{
    public const int MaxNativeIdLength = 256;
    // common.validation.failed with JSON-pointer paths (/servers/0/nativeId, /devices/3/serverNativeId…):
    // empty or > 256 chars → value.required / value.too_long; duplicate server id, or device id per server →
    // value.duplicate; complete snapshot: no server → /servers required, device on an unlisted server → value.not_allowed.
    public Result Validate();
}

public sealed record InventoryServerItem(string NativeId, string? ParentNativeId, string TypeId, string TypeVersion,
    string VendorName, IReadOnlyDictionary<string, WpValue> VendorAttributes);

public sealed record InventoryDeviceItem(string NativeId, string ServerNativeId, string? ParentNativeId,
    string TypeId, string TypeVersion, string VendorName, IReadOnlyDictionary<string, WpValue> VendorAttributes,
    IReadOnlyList<string> UnsupportedOptionalCapabilities, IReadOnlyList<InventoryLinkItem> Links);

public sealed record InventoryLinkItem(string Kind /* cameraViews, memberOf, controls, powers, partOf */,
    string ToNativeId, string? ToServerNativeId);
```

Rules: native ids are **logical vendor identifiers** (never display names), unique per server,
≤ 256 chars, stable across restarts. A connection always has at least one server (root).
`IsComplete=false` when the connector could only read part of the system (the registry will
not orphan anything, REG §8).

## 6. Optional features

| Interface | Methods | Called by the runtime |
|---|---|---|
| `IServerLocator` (static-like: implemented by a separate class declared in the manifest, instantiated without settings) | `IAsyncEnumerable<LocatedServer> LocateAsync(LocateRequest request, CancellationToken ct)` | Wizard "search the network" (e.g. ONVIF WS-Discovery, BACnet Who-Is) |
| `IDiscovery` | `ValueTask<InventorySnapshot> DiscoverAsync(IProgress<DiscoveryProgress> progress, CancellationToken ct)` | Wizard and "rediscover" action (may be slower and more thorough than `DescribeAsync`) |
| `IStateRefresh` | `ValueTask RefreshAsync(RefreshScope scope, CancellationToken ct)` — reads current states and reports them through the sink | After (re)connection, on operator "refresh", on `health.refresh` command |
| `ICommandExecutor` | `ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct)` | Command dispatch (CMD) |
| `IBatchCommandExecutor` | `ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(IReadOnlyList<ConnectorCommand> commands, CancellationToken ct)` | Group commands on batchable commands |
| `IEventCatchUp` | `ValueTask CatchUpAsync(CancellationToken ct)` — reads missed events from the connector's persisted cursor and reports them | After reconnection, before live events resume |
| `IVideoProvider` | specified in VID (M6) | Video module |
| `ITimeSync` | `ValueTask<TimeSyncStatus> GetTimeAsync(ct)`, `ValueTask<Result> SetTimeAsync(DateTimeOffset utc, ct)` | Health checks of clock drift; command `health.syncClock` (later) |
| `ILookup` | `ValueTask<LookupResult> LookupAsync(LookupRequest request, CancellationToken ct)` (e.g. cardholder details by reference) | Operator UI enrichment (with personal-data permission) |
| `IDiagnostics` | `ValueTask<IReadOnlyList<DiagnosticItem>> GetDiagnosticsAsync(ct)`, `ValueTask<Result<ConnectorMessage>> RunActionAsync(string actionId, JsonElement args, ct)` | Connection *Diagnostics* tab |

```csharp
public sealed record RefreshScope(IReadOnlyList<DeviceHandle>? Devices /* null = all */, bool IncludeServers)
{
    public static RefreshScope All { get; }
}

// Supporting types of the features above
public sealed record LocateRequest(TimeSpan Timeout, IReadOnlyList<string>? Networks = null /* CIDRs; null = local networks */);
public sealed record LocatedServer(string Address /* as the settings expect it */, string? Name, ServerIdentity? Identity,
    JsonElement? SuggestedSettings /* fragment pre-filling the wizard */);
public sealed record DiscoveryProgress(int Done, int? Total, ConnectorMessage? Stage);
public sealed record TimeSyncStatus(DateTimeOffset SystemTime, DateTimeOffset ReadAt /* node clock */, TimeSpan? RoundTrip);  // runtime computes drift
public sealed record LookupRequest(string Kind /* declared in the manifest, e.g. "cardholder" */, string Reference, DeviceKey? Device);
public sealed record LookupResult(bool Found, IReadOnlyDictionary<string, WpValue> Fields) { public static LookupResult NotFound { get; } }
public sealed record DiagnosticItem(string Id, ConnectorMessage Label, WpValue Value, DiagnosticLevel Level = DiagnosticLevel.Info);
public enum DiagnosticLevel { Info, Warning, Error }

public sealed record ConnectorCommand(Guid CommandId, DeviceHandle Target, string Command /* "openable.open" */,
    IReadOnlyDictionary<string, WpValue> Arguments, DateTimeOffset Deadline, int Attempt, CommandPriority Priority);
public enum CommandPriority { Critical, High, Normal, Background }

// Constructor enforces consistency: Failed ⇔ failure category and message present (ArgumentException otherwise).
public sealed record ConnectorCommandResult(CommandOutcome Outcome, FailureCategory? Failure,
    ConnectorMessage? Message, string? VendorCode, string? VendorDetail /* never shown to operators; logged at debug */)
{
    public static ConnectorCommandResult Succeeded();
    public static ConnectorCommandResult Failed(FailureCategory failure, ConnectorMessage message, string? vendorCode = null);
    public static ConnectorCommandResult NotSupported();
}
public enum CommandOutcome { Succeeded, Failed, NotSupported }
```

The runtime never calls `ExecuteAsync` for a command the device type does not declare; the
connector returns `NotSupported` for commands its devices lack (e.g. model without remote
opening).

## 7. Connector context

```csharp
public interface IConnectorContext
{
    ConnectionInfo Connection { get; }
    JsonElement Settings { get; }                                  // validated against the schema, defaults applied
    T GetSettings<T>(JsonTypeInfo<T> typeInfo);
    ISecretAccessor Secrets { get; }
    IObservationSink Sink { get; }
    IDeviceMap Devices { get; }
    IConnectionStore Store { get; }
    IConnectorScheduler Scheduler { get; }
    IRequestGate Gate { get; }
    IConnectionHealth Health { get; }
    ISecureHttp Http { get; }
    IInboundEndpoints Inbound { get; }
    IConnectorMetrics Metrics { get; }
    ILoggerFactory Logging { get; }                                // loggers redact declared secret fields
    TimeProvider Time { get; }
    CancellationToken Stopping { get; }                            // cancelled when StopAsync begins
}

public sealed record ConnectionInfo(Guid Id, string Name, string PackageId, string PackageVersion,
    string ServerType, string Culture /* site default culture, for vendor-language hints */);
```

### 7.1 Observation sink

```csharp
public readonly record struct DeviceKey(string ServerNativeId, string NativeId);   // ordinal comparison; null ids rejected

public interface IObservationSink
{
    void ReportProperty(DeviceKey device, string propertyPath, WpValue value,
        Quality quality = Quality.Good, DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null);
    void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null,
        DateTimeOffset? sourceTime = null);
    void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null);
    void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null);
    void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good,
        DateTimeOffset? sourceTime = null);
    void ReportInventoryChanged(InventorySnapshot partialOrComplete);   // runtime reconciles (REG §8)

    // Completes when everything reported before the call is durable (in the event log) or rejected.
    ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct);
}
public sealed record SinkFlushResult(int Accepted, int Rejected, int UnknownDevices);
```

Rules:

| # | Rule |
|---|---|
| S1 | `Report*` methods are non-blocking and thread-safe; they buffer. The runtime flushes automatically every 50 ms or 500 items |
| S2 | When buffers are full (back-pressure), `Report*` **does not block**: it records into the bounded buffer and `FlushAsync` waits; connectors that loop must `await FlushAsync` at least once per loop iteration (the SDK polling loop does it) |
| S3 | Reports for native ids not imported in the registry are dropped (counted as `UnknownDevices`, not errors) |
| S4 | A connector must call `FlushAsync` **before** persisting an event cursor (`IConnectionStore`), so no event is lost if the node crashes |
| S5 | Values must match the property type; unit may be given for numeric properties (converted by MOD) |
| S6 | Rejections (invalid values) are logged by the runtime with the connection id; the connector is not stopped |

### 7.2 Device map

```csharp
public sealed record DeviceHandle(DeviceKey Key, string TypeId, string TypeVersion,
    IReadOnlyDictionary<string, WpValue> Attributes, IReadOnlyList<string> UnsupportedOptionalCapabilities);

public interface IDeviceMap
{
    IReadOnlyCollection<DeviceHandle> All { get; }                  // imported, enabled devices of this connection
    bool TryGet(DeviceKey key, [NotNullWhen(true)] out DeviceHandle? handle);
    event EventHandler<DeviceMapChangedEventArgs>? Changed;          // imports, deletions, enable/disable, attribute edits
}
public sealed class DeviceMapChangedEventArgs(IReadOnlyList<DeviceHandle> added, IReadOnlyList<DeviceHandle> updated,
    IReadOnlyList<DeviceKey> removed) : EventArgs;          // Added (imported/enabled), Updated, Removed (deleted/disabled)
```

### 7.3 Other services

```csharp
public interface ISecretAccessor
{
    ValueTask<ConnectorSecret?> GetAsync(string fieldId, CancellationToken ct);   // fieldId from manifest secretsSchema
}
public sealed class ConnectorSecret : IDisposable        // created by the runtime from the UTF-8 bytes (copied)
{
    public ConnectorSecret(ReadOnlySpan<byte> utf8Value);
    // Dispose zeroes the bytes; any use afterwards throws ObjectDisposedException.
    public T UseAsString<T>(Func<string, T> use);
    public T UseBytes<T>(Func<ReadOnlySpan<byte>, T> use);
    public override string ToString() => "***";
    public void Dispose();
}

public interface IConnectionStore          // replicated with the connection (failover keeps cursors)
{
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);   // ≤ 64 KB per key, ≤ 1 MB total
    ValueTask DeleteAsync(string key, CancellationToken ct);
}

public interface IConnectorScheduler
{
    IDisposable SchedulePolling(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions? options = null);
}
public sealed record PollingOptions(double Jitter = 0.1, bool RunWhenDisconnected = false,
    TimeSpan? MaxBackoff = null /* 5 min */, CommandPriority Priority = CommandPriority.Background);

public interface IRequestGate               // per-connection outbound concurrency with priorities
{
    ValueTask<T> RunAsync<T>(CommandPriority priority, Func<CancellationToken, ValueTask<T>> request, CancellationToken ct);
    int MaxConcurrency { get; }              // from settings or manifest limits; default 4
}

public interface IConnectionHealth
{
    void Report(ConnectionStatus status, ConnectorMessage? reason = null);
    // Connecting, Connected, Degraded, Disconnected — runtime maps to connection state (IRT §7)
}
public enum ConnectionStatus { Connecting, Connected, Degraded, Disconnected }

public interface ISecureHttp
{
    HttpClient CreateClient(HttpClientOptions options);   // TLS policy from settings: system trust, custom CA (PEM), or SHA-256 public-key pin; never "ignore errors"
}
public sealed record HttpClientOptions(Uri BaseAddress, TimeSpan Timeout, string? PinnedPublicKeySha256 = null,
    string? CustomCaPem = null, bool AllowHttp = false /* only if the settings explicitly allow plain HTTP */);

public interface IInboundEndpoints
{
    // Hosted under https://<node>/integrations/<connectionId>/<path>, authenticated as declared.
    ValueTask<InboundRegistration> RegisterHttpAsync(string path, InboundAuth auth,
        Func<InboundRequest, CancellationToken, ValueTask<InboundResponse>> handler, CancellationToken ct);
    ValueTask<InboundRegistration> RegisterTcpAsync(int port, IPAddress bindAddress,
        Func<Stream, CancellationToken, ValueTask> handler, CancellationToken ct);   // needs manifest permission "inboundTcp"
    ValueTask<InboundRegistration> RegisterUdpAsync(int port, IPAddress bindAddress, string? shareKey,
        Func<UdpDatagram, CancellationToken, ValueTask> handler, CancellationToken ct);   // M4 (C-SNMP): permission "inboundUdp"; same shareKey → one socket fanned out
}
// JSON: discriminator "kind" = sharedSecretHeader | hmacSignature | clientCertificate | sourceAddress, camelCase members;
// metadata from IntegrationJson.Resolver (source-generated; pass it to WatchpointJson.Create).
public abstract record InboundAuth
{
    public sealed record SharedSecretHeader(string HeaderName, string SecretFieldId) : InboundAuth;
    public sealed record HmacSignature(string HeaderName, string SecretFieldId, string Algorithm /* HMACSHA256 */) : InboundAuth;
    public sealed record ClientCertificate(string PinnedSha256) : InboundAuth;
    public sealed record SourceAddress(IReadOnlyList<string> AllowedCidrs) : InboundAuth;   // combined with another
}

public sealed class InboundRegistration(string address /* public URL or bound endpoint */, Func<ValueTask> unregister)
    : IAsyncDisposable;                                       // DisposeAsync unregisters (idempotent)
public sealed record InboundRequest(string Method, string Path, string Query,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers, ReadOnlyMemory<byte> Body, IPAddress RemoteAddress);
public sealed record InboundResponse(int StatusCode, ReadOnlyMemory<byte> Body = default, string? ContentType = null)
{
    public static InboundResponse Ok { get; }
    public static InboundResponse NoContent { get; }
}
public sealed record UdpDatagram(ReadOnlyMemory<byte> Data, IPEndPoint Remote);

public interface IConnectorMetrics
{
    void CountRequest(string operation, bool success, TimeSpan duration);
    void SetGauge(string name, double value);       // name pattern ^[a-z][a-z0-9_]{1,40}$
}
```

## 8. Settings and secrets for connectors

- Settings arrive validated against the manifest's settings schema (SCM §4.4) with defaults
  applied; connectors may perform extra semantic checks in `InitializeAsync` and return
  `Result` failures with `common.validation.failed` and field errors (paths relative to the
  settings document).
- Secrets are accessed only via `ISecretAccessor` with field ids declared in the manifest.

## 9. SDK — `ConnectorBase`

```csharp
public abstract class ConnectorBase : IConnector
{
    protected IConnectorContext Context { get; private set; }
    protected ILogger Logger { get; private set; }

    public virtual ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct);
    public abstract ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct);
    public abstract ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct);

    // Template: StartAsync launches RunLoopAsync in the background and returns.
    // Loop: Health=Connecting → ConnectOnceAsync → Health=Connected → OnConnectedAsync (refresh + catch-up)
    //       → RunConnectedAsync until it returns/throws → Health=Disconnected → back-off → repeat.
    protected abstract ValueTask ConnectOnceAsync(CancellationToken ct);
    protected abstract ValueTask RunConnectedAsync(CancellationToken ct);   // e.g. event stream loop; return when connection lost
    protected virtual ValueTask DisconnectAsync(CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnConnectedAsync(CancellationToken ct);       // calls RefreshAsync/CatchUpAsync if implemented
    protected virtual TimeSpan GetReconnectDelay(int attempt);                // 1, 2, 4 … max 60 s, ±20 % jitter

    public ValueTask StartAsync(CancellationToken ct);
    public ValueTask StopAsync(CancellationToken ct);
    public virtual ValueTask DisposeAsync();
}
```

Behaviour guaranteed by the base class:

- Reconnection with back-off and jitter; `Health.Report` at each transition with a reason
  (`ConnectorMessage` from exceptions mapped by `MapFailure(Exception)` overridable).
- Polls registered through `Context.Scheduler` are paused while disconnected (unless
  `RunWhenDisconnected`).
- On disconnection, the base class reports `ServerHealth(Unknown)` for the root server with
  reason `connectionLost` (so STA marks devices stale).
- `StopAsync` cancels the loop, calls `DisconnectAsync`, awaits completion within the token.

## 10. SDK — helpers

| Helper | Purpose | Key rules |
|---|---|---|
| `PollingLoop` | Used by the scheduler implementation and by connectors needing custom loops | Interval + jitter; error back-off doubling to `MaxBackoff`; `await Sink.FlushAsync` after each iteration; never overlaps iterations |
| `BoundedPager<T>` | "Read pages until done" with safety | `MaxPages` (default 10,000), delay on empty page (1 s), stop when a page is shorter than page size or a cursor repeats; throws `PagingLoopDetectedException` on repeated cursor |
| `EnumMap<TVendor>` | Map vendor codes to Watchpoint enum codes | Unknown vendor codes → mapped to a declared fallback (`unknown`) and counted (metric + debug log), never exceptions |
| `ValueConverters` | Scaling, byte order, BCD, bit extraction (for Modbus/BACnet) | Pure functions, unit-tested |
| `TcpRequestClient` | Request/response over TCP with framing | Connect timeout (default 5 s), read timeout, max frame size, cancellation; one connection reused with keep-alive or per-request mode |
| `FramedStream` | Length-prefixed / delimiter framing over `PipeReader` | Max frame size enforced (default 1 MB) |
| `ResiliencePresets` | Polly pipelines: `Request` (timeout + 2 retries on transient), `Command` (timeout only, no retry unless idempotent), `Poll` (timeout + circuit breaker opening after 5 failures for 30 s) | |
| `RedactionExtensions` | `logger.LogProtocolTrace(...)` only active when the connection's protocol trace is enabled (time-limited), with secret field masking | |

## 11. Versioning of the contract

- `ContractVersion.Current = "1.0"`. Minor versions add optional features or members with
  default interface implementations; existing connectors keep working.
- `ContractVersion.TryParse(text, out major, out minor)` (`major.minor`, digits only),
  `Satisfies(requirement, version)` (`^M.m` = same major, minor ≥ m; plain `M.m` = exact) and
  `IsSupported(requirement)` = `Satisfies(requirement, Current)`.
- The manifest declares `contract: "^1.0"`; IRT refuses packages requiring an unknown major
  or a newer minor than the runtime implements (`irt.package.contract_unsupported`).
- Public API of Abstractions is tracked with `PublicAPI.Shipped.txt` (additive only):
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` (build-only) fails the build on undeclared (RS0016) or
  removed (RS0017) symbols; additions go to `PublicAPI.Unshipped.txt` and move to Shipped at release.

## 12. Conformance suite (T4.03, `Watchpoint.Integration.Testing`)

A connector's test project derives from `ConnectorConformanceTests<TConnector>` and provides a
**simulator** of its vendor system (`IVendorSimulator`) with hooks: start/stop, drop
connection, inject state change, inject event, make next command fail/slow, count requests.

```csharp
public abstract class ConnectorConformanceTests<TConnector> where TConnector : IConnector
{
    protected abstract IVendorSimulator CreateSimulator();
    protected abstract JsonElement CreateSettings(IVendorSimulator sim);
    protected abstract IReadOnlyDictionary<string, string> CreateSecrets(IVendorSimulator sim);
    protected virtual ConformanceOptions Options => new();   // which features to test, timings
}
```

| Id | Rule checked |
|---|---|
| INT-C-001 | `InitializeAsync` with valid settings succeeds; with semantically invalid settings returns validation errors (no exception) |
| INT-C-002 | `TestAsync` succeeds against the simulator and returns identity |
| INT-C-003 | `TestAsync` with simulator stopped → `Unreachable` within 30 s |
| INT-C-004 | `TestAsync` with wrong credentials → `AuthenticationFailed` (if the vendor has credentials) |
| INT-C-005 | `StartAsync` returns within 5 s even if the simulator is unreachable |
| INT-C-006 | Health goes Connecting → Connected when the simulator is up |
| INT-C-007 | Simulator dropped → Health Disconnected; root server health Unknown reported |
| INT-C-008 | Reconnects automatically with back-off after the simulator comes back; resync performed (refresh observed) |
| INT-C-009 | After reconnect, events that happened while disconnected are reported (if `eventCatchUp`) exactly once |
| INT-C-010 | `DescribeAsync` returns complete inventory matching the simulator; native ids stable across two calls and restarts |
| INT-C-011 | No native id is a display name (simulator renames a device → native id unchanged) |
| INT-C-012 | State change injected → property reported within the declared latency (polling interval + 1 s or 1 s for push) |
| INT-C-013 | Values reported match property types (all accepted by a strict sink using the type catalogue) |
| INT-C-014 | Event injected → event reported once with payload valid |
| INT-C-015 | Each declared command → `Succeeded` against the simulator, and the simulator registered the request |
| INT-C-016 | Simulator refuses a command → `Failed` with the right category and a message key that exists in the package bundles (en/fr/ar) |
| INT-C-017 | Simulator slow beyond deadline → command returns `Failed(Timeout)` near the deadline, no exception |
| INT-C-018 | Command for an unsupported model → `NotSupported` |
| INT-C-019 | Commands are prioritised over polls: with gate saturated by polls, a Critical command starts before queued polls |
| INT-C-020 | Batch commands (if `IBatchCommandExecutor`) return one result per command in order |
| INT-C-021 | `StopAsync` completes within 15 s while busy (polls and commands in flight) |
| INT-C-022 | No sink call after `StopAsync` returned |
| INT-C-023 | Two instances (two connections) against two simulators work independently (no shared static state) |
| INT-C-024 | Cursor persisted only after `FlushAsync` (spy store and sink: no `SetAsync` of a cursor key before flush completion) |
| INT-C-025 | Back-pressure: sink flush delayed 5 s → connector does not grow memory unboundedly (≤ 50 MB increase) and does not lose events |
| INT-C-026 | Unknown native ids from the simulator (devices not imported) do not cause errors |
| INT-C-027 | Secrets never appear in logs (`SecretLeakDetector`) during a full test/start/command/stop cycle |
| INT-C-028 | Protocol trace off by default: no protocol payloads in logs at Information level |
| INT-C-029 | TLS: with a simulator presenting an untrusted certificate and no pin → connection fails with `TlsError` (if the vendor uses TLS) |
| INT-C-030 | TLS: with the correct pin → connects |
| INT-C-031 | Paging loops bounded: simulator returns the same cursor forever → loop detected, connection degraded, no CPU spin (≤ 1 request/s) |
| INT-C-032 | Empty pages handled: simulator returns empty pages → delay applied |
| INT-C-033 | Malformed vendor responses → counted and logged, connector keeps running |
| INT-C-034 | Vendor input escaped: device names with `<`, `&`, quotes, Unicode (Arabic) round-trip without injection |
| INT-C-035 | `RefreshAsync(all)` reports every imported device's state |
| INT-C-036 | Device disabled in the map → connector stops reporting it (or reports are dropped) and does not poll it if per-device polling |
| INT-C-037 | Device map change (import) → new device starts being reported without restart |
| INT-C-038 | Exceptions inside polling callbacks do not stop the polling loop |
| INT-C-039 | CPU idle: connected and idle for 60 s → < 2 % of one core average |
| INT-C-040 | Memory steady: 10 minutes of normal simulated activity → no growth trend above 10 % |

## 13. Tests (contract and SDK)

`tests/integrations/Watchpoint.Integration.Abstractions.Tests` and `…Sdk.Tests`.

| Id | Test | Given / When / Then |
|---|---|---|
| INT-U-001 | `ConnectorMessage.Of` stores key and args; disallowed arg types rejected | |
| INT-U-002 | `ConnectorCommandResult` factories set outcome/failure consistently | `Succeeded()` has no failure; `Failed` requires category and message |
| INT-U-003 | `DeviceKey` equality and hashing | |
| INT-U-004 | `InventorySnapshot` validation helper rejects empty native ids, > 256 chars, duplicates per server | |
| INT-U-005 | `ConnectorSecret` hides value and zeroes on dispose | |
| INT-U-006 | `HttpClientOptions` cannot express "ignore TLS errors" (API surface test: no such member) | reflection check |
| INT-U-007 | `InboundAuth` variants serialise for manifest/settings references | |
| INT-U-008 | `ContractVersion` parsing and compatibility helper | `^1.0` accepts 1.0, 1.3; rejects 2.0 |
| INT-U-009 | Public API snapshot unchanged except additions | PublicAPI analyzer |
| INT-U-010 | Abstractions references only Kernel and logging abstractions | ARCH-6 executed here too |
| INT-U-011 | `ConnectorBase`: StartAsync returns immediately; loop connects | Fake connect succeeding after 2 failures → health Connecting, Disconnected, Connecting, Connected |
| INT-U-012 | `ConnectorBase`: reconnect delays | attempts 1…7 → 1,2,4,8,16,32,60 s ±20 % (TestClock) |
| INT-U-013 | `ConnectorBase`: OnConnected calls refresh and catch-up when implemented | |
| INT-U-014 | `ConnectorBase`: disconnection reports root server health Unknown with reason | |
| INT-U-015 | `ConnectorBase`: StopAsync cancels loop and calls DisconnectAsync once | |
| INT-U-016 | `ConnectorBase`: exception mapping to failure categories | SocketException → Unreachable; AuthenticationException → TlsError |
| INT-U-017 | `PollingLoop`: interval and jitter bounds | |
| INT-U-018 | `PollingLoop`: no overlap | Work longer than interval → next starts after previous ends |
| INT-U-019 | `PollingLoop`: error back-off doubling to max, reset on success | |
| INT-U-020 | `PollingLoop`: flush awaited each iteration | |
| INT-U-021 | `PollingLoop`: paused when disconnected unless allowed | |
| INT-U-022 | `BoundedPager`: stops on short page | |
| INT-U-023 | `BoundedPager`: delay on empty page | |
| INT-U-024 | `BoundedPager`: repeated cursor → exception | |
| INT-U-025 | `BoundedPager`: max pages enforced | |
| INT-U-026 | `EnumMap`: unknown vendor code → fallback + counter | |
| INT-U-027 | `ValueConverters`: scaling and offset | raw 1234, scale 0.1, offset −40 → 83.4 |
| INT-U-028 | `ValueConverters`: byte orders (ABCD, DCBA, BADC, CDAB) for 32-bit int and float | |
| INT-U-029 | `ValueConverters`: bit extraction and BCD | |
| INT-U-030 | `TcpRequestClient`: connect timeout | unreachable address → timeout ≤ 5.5 s |
| INT-U-031 | `TcpRequestClient`: read timeout and cancellation | |
| INT-U-032 | `FramedStream`: length-prefix and delimiter framing; max frame size | |
| INT-U-033 | `ResiliencePresets.Command`: no retry when not idempotent | |
| INT-U-034 | `ResiliencePresets.Poll`: circuit opens after 5 failures, half-open after 30 s | |
| INT-U-035 | `RequestGate` priority ordering | Saturated gate, queued Background ×10 then Critical → Critical runs next |
| INT-U-036 | `RequestGate` concurrency limit respected | |
| INT-U-037 | Protocol trace logging only when enabled; masks declared secret fields | |
| INT-U-038 | Secure HTTP pin validation | Pin mismatch → `HttpRequestException` with TLS inner; match → success (test server with self-signed cert) |
| INT-U-039 | Secure HTTP custom CA | Server cert chained to provided CA → success; other CA → failure |
| INT-U-040 | Secure HTTP refuses plain HTTP unless allowed | |

## 14. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.13 | §4–§8, §11 | INT-U-001…INT-U-010 |
| T1.14 | §9, §10 | INT-U-011…INT-U-040 |
| T4.03 | §12 | INT-C-001…INT-C-040 (run against C-SIM first) |
