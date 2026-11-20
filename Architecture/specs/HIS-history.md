# HIS — History

| | |
|---|---|
| Spec id | HIS |
| Projects | `src/service/Watchpoint.History` (writer, queries, policies), history store implementation in `Watchpoint.Storage.Postgres` |
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
├── Contracts/IHistoryQueries.cs, HistoryQueries.cs (query records), SeriesPoint.cs, HistorySettings.cs
├── Internal/
│   ├── Writers/EventsWriter.cs, AlarmsWriter.cs, CommandsWriter.cs, PropertiesWriter.cs   (one durable consumer each)
│   ├── Policies/PropertyHistoryPolicy.cs, PeriodicSampler.cs
│   ├── Retention/RetentionManager.cs, CompressionManager.cs, AggregatesManager.cs (part 2)
│   ├── Archive/ArchiveExporter.cs (part 2)
│   ├── Privacy/PrivacyPurger.cs, SubjectRequests.cs (part 2)
│   └── Lag/LagMonitor.cs
Watchpoint.Storage.Postgres/History/
├── PostgresHistoryStore.cs           IHistoryStore implementation (COPY, upserts, queries)
├── Sql/history_schema.sql            hypertables or partitioned tables (capability-dependent)
└── Sql/continuous_aggregates.sql     (part 2)
```

## 4. Tables (PostgreSQL provider, schema `wp_history`)

All timestamps `timestamptz` (ms). With TimescaleDB each table is a **hypertable** on `time`
(chunk interval in brackets); without it, **declarative range partitions** per month created
by `RetentionManager` 3 months ahead.

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
CREATE INDEX ON wp_history.events (entity_id, time DESC);
CREATE INDEX ON wp_history.events (kind, time DESC);
CREATE INDEX ON wp_history.events USING gin (location_ids);
CREATE INDEX ON wp_history.events USING gin (person_refs);

-- alarms [7 days]  one row per alarm, updated through its life
CREATE TABLE wp_history.alarms (
  time timestamptz NOT NULL,   -- raised_at
  id uuid NOT NULL, rule_id uuid NOT NULL, rule_key text NOT NULL, rule_version bigint NOT NULL,
  source_id uuid NULL, source_kind smallint NULL, source_name text NULL, location_id uuid NULL, location_ids uuid[] NULL,
  type_id text NULL, tag_keys text[] NULL,
  state smallint NOT NULL, priority_initial int NOT NULL, priority_final int NOT NULL, count int NOT NULL,
  last_occurrence_at timestamptz NOT NULL, acknowledged_at timestamptz NULL, acknowledged_by uuid NULL,
  resolved_at timestamptz NULL, closed_at timestamptz NULL, closed_by uuid NULL, report text NULL,
  owner jsonb NULL, escalation_level int NOT NULL, procedure_id uuid NULL, procedure_version bigint NULL,
  false_alarm boolean NULL,   -- from procedure answer "confirmed? no" (configurable mapping)
  UNIQUE (id, time)
);
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
  id uuid NOT NULL, group_id uuid NULL, device_id uuid NOT NULL, device_name text NULL, location_ids uuid[] NULL,
  path text NOT NULL, arguments jsonb NOT NULL, issuer jsonb NOT NULL, issuer_user uuid NULL, reason text NULL,
  risk smallint NOT NULL, priority smallint NOT NULL, state smallint NOT NULL, failure jsonb NULL,
  queued_at timestamptz NULL, dispatched_at timestamptz NULL, completed_at timestamptz NULL, verified_at timestamptz NULL,
  attempt int NOT NULL, approval jsonb NULL, correlation jsonb NULL,
  UNIQUE (id, time)
);
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

Value mapping: Bool → `v_bool`; Int/Decimal → `v_num`; Enum/String → `v_text`;
Timestamp → `v_text` (ISO); Duration → `v_num` (seconds); Geo/List/Object → `v_json`.

## 5. Writers (T2.13)

Four durable consumers (PIP `ILogConsumerFactory`), each with its own name and kind filter:

| Consumer | Stream / kinds | Table | Write method |
|---|---|---|---|
| `his-events` | WP_LOG: `event`, `sysevent`, `operator.note`; WP_SYS: `sysevent`, `operator.note` | events | COPY (binary) into a temp table, `INSERT … SELECT … ON CONFLICT (id, time) DO NOTHING` |
| `his-alarms` | WP_LOG: `alarm.*` | alarms (upsert) + alarm_timeline (insert) | Per batch: fold transitions per alarm id into a final row; `INSERT … ON CONFLICT (id, time) DO UPDATE` with monotonic merge (timestamps only set once, counts max, state = latest by record sequence) |
| `his-commands` | WP_LOG: `command.*`; WP_SYS: `commandGroup.*` (group rows are not stored; groups derivable from `group_id`) | commands (upsert) | Same fold/upsert approach |
| `his-properties` | WP_LOG: `property` | property_changes | History policy (§6) then COPY + ON CONFLICT DO NOTHING |

Batch: up to 5,000 records or 1 s (consumer `MaxWait`); one transaction per batch; ack after
commit. On database errors: retry with back-off (1 s → 60 s) without acking (the log retains the
records); `HistoryLagWarning` system event when lag > `History:LagWarning` (default 5 min).

Personal data split: payload fields listed in `PersonalDataFields` go to `personal`; their
`holderRef`/`credentialRef` values (if any) also to `person_refs`.

Writers are idempotent (re-delivery after a crash produces no duplicates and no regressions).

## 6. Property history policies

Per property definition (MOD `HistoryPolicy`), overridable per device type/device in configuration
(CFG, M3; `wp_history.policy_overrides`):

| Mode | Writer behaviour |
|---|---|
| `everyChange` | Every `property` record stored |
| `deadband` | Stored if `|v - lastStored| ≥ absolute` (or `≥ percent × |lastStored|`), or quality changed, or `MaxInterval` (default 1 h) elapsed since last stored |
| `periodic` | Not stored from records; `PeriodicSampler` stores current values from STA every `interval` (aligned to interval boundaries) |
| `none` | Not stored |

`lastStored` cache per (device, path) is kept in memory and rebuilt lazily from the latest row
(`SELECT DISTINCT ON`) the first time a key is seen after start.

## 7. Queries (T2.13)

```csharp
public interface IHistoryQueries
{
    Task<Page<JournalEvent>> QueryEventsAsync(EventQuery query, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<AlarmTimelineEntry>> GetAlarmTimelineAsync(AlarmId id, CancellationToken ct);
    Task<Page<AlarmHistoryRow>> QueryAlarmsAsync(AlarmHistoryQuery query, PageRequest page, CancellationToken ct);
    Task<Page<CommandHistoryRow>> QueryCommandsAsync(CommandHistoryQuery query, PageRequest page, CancellationToken ct);
    Task<IReadOnlyList<SeriesPoint>> QuerySeriesAsync(SeriesQuery query, CancellationToken ct);
    Task<IReadOnlyList<BucketCount>> CountAsync(CountQuery query, CancellationToken ct);       // reports, dashboards
}
public sealed record SeriesQuery(DeviceId Device, PropertyPath Path, DateTimeOffset From, DateTimeOffset To,
    TimeSpan? Bucket /* null = raw (≤ 10,000 points), else min/avg/max per bucket */);
public sealed record SeriesPoint(DateTimeOffset Time, WpValue? Value, decimal? Min, decimal? Avg, decimal? Max, int Count);
public sealed record CountQuery(string Table /* events | alarms | commands */, DateTimeOffset From, DateTimeOffset To,
    TimeSpan Bucket, IReadOnlyList<string> GroupBy /* kind, severity, location_id, rule_key, state… (whitelist) */,
    JsonElement Filters /* whitelisted filters */);
```

- Keyset paging on `(time, id)`; scope filters applied as `location_ids && @visibleLocations`
  or `entity_id = ANY(@visibleDevices)` (RGT provides whichever is smaller).
- Statement timeout 30 s for history queries (`SET LOCAL statement_timeout`); results limited
  (series raw 10,000 points: above, the API asks for a bucket).
- API endpoints (permissions `operate.history.view` + scope): `GET /api/v1/history/alarms`,
  `/history/commands`, `/history/series?device=&path=&from=&to=&bucket=`, `/history/counts` (used
  by RPT and dashboards); events go through EVT (`/events`).

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

## 13. Settings, errors, observability

Settings: `History:BatchSize` (5,000), `History:LagWarning` (`PT5M`), `History:QueryTimeout`
(`PT30S`), `History:Archive:*`, retention settings (§8).

Errors: `history.query.too_many_points`, `history.query.timeout`, `history.query.range_too_large`
(> 366 days for counts, > 31 days for raw events), `history.archive.failed`,
`history.retention.invalid`.

| EventId | Level | Message |
|---|---|---|
| 3201 | Information | History consumer {Consumer} started at sequence {Sequence} |
| 3202 | Warning | History lag {LagSeconds} s for {Consumer} |
| 3203 | Error | History write failed for {Consumer}: {Error}; retrying |
| 3210 | Information | Retention dropped {Chunks} chunks of {Table} older than {Limit} |
| 3220 | Information | Archived {Rows} rows of {Table} for {Day} |
| 3230 | Information | Privacy purge pseudonymised {Rows} rows |

Metrics: `watchpoint.history.written` (counter by table), `watchpoint.history.lag` (gauge seconds by
consumer), `watchpoint.history.batch.duration` (histogram), `watchpoint.history.size` (gauge bytes
by table).

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
| HIS-U-014 | U | Batch boundaries (5,000 or 1 s) |
| HIS-U-015 | U | No ack on failure; retry back-off |
| HIS-U-016 | U | Lag warning system event |
| HIS-U-017 | U | Query scope filter selection (locations vs devices) |
| HIS-U-018 | U | Raw series limit → error suggesting bucket |
| HIS-U-019 | U | Count query whitelist enforcement (unknown group-by rejected) |
| HIS-U-020 | U | Keyset paging stability |
| HIS-I-001 | I | Schema created as hypertables when TimescaleDB present |
| HIS-I-002 | I | Schema created as partitioned tables without TimescaleDB (plain postgres image) |
| HIS-I-003 | I | Events written end-to-end from simulator; idempotent after consumer restart |
| HIS-I-004 | I | Alarms and timeline end-to-end |
| HIS-I-005 | I | Commands end-to-end |
| HIS-I-006 | I | Property changes with deadband for analog |
| HIS-I-007 | I | Throughput: 5,000 records/s sustained into history on CI runner (indicative) |
| HIS-I-008 | I | Query events with filters and scope (location subtree via GIN) under 200 ms on 1 M rows |
| HIS-I-009 | I | Series raw and bucketed |
| HIS-I-010 | I | Database outage → lag grows, no loss, catch-up after recovery |
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
