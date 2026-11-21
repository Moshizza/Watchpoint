# HIS — History

| | |
|---|---|
| Spec id | HIS |
| Projects | `src/service/Watchpoint.History` (writer, queries, policies), history store contract (`IHistoryStore`, row and filter records) in `Watchpoint.Storage`, its PostgreSQL implementation in `Watchpoint.Storage.Postgres` |
| Milestone | M2 (part 1: writer and queries), M5 (part 2: retention, compression, aggregates, archive, privacy) |
| Tasks | T2.13 (§4–§7), T5.01 (§8–§10), T5.02 (§11–§12) |
| Design docs | [History and reports](../../docs/modules/history-and-reports.md), [Data storage](../../docs/modules/data-storage.md) |

## 1. Purpose and scope

Long-term storage of what happened: events, alarms and their timelines, commands, property
changes; queries for journals, timelines, charts and reports; retention, compression,
aggregates, archive and privacy purge.

Not in scope: audit trail tables (AUD defines them, HIS hosts the writer pattern), report
rendering (RPT).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, STO (`IHistoryStore` contract, provider), PIP (durable consumers), STA (periodic sampling), MOD (history policies), REG (enrichment already in records) | EVT, ALM (timeline), CMD (older commands), RPT, UIO charts, AUD |

## 3. Layout

Log event ids: 3200–3249 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.History/
├── Contracts/IHistoryQueries.cs (+ query and result records, SeriesPoint), HistoryRecordBodies.cs (kinds and bodies of
│             sysevent, operator.note, alarm.*, command.*), HistoryScope.cs, HistoryErrors.cs, HistorySettings.cs,
│             IPeriodicPropertySource.cs
├── Internal/
│   ├── Writers/EventRowMapper.cs, AlarmFolder.cs, CommandFolder.cs, PropertyWriter.cs, HistoryWriters.cs (the five
│   │           consumers), ConsumerRunner.cs (batch loop), HistoryValues.cs (value → column mapping)
│   ├── Policies/PropertyHistoryPolicy.cs, PolicyResolver.cs, LastStoredCache.cs, PeriodicSampler.cs
│   ├── Queries/HistoryQueries.cs, ScopeSelector.cs, HistoryCursor.cs
│   ├── Retention/RetentionManager.cs, CompressionManager.cs, AggregatesManager.cs (part 2)
│   ├── Archive/ArchiveExporter.cs (part 2)
│   ├── Privacy/PrivacyPurger.cs, SubjectRequests.cs (part 2)
│   └── Lag/LagMonitor.cs
├── HistoryService.cs                 hosted service: schema, the consumers, the periodic sampler
└── HistoryModule.cs                  AddWatchpointHistory
Watchpoint.Storage/Contracts/IHistoryStore.cs, HistoryRows.cs, HistoryFilters.cs     store contract (provider-neutral rows)
Watchpoint.Storage.Postgres/History/
├── PostgresHistoryStore.cs, PostgresHistoryStore.Queries.cs   IHistoryStore implementation (COPY, upserts, queries)
├── Sql/history-schema.sql            tables as hypertables or as partitioned tables (probed at start)
└── Sql/continuous_aggregates.sql     (part 2)
```

## 4. Tables (PostgreSQL provider, schema `wp_history`)

All timestamps `timestamptz` (ms). With TimescaleDB each table is a **hypertable** on `time`
(chunk interval in brackets); without it, **declarative range partitions** per month: the month before the current one, the
current one and three months ahead, plus a `<table>_default` partition that takes rows outside them (so a write never fails for
want of a partition). `IHistoryStore.EnsureSchemaAsync` creates all of this idempotently under an advisory lock (several
nodes may start together); the history service repeats it every `SchemaMaintenanceInterval` (6 h) to create the partitions of the
coming months until `RetentionManager` (T5.01) takes that over. The provider's `Capabilities` are not used for the choice: the
store asks the database whether the `timescaledb` extension is installed.

```sql
-- events [1 day]
CREATE TABLE wp_history.events (
  time          timestamptz NOT NULL,          -- received time
  id            uuid        NOT NULL,
  seq           bigint      NOT NULL,          -- WP_LOG sequence or 0 for WP_SYS
  kind          text        NOT NULL,
  category      smallint    NOT NULL,          -- EventCategory
  severity      smallint    NOT NULL,          -- Severity
  source_time   timestamptz NULL,
  entity_id     uuid        NULL,
  entity_kind   smallint    NULL,
  server_id     uuid        NULL,
  location_id   uuid        NULL,
  location_ids  uuid[]      NULL,              -- ancestors incl. self, root first (subtree filters)
  type_id       text        NULL,
  tag_keys      text[]      NULL,
  origin_type   smallint    NOT NULL,
  origin_id     uuid        NULL,
  payload       jsonb       NOT NULL,          -- non-personal fields
  personal      jsonb       NULL,              -- personal fields only (privacy purge target)
  person_refs   text[]      NULL,              -- holderRef/credentialRef values for subject search (hashed after purge)
  title_key     text        NOT NULL,
  title_args    jsonb       NOT NULL,
  source_name   text        NULL,              -- display name at the time
  location_names text[]     NULL,
  correlation   jsonb       NULL,
  UNIQUE (id, time)
);
CREATE INDEX ON wp_history.events (time DESC, id DESC);   -- the journal's default order, and the keyset seek
CREATE INDEX ON wp_history.events (entity_id, time DESC);
CREATE INDEX ON wp_history.events (kind, time DESC);
CREATE INDEX ON wp_history.events USING gin (location_ids);
CREATE INDEX ON wp_history.events USING gin (person_refs);

-- alarms [7 days]  one row per alarm, updated through its life
CREATE TABLE wp_history.alarms (
  time timestamptz NOT NULL,   -- raised_at
  id uuid NOT NULL, seq bigint NOT NULL DEFAULT 0 /* newest record merged: state and priority follow it */, rule_id uuid NOT NULL, rule_key text NOT NULL, rule_version bigint NOT NULL,
  source_id uuid NULL, source_kind smallint NULL, source_name text NULL, location_id uuid NULL, location_ids uuid[] NULL,
  type_id text NULL, tag_keys text[] NULL,
  state smallint NOT NULL, priority_initial int NOT NULL, priority_final int NOT NULL, count int NOT NULL,
  last_occurrence_at timestamptz NOT NULL, acknowledged_at timestamptz NULL, acknowledged_by uuid NULL,
  resolved_at timestamptz NULL, closed_at timestamptz NULL, closed_by uuid NULL, report text NULL,
  owner jsonb NULL, escalation_level int NOT NULL, procedure_id uuid NULL, procedure_version bigint NULL,
  false_alarm boolean NULL,   -- from procedure answer "confirmed? no" (configurable mapping)
  UNIQUE (id, time)
);
CREATE INDEX ON wp_history.alarms (time DESC, id DESC);
CREATE INDEX ON wp_history.alarms (rule_id, time DESC);
CREATE INDEX ON wp_history.alarms (source_id, time DESC);
CREATE INDEX ON wp_history.alarms USING gin (location_ids);

-- alarm_timeline [7 days]
CREATE TABLE wp_history.alarm_timeline (
  time timestamptz NOT NULL, alarm_id uuid NOT NULL, record_id uuid NOT NULL, kind text NOT NULL,
  actor_user uuid NULL, actor_session uuid NULL, client_kind smallint NULL, body jsonb NOT NULL,
  UNIQUE (record_id, time)
);
CREATE INDEX ON wp_history.alarm_timeline (alarm_id, time);

-- commands [7 days]  one row per command, updated through its life
CREATE TABLE wp_history.commands (
  time timestamptz NOT NULL,   -- requested_at
  id uuid NOT NULL, seq bigint NOT NULL DEFAULT 0, group_id uuid NULL, device_id uuid NOT NULL, device_name text NULL, location_ids uuid[] NULL,
  path text NOT NULL, arguments jsonb NOT NULL, issuer jsonb NOT NULL, issuer_user uuid NULL, reason text NULL,
  risk smallint NOT NULL, priority smallint NOT NULL, state smallint NOT NULL, failure jsonb NULL,
  queued_at timestamptz NULL, dispatched_at timestamptz NULL, completed_at timestamptz NULL, verified_at timestamptz NULL,
  attempt int NOT NULL, approval jsonb NULL, correlation jsonb NULL,
  UNIQUE (id, time)
);
CREATE INDEX ON wp_history.commands (time DESC, id DESC);
CREATE INDEX ON wp_history.commands (id);                 -- request time of a command (non-request records)
CREATE INDEX ON wp_history.commands (device_id, time DESC);
CREATE INDEX ON wp_history.commands (issuer_user, time DESC);

-- property_changes [1 day]
CREATE TABLE wp_history.property_changes (
  time timestamptz NOT NULL, device_id uuid NOT NULL, path text NOT NULL,
  v_num numeric NULL, v_text text NULL, v_bool boolean NULL, v_json jsonb NULL,
  quality smallint NOT NULL, source_time timestamptz NULL, record_id uuid NOT NULL,
  UNIQUE (record_id, time)
);
CREATE INDEX ON wp_history.property_changes (device_id, path, time DESC);
```

```sql
-- policy_overrides: per device or type (written by configuration, CFG M3; read by the property writer, §6)
CREATE TABLE wp_history.policy_overrides (
  scope text NOT NULL,      -- 'device' | 'type'
  key text NOT NULL,        -- device id | type id (without version)
  path text NOT NULL,
  policy jsonb NOT NULL,    -- { "mode": "deadband", "type": "absolute", "threshold": 2, "interval": "PT1M" }
  PRIMARY KEY (scope, key, path)
);
```

Value mapping: Bool → `v_bool`; Int/Decimal → `v_num`; Enum/String → `v_text`;
Timestamp → `v_text` (ISO 8601 UTC, ms); Duration → `v_num` (seconds); Geo/List/Object → `v_json` (the WpValue JSON form); Null → no
column set. A series reads `v_num` back as a decimal and `v_text` as a string (history keeps the column, not the property type).

## 5. Writers (T2.13)

Five durable consumers (PIP `ILogConsumerFactory`), each with its own name and kind filter:

| Consumer | Stream / kinds | Table | Write method |
|---|---|---|---|
| `his-events` | WP_LOG: `event`, `sysevent`, `operator.note` | events | binary COPY into a temp table, `INSERT … SELECT … ON CONFLICT (id, time) DO NOTHING` |
| `his-events-sys` | WP_SYS: `sysevent`, `operator.note` (`seq` stored as 0) | events | same |
| `his-alarms` | WP_LOG: `alarm.*` | alarms (upsert) + alarm_timeline (insert) | Per batch: fold records per alarm id into one row; `INSERT … ON CONFLICT (id, time) DO UPDATE` with monotonic merge (below) |
| `his-commands` | WP_LOG: `command.*` | commands (upsert) | Same fold/upsert approach |
| `his-properties` | WP_LOG: `property` | property_changes | History policy (§6) then COPY + ON CONFLICT DO NOTHING |

(`his-events` and `his-events-sys` are two consumers because an events table is fed from two streams. `commandGroup.*` records
of WP_SYS are not consumed: group rows are not stored, groups are derivable from `group_id`.)

**Batch.** Up to `History:BatchSize` (5,000, at most 10,000) records, or what arrived within the consumer's `MaxWait` (1 s); one
transaction per batch; the batch is acknowledged only after the commit. The runner reads **one batch at a time**: it asks the
consumer for the next batch only after the previous one was acknowledged, so a slow or stuck database holds the log back
(JetStream keeps what was not delivered) and no buffer grows. On any failure of the write the *same batch* is repeated with
back-off 1 s → 2 s → … → 60 s (`History:RetryDelay*`), for as long as it takes, never acknowledged and never dropped (the
consumer's `MaxDeliver` does not apply: the retry is inside the loop). If only the acknowledgement fails, it is repeated without
writing again. A record whose body cannot be read, or that lacks what the table needs, is skipped with a warning (3204) and
counted as skipped, so one bad record cannot stop the stream; records of kinds or schema versions this node does not know are
left out of the batch (3205). After a restart reading resumes after the last acknowledged batch.

**Lag.** Before each attempt the age of the oldest record of the batch is the consumer's lag (gauge `watchpoint.history.lag`).
Above `History:LagWarning` (5 min) a warning is logged (3202) and a `history.lagWarning` system event (`sysevent` with
`consumer`, `lagSeconds`, severity warning) is appended to WP_SYS, at most once per warning period per consumer; it is raised
from the retry loop too, so a database outage is reported while it lasts.

**Idempotent.** Re-delivery after a crash (stored, not acknowledged) produces no duplicates and no regressions: events,
timeline and property rows are unique by `(id, time)`, `(record_id, time)`; alarm and command rows merge with the rule below,
which is the same in the C# fold (a batch) and the SQL upsert (against the stored row):

| Field | Merge of record with sequence `s` into a row with newest sequence `r` |
|---|---|
| `state`, `priority_final`, `report`, `owner`, `procedure_*`, `false_alarm`, `failure`, `approval` | follow the newer sequence (`s > r`); an absent value never erases a stored one |
| `acknowledged_at/by`, `resolved_at`, `closed_at/by`, `queued_at`, `dispatched_at`, `completed_at`, `verified_at` | set once (the first value that arrives stays) |
| `count`, `escalation_level`, `attempt`, `last_occurrence_at`, `seq` | the greater |
| request fields of a command (`path`, `arguments`, `issuer`, `issuer_user`, `reason`, `risk`) | the request record's value, whenever it arrives (a row made from another record has `path = ''`, `risk = -1`, `priority = -1` until then) |

**Record bodies.** `command.*` are written and registered by CMD (T2.01): history does **not** register them and does not reference
the commands module; it reads the JSON body (`CommandRecordReader`): `command.requested` is the whole `Command` (`id`, `path`,
`arguments`, `issuer` with `userId` or `identityId`, `reason`, `effectiveRisk`, `priority`, `requestedAt`, `groupId`), the others carry
`id` and what changed (`priority`, `attempt`, `failure`, `error`, `by`, `reason`). Their request time, the row key, is not in them: it comes
from the request in the same batch, else from the stored row (`IHistoryStore.GetCommandTimesAsync`); a record whose request history
never saw is skipped with a warning (3204). The consumer therefore accepts `command.*` kinds this node has not registered
(`RequiresKnownKind = false`). `approved` keeps the command awaiting until `queued`; `approvalRejected` ends it as rejected.
`alarm.*`, `sysevent` and `operator.note` are written by ALM and EVT, which do not exist yet; their bodies are fixed here as
`Watchpoint.History.Contracts` records (`AlarmTransitionBody`, `SysEventBody`, `OperatorNoteBody`, kinds in `HistoryRecordKinds`)
and registered by the history service when it is built (`ILogRecordTypeRegistry.Register` accepts the same type again; the owning
module must reuse these types or change them here). Rules those modules must satisfy: every `alarm.*` record carries the **whole alarm
after the transition** (so that history can fold it) and `raisedAt` (the row key); `sysevent` carries optional `severity` (catalogue) and
`personalFields`.

**Mapping.** Source columns (`source_name`, `location_ids` = ancestors incl. self root first, `location_names`, `tag_keys`,
`type_id`) are the enrichment at the time of writing (REG `GetEnrichment` and ancestors; an unknown entity leaves them null).
`category` is Health for `event` paths starting with `health.`, Device for other `event`s, System for `sysevent`, Operator for
`operator.note`. Title: device event `capabilities.<cap>.events.<evt>.title` with the non-personal payload fields and `device`
(display name); system events `events.<kind>.title`; notes `events.operator.note`. **Personal data split:** payload fields
listed in the event definition's `PersonalDataFields` (system events: `SysEventBody.PersonalFields`) go to `personal`, not to
`payload` and **not to `title_args`**; the string values of `holderRef`/`credentialRef` fields (personal or not) also go to
`person_refs`. Alarm timeline `body` = `{ state, priority, count, detail }`; command `approval` = `{ decision, by, reason }`.

## 6. Property history policies

Per property definition (MOD `HistoryPolicy`), overridable per device type/device in configuration
(CFG, M3; `wp_history.policy_overrides`, reloaded every minute); precedence device override > type override > definition:

| Mode | Writer behaviour |
|---|---|
| `everyChange` | Every `property` record stored (the type catalogue's `all` is the same mode; a property without a definition is treated so: nothing is lost for want of one) |
| `deadband` | Stored if there is no last stored value, or `|v - lastStored| ≥ threshold` (`type: absolute`) or `≥ threshold / 100 × |lastStored|` (`type: percent`, threshold in percent), and the value changed, or quality changed, or `MaxInterval` (1 h) elapsed since last stored; non-numeric values are stored when they change |
| `periodic` | Not stored from records; `PeriodicSampler` stores current values from STA every `interval`, aligned to multiples of the interval from UTC midnight; the sample's time is the boundary and its record id is derived from (device, path, boundary), so a boundary sampled twice is one row. STA is not built yet: the sampler runs when an `IPeriodicPropertySource` is registered (seam) |
| `none` | Not stored |

`lastStored` cache per (device, path) is kept in memory and rebuilt lazily from the latest row (one `LATERAL … ORDER BY time DESC
LIMIT 1` query per batch for the keys not yet seen) the first time a key is seen after start (cache size limit 500,000 keys; when
full it is emptied and rebuilt the same way). A batch is evaluated against a local overlay of the cache and the cache is updated
only after the commit, so a repeated attempt decides exactly as the first one.

## 7. Queries (T2.13)

```csharp
public interface IHistoryQueries     // every method returns Result<…>: failures are values (HistoryErrors), not exceptions
{
    Task<Result<Page<JournalEvent>>> QueryEventsAsync(EventQuery query, PageRequest page, CancellationToken ct);
    Task<Result<IReadOnlyList<AlarmTimelineEntry>>> GetAlarmTimelineAsync(AlarmId id, CancellationToken ct);
    Task<Result<Page<AlarmHistoryRow>>> QueryAlarmsAsync(AlarmHistoryQuery query, PageRequest page, CancellationToken ct);
    Task<Result<Page<CommandHistoryRow>>> QueryCommandsAsync(CommandHistoryQuery query, PageRequest page, CancellationToken ct);
    Task<Result<IReadOnlyList<SeriesPoint>>> QuerySeriesAsync(SeriesQuery query, CancellationToken ct);
    Task<Result<IReadOnlyList<BucketCount>>> CountAsync(CountQuery query, CancellationToken ct);       // reports, dashboards
}
public sealed record SeriesQuery(DeviceId Device, PropertyPath Path, DateTimeOffset From, DateTimeOffset To,
    TimeSpan? Bucket /* null = raw (≤ 10,000 points), else min/avg/max per bucket */);
public sealed record SeriesPoint(DateTimeOffset Time, WpValue? Value, decimal? Min, decimal? Avg, decimal? Max, int Count);
public sealed record CountQuery(string Table /* events | alarms | commands */, DateTimeOffset From, DateTimeOffset To,
    TimeSpan Bucket, IReadOnlyList<string> GroupBy /* whitelist below */, JsonElement Filters /* whitelisted columns */,
    HistoryScope? Scope = null);
public sealed record HistoryScope(IReadOnlyList<LocationId>? VisibleLocations, IReadOnlyList<DeviceId>? VisibleDevices);
```

`EventQuery`, `JournalEvent` (with `JournalEventSource` in place of EVT's `EventSource`, and `IVisibleScope` replaced by
`HistoryScope`) and the alarm and command query and row records are declared in `Watchpoint.History.Contracts` because EVT, ALM and
CMD do not exist yet; EVT (T2.14) consumes them. `JournalEvent` carries personal fields **unmasked** (masking is the API's job,
EVT §7.1). History stores events, health events, system events and operator notes: `IncludeAlarmActivity` and
`IncludeCommandActivity` are accepted and reserved for the journal.

- **Keyset paging** on `(time, id)` in the requested direction, cursor `CursorCodec` version 1 (`[time]`, id): the query asks for
  `limit + 1` rows, the extra one only tells that another page follows. Rows arriving between two pages never shift them. A foreign
  or tampered cursor is `common.cursor.invalid`; a limit outside 1–1,000 is `common.value.out_of_range`.
- **Scope**: the caller's scope is turned into one restriction: `location_ids && @visibleLocations` or `entity_id = ANY(@visibleDevices)`
  (`source_id` for alarms, `device_id` for commands) — whichever list is smaller when both are given, locations on a tie. An empty
  chosen list means nothing is visible: no query is run and the result is empty (never "no restriction"). A tag filter that
  matches no tag is empty too.
- **Ranges**: events and alarms/commands ranges are `From < To`, else `history.query.invalid`; events at most 31 days, counts at
  most 366 days (`history.query.range_too_large`).
- **Counts**: `Table` and every `GroupBy` and `Filters` column must be in the whitelist (checked in History and again in the store;
  values are passed as parameters, column names only from the whitelist), else `history.query.invalid` with `field` and `reason`.
  Whitelist — events: `kind`, `category`, `severity`, `entity_id`, `server_id`, `location_id`, `type_id`, `origin_type`; alarms:
  `rule_key`, `state`, `source_id`, `location_id`, `type_id`, `priority_final`, `false_alarm`; commands: `state`, `risk`,
  `priority`, `device_id`, `path`, `issuer_user`. Filter values are a value or an array of values; numbers and UUIDs are validated.
  Buckets are `date_bin` from 2000-01-01 UTC and at least one second.
- **Series**: raw points are limited to 10,000, buckets to 10,000: above that `history.query.too_many_points` (`max`), "ask for a
  bucket". Bucketed numeric values: min, average, max, count.
- Statement timeout `History:QueryTimeout` (30 s) per query (`set_config('statement_timeout', …, true)` in a transaction), a timeout
  is `history.query.timeout`.
- API endpoints (permissions `operate.history.view` + scope): `GET /api/v1/history/alarms`,
  `/history/commands`, `/history/series?device=&path=&from=&to=&bucket=`, `/history/counts` (used
  by RPT and dashboards); events go through EVT (`/events`). (API task: not part of T2.13.)

## 8. Retention (T5.01)

Settings (`wp_history.settings`, editable in configuration, CFG):

| Category | Default | Min–max |
|---|---|---|
| events | 365 days | 7–3,650 |
| alarms + timeline | 1,095 days | 30–3,650 |
| commands | 365 days | 7–3,650 |
| property_changes (raw) | 90 days | 1–3,650 |
| aggregates | 1,095 days | 30–3,650 |
| personal fields (§12) | 90 days | 1–3,650 (≤ category retention) |

TimescaleDB: `add_retention_policy(table, INTERVAL)`, updated when settings change. Without it:
`RetentionManager` drops monthly partitions entirely older than retention (daily job at 03:30
site time) and creates future partitions.

## 9. Compression (T5.01)

TimescaleDB: `ALTER TABLE … SET (timescaledb.compress, timescaledb.compress_segmentby =
'<entity col>', timescaledb.compress_orderby = 'time DESC')` with `add_compression_policy` after
7 days (alarms/commands after 30 days, as rows can still be updated). Without TimescaleDB: no
compression (warning shown in *System → Storage*).

## 10. Aggregates (T5.01)

| Aggregate | Content | Refresh |
|---|---|---|
| `events_hourly` | bucket (1 h), location_id, kind, severity → count | every 15 min, lag 1 h |
| `alarms_daily` | bucket (1 day), location_id, rule_key, priority band → raised count, acknowledged count, avg/max time to ack, avg time to close, false alarms | every 1 h |
| `property_hourly` | bucket (1 h), device_id, path → min, avg, max, last, count (numeric only) | every 15 min |

TimescaleDB: continuous aggregates with refresh policies. Without: `AggregatesManager` jobs
compute the same into plain tables incrementally (watermark per aggregate).
`QuerySeriesAsync` with buckets ≥ 1 h uses `property_hourly`; `CountAsync` uses aggregates when
the bucket and group-by allow.

## 11. Archive (T5.02)

- Optional (`History:Archive:Enabled`), target: a folder path (local or mounted share) or an
  S3-compatible bucket (Minio .NET client, Apache-2) with credentials in SEC.
- Before retention drops data older than the limit, `ArchiveExporter` writes one Parquet file per
  table per day (`events/2026/11/03.parquet`, Parquet.Net, MIT) with the same columns (personal
  fields excluded unless `IncludePersonal` is set), verifies row counts, then allows the drop.
- Archive failures **block** the drop for that day (data kept) and raise a system event.

## 12. Privacy (T5.02)

- After the personal-data retention: `PrivacyPurger` (daily) sets `personal = NULL` and replaces
  `person_refs` values with `HMAC-SHA256(siteKey, value)` truncated to 16 hex chars
  (pseudonymised: still groupable, not reversible; `siteKey` in SEC).
- **Subject requests** (permission `admin.privacy`): `POST /api/v1/privacy/search { personRef }` →
  counts and sample of events; `POST /api/v1/privacy/export { personRef }` → job exporting all
  matching events (JSON); `POST /api/v1/privacy/erase { personRef, reason }` → pseudonymise
  immediately for that subject. All audited.
- **Event log window.** The purge and an erase act on the history tables. The same payloads also stay in
  the event log (`WP_LOG`, `WP_SYS`: delete and purge denied, PIP §5.1) until it expires, and a recovery
  replay re-reads them. `Log:Retention` is therefore capped at 30 days (PIP §10): a purge or an erase is
  **complete only once the log window after it has passed**, and the export of a subject request says so.
  Crypto-shredding (payload fields encrypted with a per-subject key that the erase destroys, SEC) is the
  M5 decision record that would make it immediate (2026-11 review, D11).

## 13. Settings, errors, observability

Settings: `History:BatchSize` (5,000; 1–10,000), `History:LagWarning` (`PT5M`; PT1S–P1D), `History:QueryTimeout`
(`PT30S`; PT1S–PT10M), `History:Archive:*`, retention settings (§8). Fixed by the module (tests shorten them): consumer `MaxWait` 1 s,
retry back-off 1 s → 60 s, policy override reload 1 min, last-stored cache 500,000 keys, schema upkeep every 6 h.

Errors: `history.query.too_many_points`, `history.query.timeout`, `history.query.range_too_large`
(> 366 days for counts, > 31 days for raw events), `history.query.invalid` (args `field`, `reason`: `not_whitelisted`,
`value`, `range`, `too_small`, `pattern`, `not_an_object`), `history.archive.failed`, `history.retention.invalid`.

| EventId | Level | Message |
|---|---|---|
| 3201 | Information | History consumer {Consumer} started at sequence {Sequence} |
| 3202 | Warning | History lag {LagSeconds} s for {Consumer} |
| 3203 | Error | History write failed for {Consumer}: {Error}; retrying |
| 3204 | Warning | History consumer {Consumer} skipped record {RecordId} of kind {Kind}: {Reason} |
| 3205 | Debug | History consumer {Consumer} skipped record {RecordId}: kind {Kind} (schema {Schema}) is not known to this node |
| 3206 | Warning | History consumer {Consumer} stopped unexpectedly ({Error}); restarting |
| 3207 | Warning | History schema upkeep failed ({Error}); retrying |
| 3210 | Information | Retention dropped {Chunks} chunks of {Table} older than {Limit} |
| 3220 | Information | Archived {Rows} rows of {Table} for {Day} |
| 3230 | Information | Privacy purge pseudonymised {Rows} rows |

Metrics: `watchpoint.history.written` (counter by table), `watchpoint.history.lag` (gauge seconds by
consumer), `watchpoint.history.batch.duration` (histogram by consumer), `watchpoint.history.size` (gauge bytes
by table; T5.01). The error text of a database failure is never logged (it can carry row values): only its type.

## 14. Tests

| Id | Layer | Test |
|---|---|---|
| HIS-U-001 | U | Event record → row mapping incl. personal split and person refs |
| HIS-U-002 | U | WP_SYS events mapped with seq 0 |
| HIS-U-003 | U | Alarm transitions folded into one row with monotonic merge |
| HIS-U-004 | U | Out-of-order redelivery does not regress alarm state |
| HIS-U-005 | U | Timeline entries per transition with actor |
| HIS-U-006 | U | Command lifecycle folded |
| HIS-U-007 | U | Value mapping for every WpValue kind |
| HIS-U-008 | U | everyChange policy |
| HIS-U-009 | U | Deadband absolute and percent, quality change, MaxInterval |
| HIS-U-010 | U | Periodic sampler alignment |
| HIS-U-011 | U | None policy |
| HIS-U-012 | U | Policy override precedence (device > type > definition) |
| HIS-U-013 | U | lastStored cache rebuilt lazily |
| HIS-U-014 | U | Batch boundaries (5,000 or 1 s), consumer names/filters, settings validation, unreadable records skipped |
| HIS-U-015 | U | No ack on failure; retry back-off 1 s → 60 s; no further batch read while failing or slow (back-pressure); ack failure repeated without rewriting |
| HIS-U-016 | U | Lag warning system event (once per period, also during an outage) |
| HIS-U-017 | U | Query scope filter selection (locations vs devices) |
| HIS-U-018 | U | Raw series limit → error suggesting bucket |
| HIS-U-019 | U | Count query whitelist enforcement (unknown group-by rejected) |
| HIS-U-020 | U | Keyset paging stability |
| HIS-I-001 | I | Schema created as hypertables when TimescaleDB present |
| HIS-I-002 | I | Schema created as partitioned tables without TimescaleDB (plain postgres image) |
| HIS-I-003 | I | Events written end-to-end (records appended through the log writer, as the ingest does; the simulator-driven variant is an E2E scenario); resume after the last acknowledged record on restart; idempotent after a crash between commit and acknowledgement |
| HIS-I-004 | I | Alarms and timeline end-to-end |
| HIS-I-005 | I | Commands end-to-end |
| HIS-I-006 | I | Property changes with deadband for analog |
| HIS-I-007 | I | Throughput: 5,000 records/s sustained into history on CI runner (indicative) |
| HIS-I-008 | I | Query events with filters and scope (location subtree via GIN) under 200 ms on 1 M rows |
| HIS-I-009 | I | Series raw and bucketed |
| HIS-I-010 | I | Database outage (connections refused and dropped) → lag grows and is reported, no loss, catch-up after recovery |
| HIS-U-021…030 | U | Retention settings validation and policy computation; partition creation/drop planning without Timescale |
| HIS-U-031…035 | U | Compression policy parameters per table |
| HIS-U-036…040 | U | Aggregates: fallback incremental jobs with watermarks; query routing to aggregates |
| HIS-I-011 | I | Timescale retention policy applied and drops old chunks (time-shifted test data) |
| HIS-I-012 | I | Partition drop without Timescale |
| HIS-I-013 | I | Compression applied; queries still correct on compressed chunks |
| HIS-I-014 | I | Alarm update on compressed chunk works (Timescale DML on compressed data) |
| HIS-I-015 | I | Continuous aggregates refresh and match raw computations |
| HIS-I-016 | I | Fallback aggregates match raw computations |
| HIS-I-017 | I | Counts API on aggregates for 1 year range |
| HIS-I-018 | I | Retention change updates policies |
| HIS-I-019 | I | Size metrics reported |
| HIS-I-020 | I | Statement timeout enforced |
| HIS-U-041…048 | U | Archive: file layout, row count verification, failure blocks drop, personal fields excluded/included |
| HIS-U-049…055 | U | Privacy: purge after retention, HMAC pseudonymisation stable per site, subject search/export/erase, audit hooks |

(Ranges `…` group individually numbered cases in code.)

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| T2.13 | §4–§7 | HIS-U-001…HIS-U-020, HIS-I-001…HIS-I-010 |
| T5.01 | §8–§10 | HIS-U-021…HIS-U-040, HIS-I-011…HIS-I-020 |
| T5.02 | §11–§12 | HIS-U-041…HIS-U-055 |
