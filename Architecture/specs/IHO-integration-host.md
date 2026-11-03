# IHO — Isolated and Remote Integration Hosts

| | |
|---|---|
| Spec id | IHO |
| Projects | `src/service/Watchpoint.Integrations.Hosting` (node side), `src/service/Watchpoint.IntegrationHost` (host process), `schemas/proto/integration_host.proto` |
| Milestone | M4 |
| Tasks | T4.01 (§4–§7), T4.02 (§8–§10) |
| Design docs | [Integrations §6](../../docs/modules/integrations.md#6-integration-hosts-and-isolation), [Security](../../docs/operations/security.md) |
| Spike | T0.11 decision record (latency and crash behaviour; numbers below are the targets it must confirm) |

## 1. Purpose and scope

Run connectors **outside the node process** with the same connector code:

- **Isolated host**: a child process `watchpoint-integration-host` on the node's machine, talking
  to the node over gRPC on a named pipe (Windows) or Unix domain socket (Linux). A crash or hang
  kills only that host.
- **Remote host**: the same program installed on another machine, paired once, connecting
  **outbound** to the cluster over mTLS gRPC, buffering observations while the WAN is down.

Both implement IRT's `IConnectionHost` (§4). The in-process host (IRT) stays the default for
packages declared `inProcessAllowed`.

Not in scope: cluster placement of connections on nodes (CLU, M9 — this spec handles a remote
host's link to "the cluster" through an address list), script host (SCR, M8 — reuses the process
supervisor of §6).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST, IRT (connection runtime, package cache, context services), INT (Abstractions), SEC (site CA, secrets, `IssueFromCsrAsync` added by T4.02), PIP (sink ingest), STO (host registry), AUD, Grpc.AspNetCore / Grpc.Net.Client (Apache-2.0), Google.Protobuf (BSD-3), Microsoft.Data.Sqlite (MIT, remote buffer) | IRT (host modes), UIF (*Integrations → Hosts*), HLT (host health), CLU (M9 host failover), SCR (process supervisor reuse), DEP (remote host installer, M11) |

## 3. Layout

```
schemas/proto/integration_host.proto           package watchpoint.host.v1
Watchpoint.Integrations.Hosting/               (node side, loaded by the node)
├── Contracts/HostInfo.cs, HostKind.cs, HostState.cs, IHostRegistry.cs, PairingCode.cs, HostErrors.cs
├── Internal/
│   ├── Isolated/IsolatedHostManager.cs, ProcessSupervisor.cs, ResourceLimiter.cs (JobObjectLimiter, CgroupLimiter, RlimitLimiter)
│   ├── Protocol/HostLinkService.cs (gRPC server), HostSession.cs, RemoteConnectionProxy.cs, ContextServicesBridge.cs
│   ├── Remote/PairingService.cs, RemoteHostAuthenticator.cs, PackageDistributor.cs
│   ├── IsolatedConnectionHost.cs, RemoteConnectionHost.cs   (IConnectionHost implementations)
│   └── Storage/HostsDbContext.cs
├── HostingModule.cs
└── HostingLog.cs                              event ids 1950–1999
Watchpoint.IntegrationHost/                    (the host program; console app + Windows service / systemd unit for remote)
├── Program.cs                                 modes: --isolated <args> | run (remote) | pair <code> <address>
├── HostLinkClient.cs                          gRPC client, reconnect, session resume
├── HostedConnection.cs                        loads package (IRT PackageLoadContext reused), runs IConnector
├── Context/ (proxy implementations of IConnectorContext services)
├── Buffer/ObservationBuffer.cs                (remote: SQLite durable buffer)
└── PackageCache.cs
```

The host references `Watchpoint.Integration.Abstractions`, the IRT package loading code
(`Watchpoint.Integrations.Loading`, extracted from IRT by T4.01 into a small shared project) and
`Watchpoint.Kernel`. It never references node modules (NetArchTest rule IHO-U-001).

## 4. `IConnectionHost` (contract owned by IRT, specified here)

```csharp
public interface IConnectionHost
{
    HostId Id { get; }                       // in-process host: HostId.InProcess
    HostKind Kind { get; }                   // InProcess | Isolated | Remote
    HostState State { get; }                 // Starting | Ready | Degraded | Unavailable | Faulted
    event Action<HostStateChanged>? StateChanged;

    Task<Result<IHostedConnection>> StartConnectionAsync(HostedConnectionSpec spec, IConnectorContextServices services, CancellationToken ct);
    Task<ConnectionTestResult> TestAsync(HostedConnectionSpec spec, IConnectorContextServices services, CancellationToken ct);
}

public sealed record HostedConnectionSpec(ConnectionInfo Info, PackageRef Package /* id, version, sha256 */, string ServerType,
    JsonElement Settings, IReadOnlyList<string> SecretFieldIds, ConnectorLimits Limits);

public interface IHostedConnection : IAsyncDisposable   // what ConnectionRuntime drives (IRT §7)
{
    ValueTask StartAsync(CancellationToken ct);
    ValueTask StopAsync(CancellationToken ct);
    ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct);
    ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct);  // if ICommandExecutor
    ValueTask<StateRefreshResult> RefreshAsync(IReadOnlyList<DeviceKey>? devices, CancellationToken ct); // IStateRefresh
    ValueTask<DiagnosticResult> RunDiagnosticAsync(string actionId, JsonElement? args, CancellationToken ct);
    IReadOnlySet<string> Features { get; }   // implemented optional feature interfaces (INT §6)
    event Action<HostedConnectionFault>? Faulted;   // host crash, link loss, connector fault
}
```

`IConnectorContextServices` is the node-side implementation of the context services that IRT
already builds for in-process connectors (sink, device map, store, secrets, health, inbound,
metrics, logging). Isolated/remote hosts **bridge** them over gRPC (§5.3); scheduling, request
gate, HTTP client and time run **inside the host** (they need no node state).

IRT changes made by T4.01: `ConnectionRuntime` resolves the host from `connection.HostMode`
(`InProcess` → `InProcessHost`, `Isolated` → `IsolatedHostManager.GetOrStartHost(groupKey)`,
`Remote` → `RemoteConnectionHost` for `HostId`), removing the M1 `irt.host.unavailable` refusal
for available modes. `isolatedRequired` packages refuse `InProcess` (unchanged).

## 5. Protocol (`integration_host.proto`)

### 5.1 Shape

The **host is always the gRPC client** (isolated and remote alike); the node exposes
`HostLink` on a local pipe/socket (isolated) and on the node's TLS port `wss`-free HTTP/2
endpoint `/watchpoint.host.v1.HostLink/*` (remote, mTLS required).

```proto
service HostLink {
  rpc Session(stream HostToNode) returns (stream NodeToHost);   // one per host, multiplexed
  rpc FetchPackage(FetchPackageRequest) returns (stream PackageChunk);  // 1 MiB chunks, sha256 checked
}

message HostToNode {
  uint64 seq = 1;                   // per session, monotonic
  oneof body {
    Hello hello = 10;               // first message: host id, version, kind, os/arch, session token or resume
    Reply reply = 11;               // reply to a NodeToHost request (correlation id)
    ObservationBatch observations = 12;   // properties, events, health, inventory changes, with buffer sequence numbers
    HealthReport health = 13;       // ConnectionStatus per connection
    LogBatch logs = 14;             // structured log events (already redacted in the host)
    MetricBatch metrics = 15;
    ContextRequest context = 16;    // secrets get, store get/set/delete, flush, inbound registration
    HostStatus status = 17;         // CPU, memory, connections, buffer usage (every 10 s)
  }
}
message NodeToHost {
  uint64 seq = 1;
  oneof body {
    Welcome welcome = 10;           // session id, resume accepted?, node time, limits, last acked observation seq
    Request request = 11;           // start/stop/describe/test/execute/refresh/diagnostic for a connection
    ContextReply context_reply = 12;
    ObservationAck ack = 13;        // highest observation seq durable in the event log
    DeviceMapUpdate device_map = 14; // full on start, then deltas
    InboundCall inbound = 15;       // forwarded inbound HTTP request (§5.3)
    Shutdown shutdown = 16;
  }
}
```

Values (`WpValue`) are encoded as a protobuf `oneof` (bool, int64, double, decimal as string,
string, bytes, timestamp, duration, enum string, JSON for structured) — conversion helpers
shared with SCR later (`Watchpoint.Kernel.Protobuf`). Round-trip test IHO-U-005 over every
`WpValue` kind.

### 5.2 Requests and timeouts

| Request | Node timeout | Notes |
|---|---|---|
| StartConnection (`spec`, device map) | 30 s | Host loads package (fetching it if missing, §9) and calls `InitializeAsync` + `StartAsync` |
| StopConnection | 20 s | INT gives 15 s; host kills the load context after |
| Describe | 5 min | |
| Test | 45 s | Uses a separate connector instance |
| Execute | command deadline + 5 s | Cancellation sent as `Request{cancel=id}` at deadline |
| Refresh / Diagnostic | 60 s | |

Lost replies (link drop) → the node treats the request as failed with
`FailureCategory.ConnectionDown`; commands may have been executed: CMD marks them
`Unverified` if not observed (CMD rules), never re-sends automatically.

### 5.3 Bridged context services

| Service | Bridge |
|---|---|
| Sink | Host buffers reports (same S1–S6 rules); sends `ObservationBatch` every 50 ms or 500 items; node ingests through IRT's sink then sends `ObservationAck(seq)` once durable (PIP ack). `FlushAsync` in the host completes when the ack for everything reported before it arrives |
| Device map | Node pushes full map at start, deltas on `Changed` |
| Store | `ContextRequest store.get/set/delete` → node `IConnectionStore` (authoritative, replicated) |
| Secrets | `ContextRequest secret.get(fieldId)` → node checks the field id is declared for that connection, returns the value **only over the encrypted link**; host keeps it in a pinned buffer, zeroed after use; never cached longer than the `Use*` call |
| Health, metrics, logging | Streamed; node re-emits with connection/host tags. Log events carry the host id and are capped at 200 events/s per connection (excess counted) |
| Inbound HTTP | Node hosts `https://<node>/integrations/<connectionId>/<path>`, authenticates as declared, forwards as `InboundCall`, waits ≤ 30 s for the reply |
| Inbound TCP | Isolated: listener runs in the host process (port declared by manifest). Remote: listener runs on the **remote machine** (that is the point of running near the devices) |
| Scheduler, gate, HTTP, time | Local to the host |

### 5.4 Sessions and resume

- `Hello` carries `resumeToken` and `lastNodeSeq` when reconnecting. Within 2 minutes (isolated) or
  `Hosts:ResumeWindow` (remote, default 24 h) the node resumes: running connections keep running
  in the host; un-acked observations are re-sent from the host's buffer; requests outstanding at
  the drop are failed (§5.2).
- After the window, or after a node restart, the node sends `Welcome{resume=false}` and restarts
  every connection assigned to that host from scratch.

## 6. Isolated hosts (T4.01)

### 6.1 Grouping

```csharp
public enum IsolationGrouping { PerPackage /* default */, PerConnection, Named }
```

Connection setting `isolationGroup` (`PerPackage` → key `pkg:<packageId>@<version>`;
`PerConnection` → `conn:<id>`; `Named` → `grp:<name>`). One host process per key; created on first
connection, stopped 60 s after its last connection stops.

### 6.2 Process supervisor (`ProcessSupervisor`, reused by SCR)

- Start: `<install>/bin/watchpoint-integration-host[.exe] --isolated --pipe <name> --token <one-time>`
  (pipe/socket ACL: service account only; token single-use, 30 s). Architecture variant: if the
  package manifest declares `hostArchitecture: x86`, the x86 host build is used (Windows only).
- Readiness: `Hello` within 15 s, else kill and count a failure.
- Liveness: `HostStatus` every 10 s; missing for 30 s → host **hung** → kill (process tree) →
  restart.
- Restart policy: delays 1, 2, 4, 8, 16, 32, 60 s; **crash loop** = 5 failures in 10 min → host
  `Faulted` for 10 min (connections `Faulted` with reason `irt.host.crash_loop`), system event
  `integrations.hostCrashLoop` (alarm-able), then one retry; manual "restart host" available.
- Crash: exit code and last 200 lines of stderr captured into the host's event timeline;
  connections restarted after the host comes back (IRT connection state `Reconnecting` with
  reason `hostRestarted`).
- Service stop: `Shutdown` → host stops connections (20 s) → exits; killed after 30 s.

### 6.3 Resource limits

| Setting (per group, defaults from manifest `limits`) | Windows | Linux |
|---|---|---|
| `memoryLimitMb` (default 1024) | Job Object `JOB_OBJECT_LIMIT_PROCESS_MEMORY` + kill on job close | cgroup v2 `memory.max` under the service's delegated slice; fallback `RLIMIT_AS` ×2 with warning 1955 |
| `cpuWeight` (default normal) | Process priority class (BelowNormal for `low`) | cgroup `cpu.weight` (fallback `nice`) |
| Exceeding memory | Process killed by OS → counted as crash with reason `memoryLimit` | same |

`ResourceLimiter` chosen at start-up by capability probe; the choice is shown in host details.

### 6.4 Performance targets (to be confirmed by spike T0.11)

| Metric | Target |
|---|---|
| Observation throughput per isolated host | ≥ 50,000 property reports/s |
| Added latency report → event log (p99) | ≤ 5 ms over in-process |
| Command round-trip overhead | ≤ 2 ms |
| Host start to Ready | ≤ 2 s (excluding package load) |
| Idle memory of an empty host | ≤ 60 MB |

## 7. Host registry and API (T4.01)

```csharp
public sealed record HostInfo(HostId Id, HostKind Kind, LocalizedName Name, HostState State, string? Version, string? Os, string? Arch,
    string? Machine, DateTimeOffset? LastSeen, IReadOnlyList<ConnectionId> Connections, HostResourceStatus? Resources,
    string? IsolationGroupKey /* isolated */, HostId? BackupHost /* remote */, bool Enabled);

public interface IHostRegistry
{
    IReadOnlyList<HostInfo> All { get; }
    Result<HostInfo> Get(HostId id);
    event Action<HostInfo>? Changed;
    Task<Result> RestartAsync(HostId id, UserId actor, CancellationToken ct);      // isolated: kill+start; remote: Shutdown(restart)
    Task<Result> SetEnabledAsync(HostId id, bool enabled, UserId actor, CancellationToken ct);
}
```

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/hosts` / `{id}` | `config.integrations.view` | Hosts with state, resources, connections |
| `POST /api/v1/hosts/{id}/restart` | `config.integrations.operate` | |
| `PATCH /api/v1/hosts/{id}` | `config.integrations.edit` | name, enabled, backup host |
| `DELETE /api/v1/hosts/{id}` | `config.integrations.edit` | Remote only; refused while connections assigned (`iho.host.in_use`); revokes certificate |
| `POST /api/v1/hosts/pairing-codes` | `config.integrations.edit` | Remote pairing code (§8) |
| `GET /api/v1/hosts/{id}/timeline` | `config.integrations.view` | Starts, crashes, link drops |

Live topic `hosts` (state, resources). Data `wp_integrations.hosts` (id, kind, name jsonb,
enabled, machine, os, arch, version, cert_thumbprint, cert_not_after, backup_host,
isolation_group_key, last_seen, row_version) and `host_events` (host_id, at, kind, details jsonb).

## 8. Remote hosts: pairing (T4.02)

1. Configurator creates a pairing code: 10 characters Crockford base32 + check character,
   valid 15 min, single use, bound to an optional name and expected machine name; audit
   `integrations.hostPairingCodeCreated`.
2. On the remote machine: `watchpoint-integration-host pair <code> https://<cluster-address>`.
   The host shows the site CA fingerprint fetched from the address and asks for confirmation
   (or `--ca-fingerprint` for unattended installs; mismatch aborts).
3. Host generates an ECDSA P-256 key (stored with DPAPI machine scope on Windows / file `0600`
   owned by the host service user on Linux), sends `PairRequest { code, csr, machine, os, arch,
   version }` to `POST /api/v1/hosts/pair` (anonymous endpoint, rate-limited 10/min per IP,
   code attempts limited 5 per code).
4. Node validates code → `SEC.IssueFromCsrAsync(csr, spec{ CN = host id, usage Client, validity
   1 year })` → returns certificate chain, host id, cluster address list → host stored as
   `Remote`, state `Unavailable` until the first session; audit `integrations.hostPaired`.
5. Certificates renew automatically at 2/3 of validity over the existing session (`RenewCertificate`
   request carrying a new CSR).

`RemoteHostAuthenticator`: TLS client certificate must chain to the site CA, have a host id CN
known, enabled and not revoked (revocation list = deleted hosts, checked per handshake).

## 9. Remote hosts: packages and placement (T4.02)

- Packages are **pushed by request**: on `StartConnection`, if the host's cache lacks
  `(id, version, sha256)`, it calls `FetchPackage` and verifies the signature **again** locally
  with the same trusted keys (embedded at build time + site keys sent in `Welcome`). Cache keeps
  the last 3 versions per package.
- A connection with `HostMode = Remote` names `HostId`; optional **backup host**: if the primary
  is `Unavailable` for `Hosts:FailoverDelay` (default 2 min) the connection starts on the backup
  host; it moves back only manually (avoids flapping). Store state is node-side so cursors
  survive the move.
- Remote hosts connect to the first reachable address in their list (cluster nodes in M9;
  single node before) with 5 s connect timeout and backoff 1…60 s.

## 10. Remote hosts: buffering (T4.02)

- `ObservationBuffer` (SQLite WAL in `<hostData>/buffer.db`): every observation batch is written
  before being sent; deleted when acked.
- Limits `Hosts:BufferMaxMb` (default 512) and `BufferMaxAge` (default 7 days). When full:
  property reports are **coalesced latest-per-(device, path)** (older values for the same path
  are dropped and counted), events are kept until the age limit; oldest events dropped first
  beyond it with a counter reported at reconnect (`bufferDropped`), shown as a warning in host
  details and a system event.
- While the link is down, connectors keep running (the host is autonomous): commands are
  impossible (node unreachable) and `FlushAsync` in the host completes when data is **in the
  buffer** (durable locally) — so S4 (flush before cursor) still holds.
- On reconnect: replay in sequence order at up to 20,000 observations/s; observations keep their
  `sourceTime` (the buffered time when the connector gave none), so histories are complete and
  ordered by source time; STA applies its normal rule (older observations do not overwrite newer
  values) so states converge to the latest values. Replayed events older than
  `Hosts:ReplayAlarmCutoff` (default 15 min) are flagged `replayed` in their payload metadata
  and ALM does not raise new event-type alarms for them (they appear in the event journal;
  ALM §5, test ALM-U-126).

## 11. Settings

| Key | Default |
|---|---|
| `Hosts:Isolated:ReadyTimeout` / `LivenessTimeout` | 15 s / 30 s |
| `Hosts:Isolated:IdleStopDelay` | 60 s |
| `Hosts:CrashLoop:Count` / `Window` / `Cooldown` | 5 / 10 min / 10 min |
| `Hosts:ResumeWindow` (remote) | 24 h |
| `Hosts:FailoverDelay` | 2 min |
| `Hosts:BufferMaxMb` / `BufferMaxAge` | 512 / 7 days |
| `Hosts:ReplayAlarmCutoff` | 15 min |
| `Hosts:PairingCodeLifetime` | 15 min |

## 12. Errors

`iho.host.not_found`, `iho.host.unavailable`, `iho.host.in_use`, `iho.host.crash_loop`,
`iho.host.start_failed`, `iho.pairing.code_invalid`, `iho.pairing.code_expired`,
`iho.pairing.rate_limited`, `iho.package.transfer_failed`, `iho.package.signature_invalid`,
`iho.protocol.version_unsupported`, `iho.link.timeout`.

## 13. Observability

| EventId | Level | Message |
|---|---|---|
| 1950 | Information | Host {HostId} ({Kind}) ready, version {Version}, pid {Pid} |
| 1951 | Warning | Host {HostId} exited with code {ExitCode} ({Reason}); restart in {Delay} |
| 1952 | Error | Host {HostId} crash loop; faulted for {Cooldown} |
| 1953 | Warning | Host {HostId} hung (no status for {Elapsed}); killing |
| 1955 | Warning | Resource limiter {Limiter} unavailable; using {Fallback} |
| 1960 | Information | Remote host {HostId} paired from {Machine} |
| 1961 | Information | Remote host {HostId} session {Resumed} from {Address} |
| 1962 | Warning | Remote host {HostId} link lost |
| 1963 | Warning | Remote host {HostId} dropped {Count} buffered observations |
| 1970 | Information | Connection {ConnectionId} failed over to backup host {HostId} |

Metrics: `watchpoint.hosts.state` (gauge per host), `watchpoint.hosts.restarts` (counter),
`watchpoint.hosts.link.rtt` (histogram), `watchpoint.hosts.buffer.bytes` (gauge),
`watchpoint.hosts.observations` (counter).

## 14. Security

- Pipe/socket restricted to the service account; one-time start token; isolated hosts run under
  the same service account (v1) — a separate low-privilege account is a later option.
- Remote: mTLS only, TLS 1.3, site-CA client certificates, revocation on delete, pairing codes
  single-use with attempt limits, CA fingerprint confirmation.
- Secrets travel only on demand over the encrypted link and are never written to the host's disk
  (buffer excludes them; test IHO-U-044).
- Packages re-verified on the remote host; a host never executes unsigned code (dev-signed
  allowed only when the site allows dev packages).

## 15. Tests

Unit tests use an in-memory gRPC transport (`Grpc.Net.Client` with `TestServer`) and the
`FakeConnector` from `Watchpoint.Integration.Testing`.

| Id | Layer | Test |
|---|---|---|
| IHO-U-001 | U | Architecture: host project references only Abstractions, Loading, Kernel |
| IHO-U-002 | U | Proto backward compatibility: field numbers unchanged vs committed descriptor (buf breaking-style check) |
| IHO-U-003 | U | Hello/Welcome handshake, version negotiation, unsupported version refused |
| IHO-U-004 | U | Request/reply correlation, timeouts per request kind |
| IHO-U-005 | U | WpValue protobuf round trip for every kind (incl. decimal precision, timestamps µs) |
| IHO-U-006 | U | Observation batching (50 ms / 500 items) and ack-driven FlushAsync |
| IHO-U-007 | U | S3 unknown devices counted through the bridge |
| IHO-U-008 | U | Device map full push and deltas |
| IHO-U-009 | U | Store bridge get/set/delete and size limits |
| IHO-U-010 | U | Secret bridge: undeclared field refused; value zeroed after use |
| IHO-U-011 | U | Health, metrics, logs streamed with tags; log rate cap |
| IHO-U-012 | U | Inbound HTTP forwarded and replied; 30 s timeout → 504 |
| IHO-U-013 | U | Execute with cancellation at deadline |
| IHO-U-014 | U | Lost reply on link drop → ConnectionDown failure; no automatic resend |
| IHO-U-015 | U | Resume within window keeps connections; after window restarts them |
| IHO-U-016 | U | Grouping keys PerPackage/PerConnection/Named |
| IHO-U-017 | U | Host idle stop after last connection |
| IHO-U-018 | U | Supervisor: start args, one-time token, ready timeout |
| IHO-U-019 | U | Supervisor: liveness timeout kills process tree |
| IHO-U-020 | U | Restart delays sequence |
| IHO-U-021 | U | Crash loop → Faulted, system event, cooldown retry |
| IHO-U-022 | U | Crash captures exit code and stderr tail into timeline |
| IHO-U-023 | U | Shutdown sequence with kill after 30 s |
| IHO-U-024 | U | Limiter selection by capability probe (fakes) |
| IHO-U-025 | U | x86 host build selected for `hostArchitecture: x86` |
| IHO-U-026 | U | ConnectionRuntime host resolution by HostMode; isolatedRequired refuses InProcess |
| IHO-U-027 | U | Features set reported by the host matches the connector's interfaces |
| IHO-U-028 | U | Host registry state changes and live topic |
| IHO-U-029 | U | Hosts API permissions and errors |
| IHO-U-030 | U | Conformance suite INT-C-001…040 passes for C-SIM running in an isolated host (in-memory transport) |
| IHO-U-031 | U | Pairing code format, check character, lifetime, single use |
| IHO-U-032 | U | Pair endpoint rate limit and attempts per code |
| IHO-U-033 | U | CA fingerprint confirmation; mismatch aborts |
| IHO-U-034 | U | CSR issued with host id CN, client usage, 1 year |
| IHO-U-035 | U | Authenticator: unknown, disabled, deleted (revoked) host refused |
| IHO-U-036 | U | Certificate renewal at 2/3 validity |
| IHO-U-037 | U | Package fetch by hash, chunk integrity, local signature verification |
| IHO-U-038 | U | Package cache keeps last 3 versions |
| IHO-U-039 | U | Backup host failover after delay; no automatic move back |
| IHO-U-040 | U | Address list connect order and backoff |
| IHO-U-041 | U | Buffer write-before-send and delete-on-ack |
| IHO-U-042 | U | Buffer limits: property coalescing, event age drop, counters |
| IHO-U-043 | U | FlushAsync offline completes on local durability |
| IHO-U-044 | U | Secrets never written to buffer or host disk |
| IHO-U-045 | U | Replay order and rate; source times kept; `replayed` flag beyond cutoff; ALM skips raising for flagged events |
| IHO-U-046 | U | Delete host refused while in use; revokes certificate |
| IHO-U-047 | U | Remote host service install/uninstall commands (Windows service / systemd unit files generated) |
| IHO-U-048 | U | Dev-signed packages refused on remote host unless site allows |
| IHO-U-049 | U | Audit records for pairing, deletion, restart |
| IHO-U-050 | U | Translations of host states and errors |
| IHO-I-001 | I | Real isolated host process runs C-SIM; states reach the node |
| IHO-I-002 | I | Kill -9 the host: connections back within 5 s; no lost acked observations |
| IHO-I-003 | I | Hung host (test connector blocks all threads) detected and restarted |
| IHO-I-004 | I | Memory limit: test connector allocating beyond limit is killed (Windows Job Object / Linux cgroup or fallback) |
| IHO-I-005 | I | Two packages with conflicting dependency versions in one PerPackage host each |
| IHO-I-006 | I | Throughput and latency targets (§6.4) benchmark |
| IHO-I-007 | I | Commands through isolated host with verification (CMD end-to-end) |
| IHO-I-008 | I | Service stop shuts hosts down cleanly |
| IHO-I-009 | I | Pair a remote host (second container) with code; session established over mTLS |
| IHO-I-010 | I | Remote host receives package push and runs C-SIM |
| IHO-I-011 | I | Network cut 10 min (Toxiproxy container `ghcr.io/shopify/toxiproxy`, added to `Watchpoint.Testing.Containers` as `ToxiproxyFixture` by T4.02): host buffers, replays on reconnect; event count equal |
| IHO-I-012 | I | Buffer overflow behaviour with small limit |
| IHO-I-013 | I | Backup host failover |
| IHO-I-014 | I | Revoked host cannot reconnect |

## 16. Tasks

| Task | Sections | Tests |
|---|---|---|
| T4.01 | §4–§7, §11–§14 (isolated parts) | IHO-U-001…IHO-U-030, IHO-I-001…IHO-I-008 |
| T4.02 | §8–§10 | IHO-U-031…IHO-U-050, IHO-I-009…IHO-I-014, ALM-U-126 |
