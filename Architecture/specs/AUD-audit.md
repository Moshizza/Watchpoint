# AUD — Audit Trail

| | |
|---|---|
| Spec id | AUD |
| Projects | `src/service/Watchpoint.Audit.Contracts`, `src/service/Watchpoint.Audit` |
| Milestone | M3 |
| Tasks | T3.06 |
| Design docs | [Users and rights §7](../../docs/modules/users-and-rights.md#7-audit-trail), [Data storage](../../docs/modules/data-storage.md), [Security](../../docs/operations/security.md) |

## 1. Purpose and scope

- An **append-only, hash-chained** record of every security-relevant action: sign-ins,
  configuration publishes, rights changes and rights-copy operations, commands, alarm handling,
  exports of personal data and video, maintenance mode, licence changes, package installs,
  secret changes.
- **Tamper detection**: verification of the chain (continuous and on demand) and signed
  checkpoints.
- Query API, export (CSV/PDF via RPT in M5), separate retention.

Not in scope: domain histories (commands, alarms, events live in HIS tables); audit is the
security view of *who did what*, not the operational timeline.

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, STO (`IAuditStore` contract and provider — PostgreSQL in M3, SQL Server in M11), SEC (checkpoint signing key), PIP (`WP_SYS` subject `wp.sys.audit` for cluster replication in M9) | Every module writes through `IAuditSink` (IDN, RGT, CFG, CMD, ALM, SEC, MNT, IRT, LIC, VID, HIS privacy); UIF audit viewer; RPT audit report |

### 2.1 Contracts project

`Watchpoint.Audit.Contracts` depends **only on Kernel** and holds `IAuditSink`, `AuditRecord`,
`AuditAction`, `AuditObjectRef`. It is created by the first task that writes audit records
(SEC or IDN in M1) together with `LoggingAuditSink` (writes records as structured log
events, EventId 2890). T3.06 adds `Watchpoint.Audit` with the real writer and replaces the
registration; no caller changes.

## 3. Layout

```
Watchpoint.Audit.Contracts/
├── IAuditSink.cs, AuditRecord.cs, AuditAction.cs, AuditObjectRef.cs, AuditOutcome.cs
└── LoggingAuditSink.cs
Watchpoint.Audit/
├── Contracts/IAuditQuery.cs, AuditQuery.cs, IAuditVerifier.cs, VerificationReport.cs
├── Internal/
│   ├── AuditWriter.cs              channel + single-writer batching
│   ├── HashChain.cs                canonical serialisation + SHA-256 chaining
│   ├── CheckpointService.cs        signed checkpoints (hourly)
│   ├── ContinuousVerifier.cs       background verification of new segments
│   ├── AuditQueryService.cs
│   ├── AuditRetention.cs
│   └── Storage/AuditDbContext.cs, PostgresAuditStore.cs
├── AuditModule.cs
└── AuditLog.cs                     event ids 2850–2899
```

## 4. Contracts

```csharp
public interface IAuditSink
{
    // Never throws for business reasons; never blocks callers more than the bounded channel wait (§5).
    ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default);
}

public sealed record AuditRecord(
    AuditAction Action,                    // "identity.signedIn", "config.published", "rights.copyApplied"…
    AuditOutcome Outcome,                  // Success | Failure | Denied
    AuditActor Actor,
    IReadOnlyList<AuditObjectRef> Objects, // what was affected (may be empty)
    string? Reason,                        // user-entered reason when required
    JsonElement? Details,                  // action-specific payload (diffs, arguments) ≤ 64 KB
    AuditSeverity Severity = AuditSeverity.Normal,  // Normal | Sensitive | Critical (break-glass, chain failure)
    Guid? CorrelationId = null,            // command id, config version, copy operation id, request id
    DateTimeOffset? OccurredAt = null);    // default: TimeProvider now at write

public readonly record struct AuditAction(string Value);   // "<module>.<verb>"; validated: ^[a-z]+(\.[a-zA-Z]+)+$

public sealed record AuditActor(ActorKind Kind /* User | Script | Automation | Integration | System | Federation */,
    Guid? Id, string DisplayName, ClientKind? Client, string? Workstation, string? IpAddress, string? Via /* "HQ via federation link" */);

public sealed record AuditObjectRef(string Type /* device, user, rights.grants, connection… */, Guid? Id, string? Label);
public enum AuditOutcome { Success, Failure, Denied }
public enum AuditSeverity { Normal, Sensitive, Critical }
```

`AuditActor.FromCurrentUser(ICurrentUser, HttpContext?)` helper in API; services without
request context use `AuditActor.System("scheduler")`.

### 4.1 Action catalogue v1

| Module | Actions |
|---|---|
| identity | `signedIn`, `signInFailed`, `signedOut`, `sessionRevoked`, `accountLocked`, `accountUnlocked`, `passwordChanged`, `passwordReset`, `mfaEnrolled`, `mfaReset`, `breakGlassUsed`, `tokenTheftDetected`, `userCreated`, `userUpdated`, `userDisabled`, `userDeleted` |
| config | `published`, `rolledBack`, `approvalRequested`, `approved`, `rejected`, `lockBroken`, `imported`, `exported` |
| rights | `grantsChanged`, `copyApplied`, `copyUndone`, `groupChanged`, `escalationDenied` |
| commands | `requested`, `rejected`, `approved`, `cancelled` (results stay in HIS) |
| alarms | `acknowledged`, `closed`, `closedWithoutReport`, `shelved`, `transferred`, `muted` |
| secrets | `created`, `replaced`, `deleted`, `readDenied`, `readAggregate` |
| maintenance | `started`, `extended`, `ended`, `autoEnded` |
| integrations | `packageInstalled`, `packageRemoved`, `connectionEnabled`, `connectionDisabled`, `hostPaired` (M4) |
| privacy | `personalDataViewed` (aggregate), `subjectExported`, `subjectErased`, `eventsExported` |
| video (M6), licence (M7), scripts (M8), cluster (M9) | added by those specs |
| audit | `chainVerificationFailed`, `checkpointCreated`, `retentionPurged`, `exported` |

Each action has a translation key `audit.action.<module>.<verb>` (en/fr/ar).

## 5. Writer

1. `WriteAsync` validates (action format, details size — oversize details are replaced with
   `{ "truncated": true, "sha256": … }`) and enqueues into a **bounded channel** (capacity
   10,000). When full, the caller waits up to 2 s; on timeout the record is written to the
   **fallback file** `<data>/audit/fallback-<date>.jsonl` (fsync per record) and EventId 2860
   is logged. Audit records are **never dropped**.
2. A single writer task drains up to 500 records or 50 ms, assigns `Sequence` (monotonic per
   node chain, `bigint`), computes hashes (§6), inserts in one transaction (`COPY` binary on
   PostgreSQL), then completes waiters.
3. On start, the writer ingests any fallback files first (in order), then deletes them.
4. Shutdown: drain within `Audit:ShutdownTimeout` (10 s), remainder to fallback file.
5. `Critical` records are flushed immediately (no 50 ms wait) and also logged at Warning.

## 6. Hash chain

- Canonical serialisation `C(r)`: UTF-8 JSON with fixed property order (sequence, chainId,
  occurredAt (ISO UTC, µs), recordedAt, action, outcome, severity, actor, objects, reason,
  details (RFC 8785 JCS canonical JSON), correlationId), no insignificant whitespace.
- `Hash(r) = SHA-256(PrevHash || C(r))`; the first record of a chain has
  `PrevHash = SHA-256("watchpoint-audit:" + chainId)`.
- `ChainId` = node id (each cluster node writes its own chain; M9 replicates chains between
  nodes for storage, never merges them).
- **Checkpoints**: every hour and at shutdown, `CheckpointService` writes
  `(chainId, sequence, hash, at, signature)` where signature = Ed25519 over
  `chainId|sequence|hash|at` with the site **audit signing key** (SEC key purpose `audit`).
  Checkpoints let verification detect truncation and wholesale rewrite by someone with
  database access but without the key.

### 6.1 Verification

```csharp
public interface IAuditVerifier
{
    Task<VerificationReport> VerifyAsync(string chainId, long fromSequence, long? toSequence, CancellationToken ct);
}
public sealed record VerificationReport(string ChainId, long From, long To, long RecordsChecked, bool Valid,
    IReadOnlyList<VerificationIssue> Issues, DateTimeOffset At);
public sealed record VerificationIssue(VerificationIssueKind Kind /* HashMismatch | SequenceGap | CheckpointMismatch | CheckpointSignatureInvalid | MissingAfterCheckpoint */,
    long Sequence, string Message);
```

- `ContinuousVerifier` verifies every new segment (from last verified sequence) every 5 min and
  the full chain weekly (streamed, 5,000 records per batch). Any issue → Critical audit
  `audit.chainVerificationFailed`, system event `audit.integrityFailed` (alarm-able), health
  check AUD degraded.
- Retention purges whole segments **up to a checkpoint** and records `audit.retentionPurged`
  with the checkpoint as the new genesis (`PrevHash` of the first remaining record is the
  checkpoint's hash — verification starts from the latest purge checkpoint).

## 7. Query

```csharp
public interface IAuditQuery
{
    Task<Page<AuditEntry>> QueryAsync(AuditQuery query, PageRequest page, CancellationToken ct);
    Task<AuditEntry?> GetAsync(long id, CancellationToken ct);
}
public sealed record AuditQuery(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<Guid>? Actors, IReadOnlyList<string>? ActionPrefixes,
    string? ObjectType, Guid? ObjectId, AuditOutcome? Outcome, AuditSeverity? MinSeverity, Guid? CorrelationId, string? Text /* reason, labels */,
    SortDirection Sort = SortDirection.Descending);
public sealed record AuditEntry(long Id, string ChainId, long Sequence, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    AuditAction Action, string ActionLabel /* localised */, AuditOutcome Outcome, AuditSeverity Severity, AuditActor Actor,
    IReadOnlyList<AuditObjectRef> Objects, string? Reason, JsonElement? Details, Guid? CorrelationId);
```

Range limit 366 days per query (`audit.query.range_too_large`). Keyset paging on
`(occurred_at, id)`.

## 8. Data (`wp_audit`)

| Table | Columns / notes |
|---|---|
| `records` | id bigserial, chain_id text, sequence bigint, occurred_at, recorded_at, action text, outcome smallint, severity smallint, actor jsonb, actor_id uuid null, objects jsonb, object_ids uuid[], reason text, details jsonb, correlation_id uuid, prev_hash bytea(32), hash bytea(32) — unique (chain_id, sequence); indexes (occurred_at), (actor_id, occurred_at), GIN object_ids, (action text_pattern_ops), correlation_id. TimescaleDB hypertable on occurred_at (30-day chunks, compression after 90 days) when available |
| `checkpoints` | chain_id, sequence, hash, at, signature bytea, key_id — PK (chain_id, sequence) |
| `verification_state` | chain_id PK, last_verified_sequence, last_full_verification_at, last_result jsonb |

- The database role used by the service for `wp_audit.records` has `INSERT, SELECT` only; a
  separate maintenance role (retention job) has `DELETE`. `UPDATE` is revoked; a trigger
  rejects updates (defence in depth). Migrations create both roles (STO §roles).
- Retention: `Audit:RetentionDays` (default 1,825 = 5 years, minimum 365), configured separately
  from history.

## 9. API

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/audit?from=&to=&actor=&action=&objectType=&objectId=&outcome=&minSeverity=&correlationId=&text=&limit=&cursor=` | `admin.audit.view` | Query |
| `GET /api/v1/audit/{id}` | `admin.audit.view` | One entry with details |
| `GET /api/v1/audit/export?…&format=csv` | `admin.audit.view` | Streamed CSV (PDF via RPT, M5); itself audited (`audit.exported`) |
| `GET /api/v1/audit/verification` | `admin.audit.view` | Latest reports per chain |
| `POST /api/v1/audit/verification` | `admin.audit.view` | `{ chainId?, from?, to? }` → job (JobRef) |

Live: `system` topic carries `auditIntegrity { ok, lastCheckedAt }` for health banners.

## 10. Settings

| Key | Default | Notes |
|---|---|---|
| `Audit:RetentionDays` | 1825 | ≥ 365 |
| `Audit:ChannelCapacity` | 10000 | |
| `Audit:BatchSize` / `BatchDelay` | 500 / 50 ms | |
| `Audit:CheckpointInterval` | 1 h | |
| `Audit:VerifyInterval` / `FullVerifyInterval` | 5 min / 7 days | |
| `Audit:ShutdownTimeout` | 10 s | |

## 11. Errors

`audit.query.range_too_large`, `audit.entry.not_found`, `audit.action.invalid` (programming
error — thrown as `ArgumentException` in Debug, logged and normalised to `unknown.invalid` in
Release), `audit.verification.chain_not_found`.

## 12. Observability

| EventId | Level | Message |
|---|---|---|
| 2850 | Information | Audit writer started; ingested {Count} fallback records |
| 2851 | Debug | Audit batch {Count} written in {ElapsedMs} ms |
| 2860 | Warning | Audit channel full; record written to fallback file |
| 2870 | Information | Checkpoint {ChainId}:{Sequence} created |
| 2871 | Critical | Audit chain {ChainId} verification failed at {Sequence}: {Kind} |
| 2880 | Information | Audit retention purged {Count} records up to {Sequence} |
| 2890 | Information | (LoggingAuditSink) {Action} by {Actor} {Outcome} |

Metrics: `watchpoint.audit.records` (counter, tags action module, outcome),
`watchpoint.audit.queue.depth` (gauge), `watchpoint.audit.fallback` (counter),
`watchpoint.audit.verify.issues` (counter).

## 13. Security

- Details must not contain secrets: `AuditRecord` details pass through the redaction filter
  (same name rules as the HST log redaction policy, HST §redaction: properties named `password`, `secret`, `token`, `pin`, `apiKey`, and values of type
  `$secret` replaced by `"•••"`) — test AUD-U-012.
- Personal data (for example cardholder names in exported events) is never written into audit
  rows: `objects` and `details` carry ids only, and labels are resolved at query time. A privacy
  erase therefore never has to modify audit rows, and the chain stays valid (test AUD-U-016).
- Query results are not filtered by device scope (auditors see all); access is controlled by
  `admin.audit.view` only.

## 14. Tests

| Id | Layer | Test |
|---|---|---|
| AUD-U-001 | U | Action format validation and translation key existence for catalogue actions |
| AUD-U-002 | U | Canonical serialisation stable (golden vectors in `tests/vectors/audit/*.json`) |
| AUD-U-003 | U | Hash chaining: genesis, consecutive records |
| AUD-U-004 | U | Verification detects modified field, deleted record (gap), inserted record, reordered records |
| AUD-U-005 | U | Checkpoint signature valid; tampered checkpoint detected; truncation after checkpoint detected |
| AUD-U-006 | U | Writer batching by size and delay; sequences contiguous |
| AUD-U-007 | U | Critical records flushed immediately |
| AUD-U-008 | U | Channel full → fallback file, no record lost; fallback ingested in order on start |
| AUD-U-009 | U | Shutdown drain and fallback of remainder |
| AUD-U-010 | U | Oversized details truncated with hash |
| AUD-U-011 | U | Actor helpers (user with client/workstation/IP, system, federation via) |
| AUD-U-012 | U | Secret redaction in details |
| AUD-U-013 | U | Query filters (actor, action prefix, object, outcome, severity, correlation, text) |
| AUD-U-014 | U | Range limit and keyset paging |
| AUD-U-015 | U | Retention purge up to checkpoint; verification continues from purge checkpoint |
| AUD-U-016 | U | Personal data stored as ids only; labels resolved at query time; erase leaves chain valid |
| AUD-U-017 | U | Continuous verifier raises Critical audit, system event and health degradation on failure |
| AUD-U-018 | U | Export streams CSV with localised action labels and is itself audited |
| AUD-U-019 | U | LoggingAuditSink → real sink swap needs no caller changes (DI test) |
| AUD-U-020 | U | Write throughput ≥ 5,000 records/s sustained (benchmark, Testcontainers PostgreSQL) |
| AUD-I-001 | I | Sign-in, publish, rights copy, command, alarm ack → expected audit entries with correlation ids |
| AUD-I-002 | I | UPDATE on `records` rejected by trigger and role; DELETE only by maintenance role |
| AUD-I-003 | I | Manual SQL tamper detected by on-demand verification |
| AUD-I-004 | I | Service crash mid-batch: no gaps after restart (fallback/transaction) |
| AUD-I-005 | I | Audit API permissions and export |

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| M1 prerequisite (first of the SEC/IDN tasks) | §2.1, §4 contracts + LoggingAuditSink | AUD-U-019 (sink part) |
| T3.06 | all | AUD-U-001…AUD-U-020, AUD-I-001…AUD-I-005 |
