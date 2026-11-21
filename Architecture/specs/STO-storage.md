# STO — Storage (Store Interfaces and PostgreSQL Provider)

| | |
|---|---|
| Spec id | STO |
| Projects | `src/service/Watchpoint.Storage` (contracts, manager), `src/service/Watchpoint.Storage.Postgres` (provider), `tests/service/Watchpoint.Storage.Certification` (provider-agnostic suite) |
| Milestone | M1 |
| Tasks | T1.03 (§4–§6), T1.04 (§7–§8) |
| Design docs | [Data storage](../../docs/modules/data-storage.md) |

## 1. Purpose and scope

All database access goes through this module. It provides:

- the **store model**: four stores (configuration, state, history, audit), each mapped to a
  database through settings;
- the **provider contract** implemented once per database technology, and the PostgreSQL
  provider (default);
- connections (pooling, credentials, TLS), **migrations** (ordered, locked, versioned),
  health checks;
- the **configuration store** foundation used by every module's EF Core context, and the
  **state store** (snapshots) used by STA, ALM and CMD.

The history and audit store **contracts** are declared here; their behaviour and tables are
specified in [HIS](HIS-history.md) and [AUD](AUD-audit.md).

Not in scope: SQL Server provider and store migration (STO-MS, M11).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST (`IStartupTask`), SEC (bootstrap secrets, from T1.05), EF Core 10 (Relational), Npgsql + Npgsql.EntityFrameworkCore.PostgreSQL | Every module with persistent data |

## 3. Project layout

Log event ids: 1200–1249 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Storage/
├── Contracts/
│   ├── StoreKind.cs                 Configuration, State, History, Audit
│   ├── IStorageProvider.cs
│   ├── ProviderCapabilities.cs
│   ├── IStoreConnectionFactory.cs
│   ├── IModuleStorageRegistration.cs
│   ├── IStateStore.cs
│   ├── IHistoryStore.cs, HistoryRows.cs, HistoryFilters.cs   (history store contract; see HIS)
│   ├── IAuditStore.cs               (declared; see AUD)
│   ├── StorageErrors.cs             error codes + StorageStartupException (exit codes)
│   ├── ConfigurationDbContextBase.cs, EntityConventions.cs (IVersioned, ITimestamped, StorageContextInfo)
│   ├── IStorageStartup.cs           wait for databases + migrations (called by HST start-up tasks)
│   ├── ModuleStorageServiceCollectionExtensions.cs   AddModuleStorage
│   └── StorageServiceCollectionExtensions.cs         AddWatchpointStorage
├── Internal/
│   ├── StorageSettings.cs, StoreMap.cs   settings → provider + connection per store (validated)
│   ├── StorageStartup.cs, MigrationRunner.cs, EfModuleMigrator.cs, ModuleContextActivator.cs
│   ├── StorageHealthCheck.cs
│   └── StorageMetrics.cs            meter + database size poller
└── StorageLog.cs

Watchpoint.Storage.Postgres/
├── PostgresProvider.cs
├── PostgresDataSources.cs           NpgsqlDataSource per database definition
├── PostgresStateStore.cs            (T1.04)
├── PostgresMigrationLock.cs         advisory lock
├── Sql/                             embedded SQL for state tables (T1.04)
└── PostgresStorageServiceCollectionExtensions.cs   AddPostgresStorage

Each module (example Registry):
Watchpoint.Registry/Storage/
├── RegistryDbContext.cs             provider-neutral model (inherits ConfigurationDbContextBase)
├── Postgres/RegistryPostgresDbContext.cs   empty derived class (own migration set)
└── Postgres/Migrations/…            EF Core migrations generated for PostgreSQL
```

Modules reference `Microsoft.EntityFrameworkCore.Relational` only. Provider packages
(`Npgsql.EntityFrameworkCore.PostgreSQL`, later `Microsoft.EntityFrameworkCore.SqlServer`)
are referenced only by `Watchpoint.Storage.*` and by the design-time tool project
`tools/Watchpoint.MigrationsDesign` (used by developers to generate migrations). ARCH-5
(TST §6) is adjusted accordingly: modules may reference EF Core *Relational*, not providers.

Everything modules and providers use is public in the `Watchpoint.Storage.Contracts` namespace
(ARCH-3: modules use other modules' types only from `.Contracts`; ARCH-4: nothing public in
`.Internal`), including `ConfigurationDbContextBase`. The host (T1.01) wires `IStorageStartup` into
its start-up phases (`Infrastructure`: `WaitForDatabasesAsync`, `Schema`: `MigrateAsync`) and maps
`StorageStartupException.ExitCode` to the process exit code.

## 4. Store model

```csharp
public enum StoreKind { Configuration, State, History, Audit }

public sealed record StoreBinding(StoreKind Kind, string ProviderId, string DatabaseKey);
// ProviderId: "postgres" (M1), "sqlserver" (M11)…
// DatabaseKey: key of a database definition in settings (Database:Databases:<key>)
```

Default mapping: all four stores → database key `default` → provider `postgres`.

### 4.1 Naming in the database

| Store | PostgreSQL schema | Tables |
|---|---|---|
| Configuration | one schema per module: `wp_<module>` (lowercase, e.g. `wp_registry`, `wp_identity`) | owned by the module's EF context |
| State | `wp_state` | `items`, `checkpoints` (§7.3) |
| History | `wp_history` | see HIS |
| Audit | `wp_audit` | see AUD |

Providers without schemas (MySQL, later) use table prefixes `<schema>__<table>`;
`IStorageNaming` hides this from modules.

Column naming: snake_case (EF Core naming convention configured by the base context);
timestamps `timestamptz` (UTC, millisecond precision); ids `uuid`; JSON documents `jsonb`.

## 5. Provider contract

```csharp
public interface IStorageProvider
{
    string ProviderId { get; }                         // "postgres"
    ProviderCapabilities Capabilities { get; }         // static values
    ProviderCapabilities CapabilitiesFor(DatabaseInfo info);   // with probed values (TimeSeriesExtension…)
    Task<Result<DatabaseInfo>> ProbeAsync(DatabaseDefinition db, CancellationToken ct); // version, edition, extensions, size
    void ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseDefinition db, string migrationsHistorySchema);
    ValueTask<DbConnection> OpenConnectionAsync(DatabaseDefinition db, CancellationToken ct);   // pooled
    IStorageNaming Naming { get; }
    IMigrationLock CreateMigrationLock(DatabaseDefinition db);  // AcquireAsync(timeout, ct) → held lock or null on timeout
    Task BootstrapAsync(DatabaseDefinition db, IReadOnlyCollection<string> schemas,
        IReadOnlyCollection<StoreKind> stores, CancellationToken ct);  // §6.3 step 2; state tables when stores has State
    PoolStatistics? GetPoolStatistics(DatabaseDefinition db);  // null when the driver publishes its own pool metrics
    string Sanitize(string connectionString);           // password removed, for logs
    IStateStore CreateStateStore(DatabaseDefinition db);
    IHistoryStore CreateHistoryStore(DatabaseDefinition db);   // T2.13 (HIS §4–§7)
    // The audit store factory is added by the AUD task (T3.06).
}

public sealed record ProviderCapabilities(
    bool Schemas,                 // PostgreSQL true
    bool JsonDocuments,           // jsonb with indexing
    bool TimeSeriesExtension,     // TimescaleDB present (probed, not static)
    bool NativeCompression,
    bool ContinuousAggregates,
    bool Partitioning,
    BulkInsertMethod BulkInsert,  // BinaryCopy, BulkCopy, MultiRowInsert
    long? MaxDatabaseSizeBytes);  // null = unlimited (SQL Server Express: set)

public sealed record DatabaseDefinition(string Key, string ProviderId, string Host, int Port,
    string Database, string Username, string? PasswordRef, TlsMode Tls, string? ExtraOptions,
    IReadOnlyList<string> AdditionalHosts,            // multi-host failover (PostgreSQL)
    int MaxPoolSize)
{ public string? PasswordDev { get; init; } }        // ToString() prints provider://user@host:port/db, never a password

public interface IDatabasePasswordProvider            // default: PasswordDev; SEC (T1.05) registers the real one
{ ValueTask<string?> GetPasswordAsync(DatabaseDefinition db, CancellationToken ct); }

public enum TlsMode { Disable, Prefer, Require, VerifyCa, VerifyFull }

public sealed record DatabaseInfo(string ServerVersion, string? Edition,
    IReadOnlyList<string> Extensions, long SizeBytes);
```

`PasswordRef` is a SEC reference (`SecretRef` once SEC exists), resolved through
`IDatabasePasswordProvider` when a connection opens. Before T1.05, the password may be given in
clear in settings for Development only (`Database:Databases:default:PasswordDev`), refused
outside Development.

### 5.1 PostgreSQL provider specifics

- One `NpgsqlDataSource` per database definition (built with `NpgsqlDataSourceBuilder`),
  `Pooling=true`, `Maximum Pool Size` from settings (default 50), `Timeout=15`,
  `Command Timeout=30`, `Keepalive=30`, `Target Session Attributes=primary` when
  `AdditionalHosts` is non-empty.
- The probe lists installed extensions; **optional** `timescaledb` sets the `TimeSeriesExtension`
  capability (and compression/continuous aggregates). Missing `timescaledb` is a warning
  (`STO1203`), not an error. `pgcrypto` is not required: `gen_random_uuid()` is built into
  PostgreSQL since 13.
- Connections open asynchronously only (the password provider is asynchronous).
- Pool statistics are not exposed by Npgsql's API: pool usage comes from Npgsql's own meter
  (`Npgsql`, `db.client.connections.usage`), exported with the node's metrics.
- Minimum server version: 16. Lower → `storage.provider.unsupported_version`.
- EF Core: `UseNpgsql(dataSource, o => o.MigrationsHistoryTable("__ef_migrations", schema))`,
  `UseSnakeCaseNamingConvention()` (EFCore.NamingConventions, Apache-2), no lazy loading,
  `QueryTrackingBehavior.NoTracking` by default.

## 6. Connections, migrations, health

### 6.1 `IStoreConnectionFactory`

```csharp
public interface IStoreConnectionFactory
{
    DatabaseDefinition GetDatabase(StoreKind store);
    IStorageProvider GetProvider(StoreKind store);
    ValueTask<DbConnection> OpenConnectionAsync(StoreKind store, CancellationToken ct);   // pooled
}
```

### 6.2 Module registration

```csharp
public interface IModuleStorageRegistration
{
    string Module { get; }                    // "Registry" → schema wp_registry
    Type DbContextType { get; }               // RegistryDbContext (neutral)
    IReadOnlyDictionary<string, Type> ProviderContexts { get; }  // "postgres" → RegistryPostgresDbContext
    int Order { get; }                        // migration order (ModuleCatalog order by default)
}

// in a module's ConfigureServices:
services.AddModuleStorage<RegistryDbContext>("Registry", providers => providers
    .Add<RegistryPostgresDbContext>("postgres"));
// registers IDbContextFactory<RegistryDbContext> that creates the provider-specific subclass.
// Contexts have the constructor (DbContextOptions options, StorageContextInfo info) and override
// OnModuleModelCreating(ModelBuilder); StorageContextInfo = (Module, IStorageNaming, TimeProvider).
```

`ConfigurationDbContextBase`:

- sets default schema from the module name;
- applies conventions: `uuid` ids with value converters for Kernel id types
  (`DeviceId` ↔ `uuid`), string-id converters, `DateTimeOffset` UTC enforcement (values with
  non-zero offset throw on save) and precision 3 (milliseconds), `LocalizedName` as JSON
  `{"default": …, "translations": {…}}` (`jsonb` on PostgreSQL; `LocalizedNameJsonConverter`);
- adds a `row_version bigint` concurrency token to entities implementing `IVersioned`
  (incremented by the base `SaveChangesAsync`, never by the database), used for ETags;
- adds `created_at`, `updated_at` to entities implementing `ITimestamped`.

### 6.3 Migration runner (startup phase `Schema`)

1. For each database used by any store: acquire the **migration lock**
   (PostgreSQL: `pg_try_advisory_lock(hashtext('watchpoint.migrations'))` polled every 250 ms on
   a dedicated connection; timeout 10 minutes → `storage.migration.lock_timeout`, exit 70).
2. Create schemas if missing; run provider bootstrap SQL (state tables §7.3) idempotently.
3. For each module registration in `Order`: `context.Database.MigrateAsync()`.
4. Release the lock. Log each applied migration (`STO1210`).

Rules:

- Migrations are **expand/contract**: a release only adds (tables, nullable columns,
  indexes); destructive changes happen one release later, after code no longer uses the old
  shape. Reviewers check this (PR template checkbox).
- A migration failure stops start-up (`StorageStartupException`, exit 70) and leaves the lock released.
- Start-up wait (`IStorageStartup.WaitForDatabasesAsync`): probes each database, retrying after
  1, 2, 4, 8, then every 15 s (`STO1202`) until `Database:StartupWait`; then
  `storage.unavailable` with exit 75. An unsupported version stops at once (exit 70); invalid
  settings stop with exit 78.
- Downgrades are not supported; restore from backup instead (DEP).

### 6.4 Health checks

`StorageHealthCheck` (tags `ready`): for each database, `SELECT 1` with 2 s timeout;
reports provider, server version, pool statistics. Unhealthy → node not ready.

## 7. Configuration and state stores

### 7.1 Configuration store

Modules use `IDbContextFactory<TModuleContext>` (short-lived contexts per operation).
Transactions across modules are **not** allowed (each module owns its schema); cross-module
consistency uses the configuration publish process (CFG) or the event log.

Entity conventions every module follows:

| Convention | Rule |
|---|---|
| Primary key | `id uuid` from Kernel ids (generated in code) |
| Concurrency | `row_version bigint` via `IVersioned` |
| Timestamps | `created_at`, `updated_at timestamptz` via `ITimestamped` |
| Soft delete | Not used by default; modules that need history of deletions use the event log |
| JSON columns | `jsonb` for validated documents (attributes, settings, translations) |
| Foreign keys | Within a module schema: real FKs. Across modules: plain `uuid` columns validated by the owning module's contract (no cross-schema FKs) |

### 7.2 State store contract

```csharp
public interface IStateStore
{
    // Writes a batch of item upserts/deletes and the consumer checkpoint for one partition, atomically.
    Task WriteSnapshotAsync(StateSnapshotBatch batch, CancellationToken ct);

    // Reads all items of a kind for given partitions (recovery); streamed.
    IAsyncEnumerable<StateItem> ReadItemsAsync(string kind, IReadOnlyCollection<int> partitions, CancellationToken ct);

    Task<IReadOnlyDictionary<int, long>> ReadCheckpointsAsync(string consumer, CancellationToken ct);
}

public sealed record StateSnapshotBatch(string Consumer, int Partition, long Sequence,
    IReadOnlyList<StateItem> Upserts, IReadOnlyList<StateItemKey> Deletes);

public sealed record StateItem(string Kind, Guid Key, int Partition, ReadOnlyMemory<byte> PayloadJson, long Sequence);
public sealed record StateItemKey(string Kind, Guid Key);
```

- `Kind` values: `device` (STA), `alarm` (ALM), `command` (CMD), `rule-window` (ALM), others
  added by modules; lowercase, ≤ 32 chars.
- `Consumer` is the logical owner of checkpoints (e.g. `state`, `alarms`, `commands`).
- `Sequence` in items = event-log sequence of the last change reflected (debug/consistency).
- The `IStateStore` resolved from DI wraps the provider's store: it validates batches
  (`StateStoreRules`: ≤ 5,000 items → else `StorageException` `storage.state.batch_too_large`; kinds
  and consumers `^[a-z][a-z0-9-]*$`, ≤ 32 chars → else `ArgumentException`; items in the batch's
  partition) and records the snapshot metrics and `STO1220`.
- `ConfigurationDbContextBase.SaveChanges*` turns `DbUpdateConcurrencyException` into
  `StorageException` with `storage.concurrency.conflict` (arg `entity`).

### 7.3 State tables (PostgreSQL)

```sql
CREATE SCHEMA IF NOT EXISTS wp_state;

CREATE TABLE IF NOT EXISTS wp_state.items (
  kind        text        NOT NULL,
  key         uuid        NOT NULL,
  partition   smallint    NOT NULL,
  payload     jsonb       NOT NULL,
  sequence    bigint      NOT NULL,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (kind, key)
);
CREATE INDEX IF NOT EXISTS items_kind_partition ON wp_state.items (kind, partition);

CREATE TABLE IF NOT EXISTS wp_state.checkpoints (
  consumer    text        NOT NULL,
  partition   smallint    NOT NULL,
  sequence    bigint      NOT NULL,
  written_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (consumer, partition)
);
```

`WriteSnapshotAsync` (PostgreSQL): one transaction; upserts via a single
`INSERT … SELECT FROM unnest(@kinds, @keys, @partitions, @payloads, @sequences) ON CONFLICT
(kind,key) DO UPDATE …`; deletes via `DELETE … WHERE (kind,key) IN (SELECT * FROM unnest(…))`;
checkpoint upsert; commit. Several upserts of the same key in one batch: the last wins. Batches up
to 5,000 items; larger inputs are split by the caller
(STA) into several batches, **with the checkpoint written only in the last one**.

Tables are created by the provider bootstrap (§6.3 step 2), not EF migrations (they are
provider-owned infrastructure).

## 8. Settings

| Key | Type | Default | Notes |
|---|---|---|---|
| `Database:Databases:<key>:Provider` | string | `postgres` | |
| `Database:Databases:<key>:Host` | string | `localhost` | |
| `Database:Databases:<key>:Port` | int | 5432 | |
| `Database:Databases:<key>:Name` | string | `watchpoint` | |
| `Database:Databases:<key>:Username` | string | `watchpoint` | |
| `Database:Databases:<key>:PasswordRef` | string | `bootstrap:database.default` | SEC bootstrap secret reference |
| `Database:Databases:<key>:PasswordDev` | string | — | Development only |
| `Database:Databases:<key>:Tls` | enum | `Prefer` (Production bundled: `Require`) | |
| `Database:Databases:<key>:AdditionalHosts` | list | [] | `host:port` |
| `Database:Databases:<key>:MaxPoolSize` | int | 50 | 5–500 |
| `Database:Stores:Configuration` / `State` / `History` / `Audit` | string | `default` | database key |
| `Database:StartupWait` | duration | `PT5M` | How long start-up waits for the database before exit 75 |

## 9. Errors

| Code | Kind | When |
|---|---|---|
| `storage.unavailable` | Unavailable | Database unreachable (args `store`) |
| `storage.provider.unknown` | Validation | Settings reference an unknown provider |
| `storage.provider.unsupported_version` | Unprocessable | Server version too old (args `minimum`, `actual`) |
| `storage.migration.failed` | Internal | Migration error (args `module`, `migration`) |
| `storage.migration.lock_timeout` | Unavailable | Could not get migration lock |
| `storage.concurrency.conflict` | PreconditionFailed | `row_version` mismatch on save (mapped by modules to `common.version.conflict`) |
| `storage.state.batch_too_large` | Validation | > 5,000 items in one batch (args `count`, `max`) |
| `storage.module.context_missing` | Validation | A module registered no context for the configured provider (args `module`, `provider`); start-up stops with exit 78 |
| `storage.settings.invalid` | Validation | Storage settings invalid; field errors name the keys (unknown database key, `PasswordDev` outside Development, insecure remote TLS, port/pool out of range, bad `StartupWait`) |

## 10. Observability

| EventId | Level | Message |
|---|---|---|
| 1201 | Information | Database {Key} ({Provider} {Version}) reachable |
| 1202 | Warning | Database {Key} unreachable, retrying in {DelayMs} ms |
| 1203 | Warning | TimescaleDB extension not available on {Key}; history uses plain partitioning |
| 1204 | Warning | Storage settings: {Warning} (e.g. TLS disabled for a remote host with `AllowInsecureRemote`) |
| 1210 | Information | Applied migration {Migration} for module {Module} |
| 1211 | Error | Migration {Migration} for module {Module} failed |
| 1212 | Information | Waiting for the migration lock of database {Key} |
| 1220 | Debug | State snapshot written: consumer {Consumer}, partition {Partition}, {Upserts} upserts, {Deletes} deletes, sequence {Sequence} |

Metrics (`Watchpoint.Storage`): `watchpoint.storage.snapshot.duration` (ms, tag `consumer`),
`watchpoint.storage.snapshot.items` (counter), `watchpoint.storage.pool.busy` (gauge, tag
`database`), `watchpoint.storage.database.size` (gauge, bytes, polled every 10 min).

## 11. Security

- Passwords from SEC references (or Development settings); never logged (connection strings
  logged with `Password` removed via `NpgsqlConnectionStringBuilder` sanitisation).
- Production default TLS `Require` for non-local hosts; `Disable` allowed only for
  `localhost`/Unix socket (validation error otherwise unless
  `Database:AllowInsecureRemote=true`, logged warning).
- Database roles (created by DEP installer): `watchpoint_app` (DML on wp_* schemas,
  DDL only through migration role), `watchpoint_migrate` (DDL), `watchpoint_report`
  (read-only views, M5). In M1 a single role may be used in Development.

## 12. Tests

Unit: `tests/service/Watchpoint.Storage.Tests`. Integration and certification:
`tests/service/Watchpoint.Storage.Certification` (`StorageCertificationTests`: STO-I-007…014, and
the `CertificationContext` module whose provider migration each provider supplies) with
`Watchpoint.IntegrationTests/Storage/PostgresCertificationTests` deriving from it for PostgreSQL
(plus STO-I-015). STO-I-001…006 are PostgreSQL-specific and live in
`Watchpoint.IntegrationTests/Storage/PostgresStorageTests`.

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| STO-U-001 | U | Default store mapping | No settings → four stores → `default` → `postgres` |
| STO-U-002 | U | Unknown database key rejected | Store History → `archive` (undefined) → settings validation error |
| STO-U-003 | U | Unknown provider rejected | `Provider: oracle` (not installed) → `storage.provider.unknown` |
| STO-U-004 | U | Dev password refused in Production | Production + `PasswordDev` → validation error |
| STO-U-005 | U | Insecure remote refused | Host `10.0.0.5`, `Tls: Disable` → validation error; `localhost` → ok |
| STO-U-006 | U | Naming: schema per module | Module "Registry" → `wp_registry` |
| STO-U-007 | U | Naming: table prefix fallback | Provider without schemas → `wp_registry__devices` |
| STO-U-008 | U | Id value converter round-trip | `DeviceId` ↔ `Guid` |
| STO-U-009 | U | Non-UTC DateTimeOffset rejected on save | Entity with +01:00 → `InvalidOperationException` |
| STO-U-010 | U | `row_version` increments on modification | Modified entity saved → version +1; unchanged → no change |
| STO-U-011 | U | Timestamps set | New entity → created=updated=now (TestClock); modified → updated changes |
| STO-U-012 | U | Migration order follows registrations | Three registrations orders 3,1,2 → migrated 1,2,3 |
| STO-U-013 | U | Migration failure stops and releases lock | Fake provider throwing in module 2 → exception; lock released; module 3 not run |
| STO-U-014 | U | Connection string sanitised in logs | Log contains host/db but not password |
| STO-U-015 | U | Startup wait then exit code 75 | DB never reachable, StartupWait PT1M (TestClock) → `storage.unavailable` and exit request 75 |
| STO-U-016 | U | State batch over 5,000 rejected | → `storage.state.batch_too_large` |
| STO-U-017 | U | State kind validation | Kind `Device` (uppercase) or > 32 chars → argument exception |
| STO-U-018 | U | Module registration requires provider context | `AddModuleStorage` without `postgres` context while provider postgres configured → startup validation error naming the module |
| STO-U-019 | U | DbContext factory creates provider subclass | postgres configured → `RegistryPostgresDbContext` instance |
| STO-U-020 | U | NoTracking default | Queried entity not tracked |
| STO-U-021 | U | LocalizedName JSON column mapping | Round-trip in model (in-memory SQLite provider used only for this mapping test) |
| STO-U-022 | U | `IVersioned` mismatch → concurrency error | Simulated stale version → `storage.concurrency.conflict` |
| STO-U-023 | U | Health check reports provider and version | Fake provider → data contains `provider`, `version` |
| STO-U-024 | U | Capabilities: TimescaleDB probed | Probe without extension → `TimeSeriesExtension=false`, warning 1203 |
| STO-U-025 | U | Version below 16 rejected | Probe "15.4" → `storage.provider.unsupported_version` |
| STO-I-001 | I | Probe real PostgreSQL | Container → version ≥ 16, `timescaledb` in extensions |
| STO-I-002 | I | Migrations create module schema and history table | Test module context with one table → `wp_testmod.__ef_migrations` row exists |
| STO-I-003 | I | Migrations idempotent | Run twice → no error, no duplicate rows |
| STO-I-004 | I | Concurrent nodes: only one migrates at a time | Two runners in parallel on same DB → both complete; migrations applied once; second waited on advisory lock |
| STO-I-005 | I | Health check unhealthy when DB stops | Stop container → unhealthy within 5 s; restart → healthy |
| STO-I-006 | I | Pool exhaustion surfaces as timeout, not hang | MaxPoolSize 5, 6 long-running connections → 6th fails within `Timeout` |
| STO-I-007 | I | State tables bootstrapped | `wp_state.items`, `wp_state.checkpoints` exist after start |
| STO-I-008 | I | Snapshot upsert and read back | Write 3 items + checkpoint 10 → read items for partition → 3; checkpoints → {p: 10} |
| STO-I-009 | I | Snapshot update and delete | Second batch updates 1, deletes 1 → read returns 2 with new payload |
| STO-I-010 | I | Snapshot atomicity | Fault injected after upserts (test hook throwing before commit) → neither items nor checkpoint changed |
| STO-I-011 | I | Large snapshot performance | 5,000 items batch, p95 of 20 runs after one warm-up batch → p95 < 500 ms on a developer machine or with `WATCHPOINT_STRICT_TIMING=1` (benchmark run); on shared CI runners (`CI=true`) p95 < 2 s (TST §4.7), and the measured p95 is written to the test output. The certification class runs in a non-parallel collection so the measurement is not shared with other tests; shared-runner disk I/O noise still exceeds 500 ms, while 2 s still catches a batch written row by row. All 5,000 items are read back in every environment |
| STO-I-012 | I | Recovery stream ordering independence | Items read for partitions {1,3} only return those partitions |
| STO-I-013 | I | Concurrency token enforced in database round-trip | Two contexts load same entity; both save → second gets `storage.concurrency.conflict` |
| STO-I-014 | I | UTC precision | Save `…:30.1234567Z` → read `…:30.123Z` |
| STO-I-015 | I | Certification suite skeleton runs for postgres | Abstract certification tests (this table's I tests are the first members) executed via `PostgresCertificationTests` |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.03 | §4, §5, §6, §9, §10, §11 | STO-U-001…STO-U-015, STO-I-001…STO-I-006 |
| T1.04 | §7, §8 | STO-U-016…STO-U-025, STO-I-007…STO-I-015 |
