# CLU — Cluster (redundancy and scale-out)

| | |
|---|---|
| Spec id | CLU |
| Projects | `src/service/Watchpoint.Cluster`, `tools/wpwitness` (witness packaging), `deploy/ha/` (PostgreSQL HA templates) |
| Milestone | M9 |
| Tasks | T9.01 (§4–§6), T9.02 (§7–§8), T9.03 (§9–§10), T9.04 (§11) |
| Event id range | 3500–3599 (Cluster/Federation range; FED uses 3600–3699) |
| Spike | T0.09 decision record (JetStream KV CAS as lease store) |
| Design docs | [Redundancy, scale-out and multi-site §1–§3](../../docs/modules/redundancy-and-multi-site.md) |

## 1. Purpose and scope

Make one site run on **several nodes** as one system:

- membership (join with pairing, node identities, roles, health), witness;
- **leases with fencing tokens** in JetStream KV for every owned task: pipeline partitions,
  connections, singletons (schedulers, retention jobs, notification workers' leadership…);
- failover of partitions, connections, command queues and timers in < 30 s without lost events
  and **without split brain** (a node without majority stops acting);
- placement of connections and partitions by roles and load, drain and rolling upgrades;
- client node lists; file replication; PostgreSQL HA integration.

Not in scope: multi-site links (FED).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST, PIP (NATS connection, JetStream KV/Object Store, partition workers), STO (cluster DB tables), SEC (node certificates, cluster join, shared signing keys distribution), IRT/IHO (connection runtimes, hosts), CMD (dispatch fencing), FIL (`IFileReplicator`), LIC (node licensing), AUD | PIP (partition ownership), IRT (connection ownership), CMD, ALM/AUT/SCH/NOT/RPT (singletons), API (node list, `front` role), HLT, DEP (rolling upgrades) |

## 3. Layout

Log event ids: 3500–3599 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Cluster/
├── Contracts/
│   ├── NodeInfo.cs, NodeRole.cs, NodeState.cs, IClusterMembership.cs
│   ├── LeaseKey.cs, Lease.cs, FencingToken.cs, ILeaseManager.cs, IOwnership.cs
│   ├── IPlacementEngine.cs, PlacementConstraint.cs
│   └── ClusterErrors.cs
├── Internal/
│   ├── Membership/MembershipService.cs (KV bucket wp_members), JoinService.cs, QuorumMonitor.cs
│   ├── Leases/JetStreamLeaseManager.cs (KV bucket wp_leases), LeaseRenewer.cs
│   ├── Ownership/PartitionOwnership.cs, ConnectionOwnership.cs, SingletonOwnership.cs
│   ├── Placement/PlacementEngine.cs, LoadReporter.cs, Rebalancer.cs
│   ├── Drain/DrainService.cs, Upgrade/VersionCompatibility.cs
│   ├── Files/ObjectStoreReplicator.cs (FIL IFileReplicator over JetStream Object Store)
│   └── Storage/ClusterDbContext.cs
├── ClusterModule.cs, ClusterLog.cs
```

## 4. Membership (T9.01)

```csharp
public enum NodeRole { Front, Integration, Pipeline, History, Media }      // flags; default all
public enum NodeState { Joining, Active, Draining, Standby /* licence or majority */, Isolated, Left }
public sealed record NodeInfo(NodeId Id, string Name, IReadOnlySet<NodeRole> Roles, NodeState State, string Version,
    IReadOnlyList<Uri> Endpoints /* https, nats */, string Os, DateTimeOffset LastHeartbeat, NodeLoad Load);

public interface IClusterMembership
{
    NodeId Self { get; }
    IReadOnlyList<NodeInfo> Members { get; }
    bool HasQuorum { get; }                                    // NATS cluster meta-leader reachable and KV writes succeed
    event Action<MembershipChange>? Changed;
}
```

- **NATS cluster**: every node runs embedded-supervised `nats-server` (as in M1) configured as a
  cluster member (routes to peers, `jetstream: { store_dir }`, cluster name = site id); the
  **witness** (`tools/wpwitness`: a packaged `nats-server` + tiny health agent, Windows service /
  systemd / container) is a third JetStream member with `--js` but no Watchpoint role. Streams
  and KV buckets replicated R3 (two nodes + witness, or ≥ 3 nodes).
- **Join**: an admin creates a join token on an existing node (`POST /api/v1/cluster/join-tokens`,
  15 min, single use); the new node's setup (`wpctl cluster join <address> <token>`) obtains the
  site CA-signed node certificate (SEC `IssueFromCsrAsync`), the cluster secrets bundle (master
  key wrapped for the new node through an ECDH exchange, signing keys), NATS routes, and DB
  connection settings; then appears `Joining` → `Active` after its first successful heartbeat and
  licence check (LIC `NodeNotLicensed` → `Standby` for 14 days).
- **Heartbeat**: each node writes `wp_members.<nodeId>` every 2 s (roles, state, version, load:
  CPU, memory, partitions owned, connections owned, devices, event rate). Members not seen for
  10 s are shown `Unreachable` (their leases expire on their own).
- **Quorum monitor**: if the node cannot write to KV for 10 s (`Cluster:QuorumLossTimeout`; no JetStream majority), it enters
  **Isolated**: stops partition workers, stops connections, refuses commands
  (`cluster.no_quorum`), API stays up read-only with a banner; recovers automatically when writes
  succeed again (re-acquires leases normally).

## 5. Leases and fencing (T9.01)

```csharp
public readonly record struct LeaseKey(string Value);       // "partition.17", "connection.<id>", "singleton.scheduler"
public readonly record struct FencingToken(ulong Value);    // KV revision of the acquisition
public sealed record Lease(LeaseKey Key, NodeId Owner, FencingToken Token, DateTimeOffset ExpiresAt);

public interface ILeaseManager
{
    ValueTask<Result<Lease>> TryAcquireAsync(LeaseKey key, TimeSpan ttl, CancellationToken ct);   // CAS create or take-over if expired
    ValueTask<Result<Lease>> RenewAsync(Lease lease, CancellationToken ct);                       // CAS update on revision
    ValueTask ReleaseAsync(Lease lease, CancellationToken ct);
    Lease? Get(LeaseKey key);                                                                       // watched cache
    IAsyncEnumerable<LeaseChange> WatchAsync(string prefix, CancellationToken ct);
}
```

- Modules that need "one per cluster" work (PIP registry outbox publisher, later HIS consumers, NOT
  delivery, SCH timers) depend only on the Kernel `ILeaseGate` (K §6.1); CLU registers its
  implementation backed by this lease manager (`WaitUntilHeldAsync` returns while the lease is held,
  its token is cancelled when the lease is lost). Single-node and M1 installs use the always-held gate.
- Store: JetStream KV bucket `wp_leases` (R3, history 1). Value `{ owner, expiresAt, acquiredAt }`.
  **Acquire**: `Create` (key absent) or `Update(expectedRevision)` when the current value's
  `expiresAt` < now − clock skew allowance (2 s); the new KV revision is the **fencing token**
  (monotonic per key across the cluster).
- TTL 15 s, renewal every 2 s (`LeaseRenewer` batches renewals); renewal failure for the TTL →
  the owner **must** stop the work before `expiresAt` (local deadline = last successful renewal +
  TTL − 2 s), guaranteeing no overlap with a new owner when clocks are within 2 s (NTP required;
  HLT checks skew).
- Every side effect that must not be duplicated carries the token: CMD dispatch
  (`IConnectionCommandChannel.ExecuteAsync(…, fencingToken)` — IRT rejects tokens lower than the
  connection's current lease token), partition record writes (PIP adds header `Wp-Fence` and the
  partition writer checks it against `IOwnership.CurrentToken(partition)` before publishing; JetStream
  publish with `Nats-Expected-Last-Subject-Sequence` **and**
  `Nats-Expected-Last-Subject-Sequence-Subject: wp.log.<pp>.>` prevents two writers interleaving —
  the exact-subject check alone does not, a partition has one subject per kind), singleton jobs
  (DB writes include the token in a `cluster_fences` check).
- Fenced partition writes ([ADR-001](../decisions/ADR-001-nats-jetstream-event-log-and-leases.md)):
  preferably one **atomic batch** per worker batch (expected-sequence headers on the first
  message), at most 32 concurrently open batches per stream (64 fail: server in-flight batch limit);
  otherwise per-record fenced publishes. On `10071`/`10164` "wrong last sequence" the writer reads
  the last record on `wp.log.<pp>.>`: its own record id → adopt that sequence; equal to the
  expected sequence → retry; otherwise → fenced out (`cluster.fence.stale_token`, stop the worker).
- Measured (ADR-001): a stream leader loss blocks KV writes for 5–7.5 s and log appends for up
  to ~12 s; TTL, renewal and quorum timeouts are sized so that a leader failover alone never
  makes owners stop.

## 6. Ownership (T9.01)

```csharp
public interface IOwnership
{
    IReadOnlySet<int> OwnedPartitions { get; }
    bool OwnsConnection(ConnectionId id);
    bool IsSingletonOwner(string singleton);
    FencingToken? CurrentToken(LeaseKey key);
    event Action<OwnershipChange>? Changed;      // gained/lost (with token)
}
```

Integration points (implemented in the owning modules by T9.01/T9.02):

| Module | Change |
|---|---|
| PIP | `PartitionWorkerHost` starts/stops workers on ownership changes (M1 behaviour = own all); on gain: restore snapshot + replay tail (PIP recovery path), then live |
| IRT | `ConnectionManager` runs only owned connections; on gain: start connection (resync, catch-up); on loss: stop within the deadline |
| CMD | Command queues are per partition → follow partition ownership; dispatch carries the connection lease token |
| ALM/AUT timers | In partition state → restored by replay on gain |
| Singletons | `scheduler` (SCH triggers), `retention` (HIS/AUD/FIL jobs), `reports.schedules`, `maintenance.timers`, `licensing.checkin`, `notifications.dispatch` (workers can run on all nodes thanks to SKIP LOCKED — singleton only for summariser windows) |

## 7. Failover (T9.02)

Sequence on node loss (A owns partitions P and connections C):

1. A stops renewing; within 15 s its leases expire.
2. `Rebalancer` on each remaining node watches `wp_leases`: for expired keys, the **placement
   engine** (§9) proposes the target; the target node acquires (CAS); others back off.
3. Partition gain on B: snapshot restore + replay of the tail since the last snapshot
   (`State:SnapshotInterval`, 5 s; target < 10 s for up to 1.5 M records — ADR-001 measured
   ≈ 180k records/s whole-log replay, so "1 h of tail" at 20,000 records/s would take minutes), start live consumption; **commands** pending in those partitions are
   resumed by CMD (pending → re-evaluated: not yet dispatched → dispatched with B's tokens;
   dispatched but not completed → `Unverified` reconciliation per CMD rules, never re-sent
   blindly).
4. Connection gain on B: start connector (in-process/isolated per host mode; remote hosts
   reconnect to B via their address list), `IStateRefresh` + `IEventCatchUp` from the replicated
   connection store cursors → no lost events from systems that support catch-up.
5. Clients reconnect to other nodes (API node list §10), live streams resume from sequences
   (API §8).

Targets (verified by CLU-I tests): failover < 30 s p95 for 2,000 devices and 20 connections; zero
lost events from catch-up-capable connectors; zero duplicate command dispatch.

## 8. Split brain protection (T9.02)

- Isolated node (no quorum): stops acting before its leases could be taken (§5 deadline).
- Old owner coming back with stale state: its writes fail fencing checks; it observes lease loss
  and stops.
- Connector-level protection: for vendor systems allowing one client session, the new owner's
  connection may kick the old one — acceptable since the old owner is fenced.
- Tests inject partitions with Toxiproxy between nodes/witness (CLU-I).

## 9. Placement (T9.03)

```csharp
public interface IPlacementEngine
{
    NodeId? ChooseOwner(LeaseKey key, PlacementContext context);       // deterministic given members/loads
    IReadOnlyList<PlacementMove> Rebalance(PlacementSnapshot snapshot); // minimal moves
}
public sealed record PlacementConstraint(LeaseKey Key, IReadOnlySet<NodeId>? PinnedTo, string? RequiresOs, NodeRole RequiredRole, NodeId? Avoid);
```

- Eligibility: node `Active`, has the role (`Integration` for connections, `Pipeline` for
  partitions), satisfies constraints (connection pin, package `platforms`/OS, remote host
  affinity: remote-host connections prefer the node the host is connected to).
- Score: weighted load (devices owned, observed events/s, CPU) — choose the lowest score; ties by
  node id (deterministic).
- Rebalance (every 5 min or on join/leave/drain): move only when a node's load exceeds the mean by
  > 25 %, at most 2 moves per minute (moves cost a failover each), never during active alarm
  floods (PIP backlog > threshold).

## 10. Drain, rolling upgrade, client node lists (T9.03)

- **Drain** (`POST /api/v1/cluster/nodes/{id}/drain`, `config.system.cluster`): node state
  `Draining`; leases released one by one with handover (new owner acquires, then old stops —
  zero-gap order: old stops consumption at a known sequence, releases, new starts from there);
  when empty → `Drained` (ready for maintenance). `undrain` returns to `Active`.
- **Rolling upgrade**: versions within the same minor (or N/N+1 minor) can coexist; schema
  migrations are backward compatible for one minor (expand/contract rule, STO); the newest node
  never takes singletons that require the new schema until all nodes report it (version gates in
  `VersionCompatibility`); DEP drives: drain → upgrade → undrain per node.
- **Client node list**: `/auth/sign-in` and `/auth/refresh` responses include `nodes: [{ url,
  roles }]` (front nodes) — UIC live client reconnect logic tries them in order of last success;
  API `GET /api/v1/cluster/nodes/public`. Optional virtual IP/DNS documented in deployment.
- **File replication**: `ObjectStoreReplicator` stores blobs in JetStream Object Store bucket
  `wp_files` (R3, chunked) on `OnStoredAsync`, fetches missing ones on demand; large exports
  (> 1 GB) are replicated lazily (on read) to save bandwidth.

## 11. PostgreSQL HA (T9.04)

- Spike compares **Patroni** (needs etcd/consul: run etcd on the same three members, etcd on the
  witness) and **pg_auto_failover** (monitor on the witness): failover time, operational
  simplicity on Windows and Linux, packaging, licence (both PostgreSQL/BSD-like), behaviour with
  the witness offline → decision record `Architecture/decisions/ADR-0xx-postgres-ha.md`.
- Product integration either way: connection string with multiple hosts and
  `target_session_attrs=read-write` (Npgsql), retry policy on failover errors (STO resilience),
  health checks of replication lag in HLT, backup/restore procedures, witness package installs
  the chosen monitor/etcd.
- SQL Server provider (M11): uses Always On availability groups listener (documented, no CLU code).

## 12. API, settings, errors, observability

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/cluster` | `admin.health.view` | Members, roles, states, quorum, leases summary |
| `GET /api/v1/cluster/leases?prefix=` | `admin.health.view` | Lease owners and tokens |
| `POST /api/v1/cluster/join-tokens` | `config.system.cluster` | Join token |
| `PATCH /api/v1/cluster/nodes/{id}` | `config.system.cluster` | Roles, name |
| `POST /api/v1/cluster/nodes/{id}/drain` / `undrain` / `DELETE` (remove left node) | `config.system.cluster` | |
| `POST /api/v1/cluster/connections/{id}/move` `{ toNode }` | `config.system.cluster` | Manual move (pin optional) |
| `GET /api/v1/cluster/nodes/public` | anonymous | Front node URLs (no internal details) |

| Key | Default |
|---|---|
| `Cluster:LeaseTtl` / `RenewInterval` | 15 s / 2 s (ADR-001) |
| `Cluster:QuorumLossTimeout` | 10 s (ADR-001: leader failover blocks KV writes up to 7.5 s) |
| `Cluster:Rebalance:Interval` / `Threshold` / `MaxMovesPerMinute` | 5 min / 25 % / 2 |
| `Cluster:MaxClockSkew` | 2 s |
| `Nats:ServerName` / `ClusterPort` / `Routes` (HST §4) | `Node:Id` / 6222 / written at join |

Errors: `cluster.no_quorum`, `cluster.join.token_invalid`, `cluster.node.not_found`,
`cluster.node.not_drained`, `cluster.lease.lost`, `cluster.fence.stale_token`,
`cluster.version.incompatible`, `cluster.move.constraint_violation`.

| EventId | Level | Message |
|---|---|---|
| 3500 | Information | Node {Node} joined with roles {Roles} (version {Version}) |
| 3501 | Warning | Node {Node} unreachable |
| 3502 | Critical | Quorum lost; node isolated, all owned work stopped |
| 3503 | Information | Quorum regained |
| 3510 | Information | Lease {Key} acquired by {Node} token {Token} |
| 3511 | Warning | Lease {Key} lost by {Node} |
| 3520 | Information | Failover of {Partitions} partitions and {Connections} connections completed in {ElapsedMs} ms |
| 3521 | Warning | Stale fencing token {Token} rejected for {Key} |
| 3530 | Information | Rebalance moved {Key} from {From} to {To} |

Metrics: `watchpoint.cluster.members` (gauge by state), `watchpoint.cluster.quorum` (gauge),
`watchpoint.cluster.leases.owned` (gauge per node), `watchpoint.cluster.failover.duration`
(histogram), `watchpoint.cluster.fence.rejections` (counter).

## 13. Tests

| Id | Test |
|---|---|
| CLU-U-001…008 | Membership: heartbeat writes, unreachable detection, roles, states, join token lifecycle, certificate issuance and secrets bundle (ECDH wrap) |
| CLU-U-009…012 | Quorum monitor: isolation after timeout, stop actions, read-only API banner, recovery |
| CLU-U-013…022 | Lease manager against embedded NATS: create, CAS take-over after expiry, renewal, release, token monotonic, skew allowance, watch, batch renewals, local deadline stop |
| CLU-U-023…028 | Fencing: CMD/IRT stale token rejection, PIP partition writer check and expected-last-subject-sequence, singleton DB fence |
| CLU-U-029…035 | Ownership events drive PIP worker host, IRT connection manager, singletons; M1 compatibility (single node owns all) |
| CLU-U-036…045 | Failover sequence: expiry detection, placement proposal, CAS race between two candidates, partition restore + replay, command reconciliation (not dispatched vs dispatched), connection restart with cursors |
| CLU-U-046…052 | Split brain: isolated node stops before expiry; returning stale owner fenced; connector kick tolerance |
| CLU-U-053…060 | Client reconnect lists; live stream resume after node loss; auth tokens valid on all nodes |
| CLU-U-061…068 | Placement: eligibility (roles, pins, OS, remote host affinity), scores, determinism, rebalance thresholds and move limits, no moves during floods |
| CLU-U-069…074 | Drain handover zero-gap order; undrain; remove node |
| CLU-U-075…078 | Version compatibility gates and mixed-version window |
| CLU-U-079…080 | Object Store file replicator store/fetch, lazy large files |
| CLU-U-081 | Node with a foreign `Site:Id` (REG §4 R1 mismatch) stays not ready (reason `siteId`) and does not join |
| CLU-U-082 | R3 lane throughput measured (PIP §5.4): records/s per partition and over 64 partitions written to the test output; below 2,000/s per partition the atomic-batch path is required before T9.02 |
| CLU-I-001 | 3-node cluster (2 nodes + witness containers) forms; streams R3 |
| CLU-I-002 | Leases distributed; partitions split across nodes |
| CLU-I-003 | Join a node with a token; certificate and secrets work |
| CLU-I-004 | Witness down: cluster continues with 2 of 3 |
| CLU-I-005 | Clock skew > 2 s detected by HLT warning |
| CLU-I-006 | Lease store throughput: 5,000 leases renewed every 2 s |
| CLU-I-007 | Kill node A under load (simulator 2,000 devices, 200 events/s) → failover < 30 s, no lost events (counts), no duplicate commands |
| CLU-I-008 | Network partition A ↔ {B, W} (Toxiproxy): A isolates, B takes over; heal: A rejoins without conflicts |
| CLU-I-009 | Partition between A and B only, witness reachable by both: single owner per lease |
| CLU-I-010 | Remote host follows its connection to the new owner |
| CLU-I-011 | Alarm timers and escalations continue after failover (FakeTime in test host) |
| CLU-I-012 | Automation run waiting continues on the new owner |
| CLU-I-013 | Clients (desktop/web) reconnect and resume streams |
| CLU-I-014 | File stored on A readable on B after A dies |
| CLU-I-015 | 5 consecutive failovers back and forth: no leaks, no duplicates |
| CLU-I-016…020 | PostgreSQL HA with the chosen tool: primary kill → writes resume < 30 s; replication lag health; witness/monitor loss; backup/restore; Windows and Linux packaging smoke |

## 14. Tasks

| Task | Sections | Tests |
|---|---|---|
| T9.01 | §4–§6, §12 (`ILeaseGate` implementation, NATS cluster keys) | CLU-U-001…CLU-U-035, CLU-U-081, CLU-U-082, CLU-I-001…CLU-I-006 |
| T9.02 | §7–§8 | CLU-U-036…CLU-U-060, CLU-I-007…CLU-I-015 |
| T9.03 | §9–§10 | CLU-U-061…CLU-U-080 |
| T9.04 | §11 | Spike report, CLU-I-016…CLU-I-020 |
