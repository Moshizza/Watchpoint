# STO-MS — SQL Server Storage Provider and Store Migration

| | |
|---|---|
| Spec id | STO-MS |
| Projects | `src/service/Watchpoint.Storage.SqlServer`, per-module `*.SqlServer` migration assemblies (e.g. `Watchpoint.Registry.Storage.SqlServer`), `tests/service/Watchpoint.Storage.Certification` (provider-agnostic certification suite), `tools/wpctl` (`storage migrate`) |
| Milestone | M11 |
| Tasks | T11.01 (§4–§8), T11.02 (§9–§10) |
| Event id range | 1250–1299 (Storage range; STO uses 1200–1249) |
| Design docs | [Data storage](../../docs/modules/data-storage.md), [STO](STO-storage.md) |

## 1. Purpose and scope

- A second storage provider, **Microsoft SQL Server** (2019, 2022, 2025; Express, Standard,
  Enterprise; Azure SQL Database and Managed Instance), implementing the four store kinds
  (Configuration, State, History, Audit) behind the STO contracts.
- The **provider certification suite** run against every provider (PostgreSQL and SQL Server in
  v1).
- **Store mapping** (each store to a different database/provider) and `wpctl storage migrate` to
  move stores between providers, including background history migration.

Licensing note: SQL Server itself is supplied by the customer; Microsoft.Data.SqlClient and the
EF Core SQL Server provider are MIT.

## 2. Dependencies

STO (contracts, base contexts, migration runner), SEC (credentials), every module's storage
(migrations per provider), HIS/AUD (store implementations), Microsoft.Data.SqlClient (MIT),
Microsoft.EntityFrameworkCore.SqlServer (MIT), Testcontainers `mcr.microsoft.com/mssql/server`
images (Developer edition for tests only).

## 3. Layout

```
Watchpoint.Storage.SqlServer/
├── SqlServerStorageProvider.cs (IStorageProvider), SqlServerNaming.cs, SqlServerMigrationLock.cs (sp_getapplock)
├── State/SqlServerStateStore.cs (MERGE/TVP batches)
├── History/SqlServerHistoryStore.cs (SqlBulkCopy, partitioned tables, columnstore when available)
├── Audit/SqlServerAuditStore.cs
├── Capabilities/EditionProbe.cs (Express limits, version, compatibility level, Azure detection)
└── Resilience/SqlServerRetryPolicy.cs (transient error numbers, AG failover)
Watchpoint.Storage.Migration/ (store migration engine used by wpctl and the UI wizard)
```

## 4. Provider specifics (T11.01)

| Concern | SQL Server implementation |
|---|---|
| Schemas | `wp_<module>` schemas (SQL Server supports schemas) |
| JSON | `nvarchar(max)` with `ISJSON` check constraints; JSON queries via `JSON_VALUE` computed columns where indexed (registry attributes used for filtering are already modelled as columns); SQL Server 2025 native `json` type used when compatibility level ≥ 170 (probed) |
| UUIDs | `uniqueidentifier`; UUIDv7 values stored as-is. Clustered indexes on UUIDv7 keys suffer SQL Server's GUID byte-order sorting → clustered key on `(created_at, id)` or a sequential `bigint` surrogate clustered key with the UUID as non-clustered primary key for high-insert tables (history, audit, state items) |
| Timestamps | `datetime2(6)` UTC (`timestamptz` equivalent handled by the provider: values always UTC, `DateTimeOffset` converted) |
| Text | `nvarchar` everywhere; collation per column for case-insensitive comparisons: `Latin1_General_100_CI_AS_SC_UTF8` for names, binary collation for identifiers/keys |
| Arrays (`uuid[]`, `text[]`) | Child tables or JSON arrays (per module mapping; e.g. `location_ids` → `history_event_locations` junction table) — modules express arrays through `IStorageNaming`/value converters, never provider-specific SQL |
| Bulk insert | `SqlBulkCopy` with `TableLock` off, batches of 5,000, `KeepIdentity` off |
| Upserts (state) | Table-valued parameter + `MERGE` with `HOLDLOCK` (or `UPDATE`/`INSERT` pattern under `SERIALIZABLE`, chosen by benchmark) |
| History time series | Monthly **partitioned tables** (partition function on `time`, sliding window maintained by the retention job: `SWITCH` out old partitions then drop — fast retention); **clustered columnstore** on closed partitions (Standard/Enterprise; Express supports columnstore since 2016 SP1 with limits) |
| Aggregates | No continuous aggregates → HIS fallback incremental aggregate jobs (already specified, HIS §10) |
| Audit immutability | `INSTEAD OF UPDATE` trigger rejecting updates, `DENY UPDATE` to the service login, separate maintenance login for retention deletes; ledger tables (SQL Server 2022+ append-only ledger) used when available (`LEDGER = ON (APPEND_ONLY = ON)`) in addition to the hash chain |
| Migration lock | `sp_getapplock @Resource='watchpoint-migrations', @LockMode='Exclusive', @LockOwner='Session'` |
| HA | Availability group listener with `MultiSubnetFailover=True`; retry policy covering error numbers 4060, 40197, 40501, 40613, 49918, 49919, 49920, 1205 (deadlock), 233, -2 |
| Authentication | SQL logins, Windows integrated (Windows nodes; Kerberos on Linux with keytab documented), Azure AD (`Active Directory Default` / managed identity) |
| TLS | `Encrypt=Strict` (TDS 8) where supported, else `Encrypt=Mandatory` with `TrustServerCertificate=False` and optional certificate thumbprint pinning (`HostNameInCertificate`) |

### 4.1 Express edition limits

`EditionProbe` reads `SERVERPROPERTY('EngineEdition'/'Edition')`; Express → `MaxDatabaseSizeBytes =
10 GB`. HLT shows usage vs limit; warnings at 70/85/95 %; the setup wizard recommends mapping
**History** to the bundled PostgreSQL when Express is chosen (data-storage doc mapping example).

## 5. Migrations per provider (T11.01)

- Each module with storage has `Migrations/Postgres/` and `Migrations/SqlServer/` folders in
  separate assemblies (EF Core requires provider-specific migrations); `tools/add-migration.ps1`
  generates both from the same model change; CI check fails when one provider's migrations are
  missing for a model snapshot change (`ef migrations has-pending-model-changes` for each).
- Raw SQL in migrations allowed only inside provider folders (partitioning, columnstore,
  triggers).
- Upgrade path: every migration from the oldest supported release must apply on SQL Server
  (certification CERT-001).

## 6. Store implementations (T11.01)

| Store | Notes |
|---|---|
| Configuration | EF Core contexts per module (derived `*SqlServerDbContext`), optimistic concurrency with `rowversion` columns mapped to the `RowVersion` convention used on PostgreSQL (`xmin`) — base context abstracts it |
| State | `SqlServerStateStore` implements `IStateStore` (STO §7.2) with TVPs; checkpoints in the same transaction |
| History | `SqlServerHistoryStore` implements `IHistoryStore` (HIS) — writer batches via `SqlBulkCopy` into a staging heap then `INSERT…SELECT` for idempotency on retries (dedup by `(partition, sequence)` unique index), queries with keyset paging, `OPTION (RECOMPILE)` for highly variable filters, statement timeout via `CommandTimeout` |
| Audit | `SqlServerAuditStore` implements `IAuditStore` (AUD) with the immutability measures of §4 |

## 7. Certification suite (T11.01)

`Watchpoint.Storage.Certification` — xUnit test classes parameterised by provider fixture
(`PostgresFixture`, `SqlServerFixture`; future providers add theirs):

| Id | Test |
|---|---|
| STO-MS-I-001 | Fresh install creates all schemas/tables via the migration runner |
| STO-MS-I-002 | Upgrade path: database created by release N-2 migrations then upgraded to current |
| STO-MS-I-003…010 | Configuration store: CRUD for each module context, optimistic concurrency conflicts, CFG publish transaction across modules, rollback |
| STO-MS-I-011…014 | State store: batches, checkpoints atomicity, recovery read, concurrency of partitions |
| STO-MS-I-015…020 | History store: writer throughput (≥ 20,000 rows/s on reference hardware), idempotent retries, every HIS query with expected results (shared fixtures), retention via partition switch, aggregates fallback jobs |
| STO-MS-I-021…023 | Audit store: append, hash chain verification, update rejected, ledger when available |
| STO-MS-I-024 | Unicode (Arabic, emoji), case-insensitive name searches, binary identifiers |
| STO-MS-I-025 | Time zones/UTC round trip with µs precision |
| STO-MS-I-026 | Concurrency: parallel publishes, alarm transitions, deadlock retry |
| STO-MS-I-027 | Failover: AG listener / container restart → retry policy resumes |
| STO-MS-I-028 | Express size limit detection and health warnings |
| STO-MS-I-029 | Backup/restore with the provider's tooling (documented commands executed in CI) |
| STO-MS-I-030 | Performance targets of HIS queries (P-007 subset) on SQL Server |

The same suite runs for PostgreSQL (with and without TimescaleDB) — it replaces nothing in STO
tests but certifies equivalence.

## 8. Settings (T11.01)

`Database:Databases:<key>` gains SQL Server fields: `Provider: "sqlserver"`, `Instance`,
`Authentication: sql | windows | azureAd`, `MultiSubnetFailover`, `Encrypt: strict | mandatory`,
`HostNameInCertificate`. Setup wizard (DEP §7) and *System → Storage* show them.

## 9. Store mapping (T11.02)

- `Storage:Stores:<Configuration|State|History|Audit>` = database key (default `default`);
  Configuration and State must share one database (CFG transactions span them for some modules —
  validation `storage.mapping.config_state_split`); History and Audit may each live elsewhere
  (e.g. History on bundled PostgreSQL + TimescaleDB, others on corporate SQL Server).
- Cross-store consistency: no distributed transactions; modules already write history/audit
  asynchronously (consumers/writers), so mapping changes do not change semantics.

## 10. `wpctl storage migrate` (T11.02)

```
wpctl storage migrate --store history --to <databaseKey> [--background] [--verify-only] [--resume]
```

1. Preconditions: target database reachable, provider migrations applied to the target (runner
   executes them), enough space estimate (source size × provider factor), cluster in
   maintenance-friendly state (Configuration/State moves require **maintenance mode** with all
   nodes stopped except the migrating one; History/Audit moves can run online).
2. Copy in a **neutral format**: per table, keyset-ordered batches (10,000 rows) read via the
   source store's export reader and written via the target store's import writer (typed rows,
   not SQL dialect); progress persisted (`storage_migrations` table in the target) for `--resume`.
3. **Online history mode** (`--background`): the store mapping switches **first** for new writes
   (HIS writer targets the new database), queries read from both (union by time range: new DB for
   times ≥ switch time, old DB before) until the copy finishes, then old reads stop.
4. Verification: row counts per table and per day, checksums (SHA-256 over canonical row
   serialisation, sampled 1 % + full for audit chain), audit chain re-verified on the target.
5. Switch mapping (CFG system document `storage.mapping` + node settings), audit
   `storage.migrated`, keep the source untouched until the admin removes it (`wpctl storage
   cleanup --store history --from <key>`).

Tests:

| Id | Test |
|---|---|
| STO-MS-U-001…005 | Mapping validation rules; settings parsing; config/state split refused |
| STO-MS-U-006…010 | Neutral row format per store: typed serialisation round trips (all column types) |
| STO-MS-U-011…014 | Copy engine batching, keyset order, resume from persisted progress, cancellation |
| STO-MS-U-015…017 | Online history mode: write switch first, dual-read union by time, end of dual-read |
| STO-MS-U-018…020 | Verification: counts, checksums, audit chain re-verify; mismatch blocks switch |
| STO-MS-I-031…034 | PostgreSQL → SQL Server migration of each store on a seeded site |
| STO-MS-I-035…037 | SQL Server → PostgreSQL (history to bundled PostgreSQL + TimescaleDB) online while ingesting |
| STO-MS-I-038 | Interrupted migration resumed |
| STO-MS-I-039 | Configuration/State migration requires maintenance mode and stops other nodes |
| STO-MS-I-040 | Cleanup of the source after confirmation |

## 11. Errors, observability

Errors: `storage.sqlserver.unsupported_version` (< 2019 / compatibility level < 150),
`storage.sqlserver.express_limit`, `storage.mapping.invalid`, `storage.mapping.config_state_split`,
`storage.migration.precondition_failed` (args reason), `storage.migration.verification_failed`,
`storage.migration.in_progress`.

| EventId | Level | Message |
|---|---|---|
| 1250 | Information | SQL Server {Version} ({Edition}) detected for database {Key} |
| 1251 | Warning | SQL Server Express database {Key} at {Percent} % of 10 GB |
| 1260 | Information | Store migration {Store} {From} → {To}: {Rows} rows copied ({Percent} %) |
| 1261 | Information | Store migration {Store} verified and switched |
| 1262 | Error | Store migration {Store} verification failed: {Details} |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T11.01 | §4–§8 | STO-MS-I-001…STO-MS-I-030 |
| T11.02 | §9–§10 | STO-MS-U-001…STO-MS-U-020, STO-MS-I-031…STO-MS-I-040 |
