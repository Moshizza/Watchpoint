# STA — Live State

| | |
|---|---|
| Spec id | STA |
| Projects | `src/service/Watchpoint.State` |
| Milestone | M1 |
| Tasks | T1.11 (§4–§7), T1.12 (§8) |
| Design docs | [Core runtime §3](../../docs/modules/core-runtime.md#3-live-state-store) |

## 1. Purpose and scope

The live state store holds the **current value of every property** of every device and
server, their health, and their **overall state**; maintains **aggregate counters** (per
location, server, tag); answers queries; publishes **change notifications** for the live
API; and survives restarts through **snapshots + log replay**.

Not in scope: alarms (ALM), history (HIS), rights filtering (applied by callers with RGT).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, MOD (state summary evaluator), REG (devices, memberships), PIP (subscriber, dispatcher, log reader), STO (state store) | API (queries, live), CMD (verification), ALM (state-based rules), EXP (device state view), SYN, AUT, SCR, FED |

## 3. Project layout

```
Watchpoint.State/
├── Contracts/
│   ├── DeviceState.cs, PropertyState.cs, HealthState.cs
│   ├── IStateReader.cs, StateQuery.cs
│   ├── IStateChangeFeed.cs, StateChange.cs
│   ├── IAggregateReader.cs, AggregateCounts.cs, AggregateScope.cs
│   └── StateErrors.cs
├── Internal/
│   ├── PartitionStateStore.cs         per-partition map, single writer
│   ├── StateSubscriber.cs             IPartitionSubscriber (Order 100)
│   ├── StaleTracker.cs                min-heap of stale deadlines per partition
│   ├── AggregateCounters.cs
│   ├── MembershipTracker.cs           reacts to registry changes
│   ├── RecentChangesRing.cs
│   ├── ChangeFanout.cs
│   ├── Snapshotter.cs, StateRecovery.cs
│   └── StateSerializer.cs
├── StateModule.cs
└── StateLog.cs                        event ids 1700–1799
```

## 4. Model

```csharp
public sealed record PropertyState(PropertyPath Path, WpValue? Value, Quality Quality,
    DateTimeOffset? SourceTime, DateTimeOffset? ReceivedTime, DateTimeOffset? LastSeen,
    long Sequence, CommandId? CausedBy);
// Value null = never received (unknown)

public sealed record HealthState(Connectivity Connectivity, IReadOnlyList<string> Faults,
    string? Reason, DateTimeOffset? LastContact, DateTimeOffset? Since);

public sealed record LastEvent(EventPath Path, DateTimeOffset Time, Severity Severity, long Sequence);

public sealed record DeviceState(
    Guid EntityId,                       // DeviceId or ServerId value
    EntityKind Kind,                     // Device | Server
    TypeRef Type,
    IReadOnlyDictionary<PropertyPath, PropertyState> Properties,
    HealthState Health,
    StateSummary Summary,                // from MOD
    bool ServerUnavailable,              // the hosting server/connection is offline or unknown
    IReadOnlyDictionary<EventPath, LastEvent> LastEvents,   // last occurrence per event path
    long Sequence,                       // last log sequence applied to this entity
    DateTimeOffset UpdatedAt);
```

Effective quality seen by readers: if `ServerUnavailable` is true, every property is reported
with quality `Stale` (stored quality unchanged), so old values are never shown as current.

## 5. Public contract

```csharp
public interface IStateReader
{
    Result<DeviceState> Get(Guid entityId);
    IReadOnlyList<DeviceState> GetMany(IReadOnlyCollection<Guid> entityIds);   // missing ids omitted
    Page<DeviceState> Query(StateQuery query, PageRequest page);
    long CurrentSequence { get; }                                               // max applied over partitions
}

public sealed record StateQuery(DeviceQuery Devices /* REG filters */, IReadOnlyList<OverallState>? States = null,
    PropertyCondition? Where = null);
public sealed record PropertyCondition(PropertyPath Path, WpValue EqualsValue);   // e.g. doorPosition.position == forced

public interface IAggregateReader
{
    AggregateCounts Get(AggregateScope scope);
    IReadOnlyDictionary<AggregateScope, AggregateCounts> GetMany(IReadOnlyCollection<AggregateScope> scopes);
}
public readonly record struct AggregateScope(AggregateScopeKind Kind, Guid Id);   // Location (subtree), Server (incl. child servers), Tag, Site
public sealed record AggregateCounts(int Total, IReadOnlyDictionary<OverallState, int> ByState);

public interface IStateChangeFeed
{
    // Non-blocking fan-out; each listener has a bounded queue (capacity given) and is told when it overflowed.
    IStateChangeListener Subscribe(string name, int capacity);
    // Changes since a sequence for resync; null if the ring no longer contains that sequence.
    IReadOnlyList<StateChange>? GetChangesSince(long sequence, int max);
}
public interface IStateChangeListener : IDisposable
{
    ChannelReader<StateChange> Changes { get; }
    bool Overflowed { get; }            // reset when read after overflow; listener must resync
}
public sealed record StateChange(Guid EntityId, long Sequence, IReadOnlyList<PropertyPath> ChangedProperties,
    bool HealthChanged, OverallState? NewOverallState /* when changed */, DateTimeOffset At);
```

## 6. Internal design

### 6.1 Single writer per partition

- One `PartitionStateStore` per partition: `ConcurrentDictionary<Guid, DeviceState>` (readers on
  any thread, writer only the partition worker). `DeviceState` instances are immutable;
  updates replace the entry. Readers always see a complete state of one entity.
- `StateSubscriber` (`IPartitionSubscriber`, Order 100) applies records:

| Record kind | Effect |
|---|---|
| `property` | Replace `PropertyState` (value, quality, times, sequence, causedBy from `corr.command`) |
| `event` | Update `LastEvents[path]` |
| `health` | Replace `Health`; `Since` set when connectivity changes; for **servers**: recompute `ServerUnavailable` of all devices of that server (and child servers) — work posted to each affected device's partition through `IPartitionDispatcher` |
| touch (`OnTouched`) | Update `LastSeen`; if quality was `Stale` because of `staleAfter`, restore stored quality |

  After applying a batch, recompute the summary (MOD `IStateSummaryEvaluator`) for each entity
  touched, update aggregates if the overall state changed, push changes to the fan-out and the
  recent-changes ring.

### 6.2 Staleness

- `StaleTracker` per partition: min-heap of `(deadline, entity, path)` for properties whose
  definition has `StaleAfter`; deadline = `LastSeen + StaleAfter`.
- `OnTick` pops expired entries; if still expired (not refreshed), set quality `Stale`,
  recompute summary, emit `StateChange`. Staleness is **not** logged as a record (derived
  state; reproducible from times after recovery: recovery recomputes it on first tick).

### 6.3 New and deleted entities

- STA listens to `IRegistryReader.Changed`. For each created device/server: post to its
  partition an initialisation (all properties unknown — or the definition's `Default` — health
  `Unknown`, summary computed). For deleted ones: remove state, adjust aggregates.
- Type version changes: properties no longer in the type are dropped; new ones initialised.
- Flags (`Enabled`, `Maintenance`) and memberships (location, tags, server) changes: posted
  to the partition; summary recomputed (flags) and aggregates moved (memberships).

### 6.4 Aggregates

- Each entity remembers its current **membership set** (location ancestors incl. self
  location, server and its ancestors, tags, site) and current overall state.
- On overall-state change A → B: for each scope in the membership set, `Interlocked`
  decrement counter A, increment B.
- On membership change: decrement all old scopes for the current state, increment new ones.
- Counters: `ConcurrentDictionary<AggregateScope, int[]>` (index = `OverallState`). `Total`
  = sum.
- Consistency check: a background job every 10 minutes recomputes all counters from scratch
  on a copy and logs 1730 if a drift is found (then replaces). Must never happen; it is a
  safety net.

### 6.5 Change fan-out and resync ring

- `ChangeFanout`: listeners registered with bounded channels (`DropOldest` is **not** used —
  instead, when full, the listener is marked `Overflowed` and new changes are discarded for it
  until it drains; the listener then resyncs via queries). This keeps the writer non-blocking.
- `RecentChangesRing`: lock-free ring of the last `State:RecentChanges` (default 200,000)
  `StateChange`s ordered by sequence; `GetChangesSince(s)` returns changes with sequence > s
  if s ≥ oldest retained, else null.

### 6.6 Queries

- `Query` asks REG for device ids matching `Devices` (with `Within` for rights), then filters
  by state/condition in memory and pages. For `States`/`Where`-only queries over all devices,
  scanning 50,000 states takes < 20 ms (STA-U-031).

## 7. API (owned by STA)

| Method and path | Permission (M3) | Description |
|---|---|---|
| `GET /api/v1/devices/{id}/state` | `operate.devices.view` + scope | Device state (JSON §7.1) |
| `POST /api/v1/states/batch` | same | Body `{ "ids": [...] }` (≤ 1,000) → states |
| `GET /api/v1/states?…REG filters…&state=alarm,fault&where=doorPosition.position:forced` | same | Paged states with device summaries |
| `POST /api/v1/aggregates` | same | Body `{ "scopes": [{ "kind": "location", "id": "…" }] }` (≤ 500) → counts |

Live stream topics `devices` and `aggregates` are served by API using `IStateChangeFeed`
(API §8).

### 7.1 JSON form

```json
{
  "id": "0192…", "kind": "device", "type": { "id": "ac.door", "version": "1.0.0" },
  "overall": "alarm", "underlying": "alarm", "cause": "doorPosition.position",
  "serverUnavailable": false,
  "health": { "connectivity": "online", "faults": [], "lastContact": "…", "since": "…" },
  "properties": {
    "doorPosition.position": { "value": { "k": "enum", "v": "forced" }, "quality": "good",
                               "sourceTime": "…", "receivedTime": "…", "lastSeen": "…", "sequence": 1234 }
  },
  "lastEvents": { "doorPosition.forced": { "time": "…", "severity": "alarm" } },
  "sequence": 1234, "updatedAt": "…"
}
```

## 8. Snapshots and recovery (T1.12)

### 8.1 Snapshotter

- Every `State:SnapshotInterval` (default 5 s) and at shutdown (`IShutdownTask`, phase
  Runtime), for each owned partition: post to the partition worker a job that collects the
  **dirty set** (entities changed since the last snapshot) and serialises them
  (`StateSerializer`, compact JSON) together with the partition's current sequence; then a
  background writer calls `IStateStore.WriteSnapshotAsync` (consumer `state`, kind `device`,
  batches of ≤ 5,000, checkpoint in the last batch).
- If writing fails, the dirty set is merged back and retried next interval (log 1720).
- Other modules with per-partition in-memory state (CMD pending commands, ALM active alarms
  and rule windows) plug into the same cadence through:

```csharp
public interface ISnapshotParticipant
{
    string Consumer { get; }                      // "commands", "alarms"
    string Kind { get; }                          // state item kind
    // Called on the partition worker: return dirty items since the last call (serialised) and deletions.
    SnapshotContribution Collect(int partition);
    void Restore(int partition, IReadOnlyList<StateItem> items);   // during recovery, before replay
}
public sealed record SnapshotContribution(IReadOnlyList<StateItem> Upserts, IReadOnlyList<StateItemKey> Deletes);
```

  Each participant writes its own checkpoint (consumer name) in the same transaction pattern.
- Persisted payload excludes derived fields (`Summary`, `ServerUnavailable`, stale-derived
  quality) — they are recomputed at recovery.

### 8.2 Recovery (startup phase `Recovery`)

Start-up task `StateRestore` (phase `Recovery`, order 50, before `PipelineRecovery` at order 100, PIP §13);
other snapshot participants (`ISnapshotParticipant`: CMD, ALM) restore inside it.

1. Read checkpoints (`consumer = state`) and all `device` items for owned partitions →
   rebuild `PartitionStateStore`s (entities no longer in REG are discarded; entities in REG
   without a snapshot are initialised as new).
2. Report checkpoints to PIP (`IPartitionSubscriber.GetRecoveryCheckpoint`); PIP replays records after them (PIP §7.4); STA applies them.
3. Recompute summaries, `ServerUnavailable`, aggregates from scratch.
4. Provide current values to PIP's last value cache (`ILastValueSource.ProvideLastValues`, called by PIP after the replay, just before `OnRecoveryCompleted`).
5. Mark readiness for the State module.

Target: 25,000 devices with 10 properties each recovered in < 60 s including replay of
1 million records (STA-I-005).

## 9. Settings

| Key | Default | Range |
|---|---|---|
| `State:SnapshotInterval` | `PT5S` | PT1S–PT5M |
| `State:RecentChanges` | 200000 | 10,000–5,000,000 |
| `State:ListenerDefaultCapacity` | 10000 | |

## 10. Errors

| Code | Kind | When |
|---|---|---|
| `state.entity.not_found` | NotFound | No state for id (unknown or deleted) |
| `state.query.too_many_ids` | Validation | Batch over limit |
| `state.query.invalid_condition` | Validation | `where` path unknown or value invalid |

## 11. Observability

| EventId | Level | Message |
|---|---|---|
| 1701 | Information | State recovered: {Entities} entities from snapshot, {Replayed} records replayed in {DurationMs} ms |
| 1710 | Debug | Snapshot partition {Partition}: {Count} entities at sequence {Sequence} |
| 1720 | Warning | Snapshot write failed for partition {Partition}; will retry |
| 1730 | Error | Aggregate drift detected for {Scope}: {Details}; corrected |
| 1740 | Warning | Listener {Listener} overflowed |

Metrics: `watchpoint.state.entities` (gauge by `kind`), `watchpoint.state.apply.duration`
(histogram ms), `watchpoint.state.stale` (gauge: number of stale properties),
`watchpoint.state.snapshot.lag` (gauge: sequences between applied and snapshotted),
`watchpoint.state.listener.overflows` (counter, tag `listener`).

Health check `ready`: recovery completed.

## 12. Security

Readers filter with RGT visible sets before returning data (API layer). Event payloads are not
kept in state (only `LastEvents` metadata), so personal data is not stored in snapshots.

## 13. Tests

Unit: `tests/service/Watchpoint.State.Tests` (fake registry, fake log, `TestClock`).
Integration: `Watchpoint.IntegrationTests/State`.

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| STA-U-001 | U | New device initialised unknown | REG creates door → state with all properties null value, health unknown, overall `Unknown` |
| STA-U-002 | U | Default values used | Property with `default` → initial value set, quality uncertain |
| STA-U-003 | U | Property record applied | `doorPosition.position=open` → state updated with times and sequence |
| STA-U-004 | U | Summary recomputed | forced → overall `Alarm`, cause `doorPosition.position` |
| STA-U-005 | U | Event record updates last events only | `doorPosition.forced` event → `LastEvents` entry; properties unchanged |
| STA-U-006 | U | Health record replaces health and sets Since | connectivity online → offline → `Since` = record time |
| STA-U-007 | U | Server offline marks devices server-unavailable | Server health offline → devices of server (in other partitions) get `ServerUnavailable=true`; readers see quality `Stale` |
| STA-U-008 | U | Child servers propagate | Offline parent server → devices of child servers unavailable |
| STA-U-009 | U | Server back online clears flag | |
| STA-U-010 | U | Touch updates LastSeen | |
| STA-U-011 | U | Staleness after StaleAfter | `staleAfter` PT10M, no touch for 11 min (TestClock + ticks) → quality `Stale`, StateChange emitted |
| STA-U-012 | U | Touch restores from stale | |
| STA-U-013 | U | Stale tracker rescheduled on refresh | Refresh at 9 min → not stale at 11 min, stale at 19 min |
| STA-U-014 | U | CausedBy from correlation | Record with `corr.command` → `PropertyState.CausedBy` set |
| STA-U-015 | U | Deleted device removed | REG delete → `Get` → `state.entity.not_found`; aggregates decremented |
| STA-U-016 | U | Type change drops/initialises properties | |
| STA-U-017 | U | Disabled flag → overall `Disabled` | |
| STA-U-018 | U | Maintenance flag → `Maintenance`, underlying kept | |
| STA-U-019 | U | Aggregates per location subtree | 3 doors in floor F of building B: one forced → B and F counts `Alarm=1`, `Normal=2` |
| STA-U-020 | U | Aggregates move on location change | Move device to another building → counts move |
| STA-U-021 | U | Aggregates per tag and server | |
| STA-U-022 | U | Aggregates consistency job finds no drift | Random operations ×10,000 → recompute equals incremental |
| STA-U-023 | U | Aggregates drift corrected | Corrupt a counter in test → job logs 1730 and fixes |
| STA-U-024 | U | Fan-out delivers changes in sequence order | |
| STA-U-025 | U | Slow listener overflows without blocking writer | Capacity 10, 100 changes → writer not blocked; `Overflowed=true` |
| STA-U-026 | U | Changes since sequence | Ring has 1…1000 → since 900 → 100 changes; since 0 with ring starting at 500 → null |
| STA-U-027 | U | GetMany omits unknown ids | |
| STA-U-028 | U | Query by state | 5 forced among 100 → `States=[alarm]` returns 5 |
| STA-U-029 | U | Query by property condition | `doorPosition.position == forced` |
| STA-U-030 | U | Query invalid condition | Unknown path → `state.query.invalid_condition` |
| STA-U-031 | U | Query performance | 50,000 entities scan with condition → < 20 ms (benchmark test, warning threshold) |
| STA-U-032 | U | Concurrent reads during writes are consistent | Readers on 4 threads while writer updates → every read state internally consistent (sequence matches property sequences) |
| STA-U-033 | U | JSON form | State → JSON matches §7.1 snapshot |
| STA-U-034 | U | Effective quality stale when server unavailable | Stored good, server offline → JSON quality `stale` |
| STA-U-035 | U | Membership change and state change in same batch | Both applied; counts correct |
| STA-U-036 | U | Snapshot collects only dirty entities | 100 entities, 3 changed → batch of 3 |
| STA-U-037 | U | Snapshot checkpoint = partition sequence at collection | |
| STA-U-038 | U | Snapshot split into batches with checkpoint in last | 12,000 dirty → 3 batches, checkpoint only in the 3rd |
| STA-U-039 | U | Failed snapshot retried with merged dirty set | |
| STA-U-040 | U | Snapshot payload excludes derived fields | |
| STA-U-041 | U | Shutdown writes final snapshot | |
| STA-U-042 | U | Recovery from snapshot + replay | Snapshot at 100, records 101…150 → final state equals uninterrupted run |
| STA-U-043 | U | Recovery discards entities removed from REG | |
| STA-U-044 | U | Recovery initialises entities missing from snapshot | |
| STA-U-045 | U | Recovery provides last values to PIP cache | |
| STA-I-001 | I | Snapshot persisted to PostgreSQL | After interval, `wp_state.items` has rows with JSON payloads; checkpoint rows per partition |
| STA-I-002 | I | Restart restores state | Ingest, stop node gracefully, start → same states (excluding derived timestamps) |
| STA-I-003 | I | Crash restores state | Kill node without shutdown snapshot → restart → state equals last acknowledged ingest (replay) |
| STA-I-004 | I | API state endpoints | GET state, batch, query and aggregates through the API test host |
| STA-I-005 | I | Recovery time target | 25,000 devices × 10 properties, 1 M records after snapshot → recovered < 60 s (nightly; CI scaled down ×10 with proportional target) |
| STA-I-006 | I | Server offline end-to-end | Simulator connection stopped → devices served with quality stale via API |

## 14. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.11 | §4–§7, §9–§12 | STA-U-001…STA-U-035 |
| T1.12 | §8 | STA-U-036…STA-U-045, STA-I-001…STA-I-006 |
