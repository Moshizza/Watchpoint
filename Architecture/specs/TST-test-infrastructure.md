# TST — Shared Test Infrastructure

| | |
|---|---|
| Spec id | TST |
| Projects | `tests/service/Watchpoint.Testing`, `tests/service/Watchpoint.Testing.Containers`, `tests/service/Watchpoint.ArchitectureTests`, `src/service/Watchpoint.Analyzers` |
| Milestone | M0 |
| Tasks | T0.05 |
| Design docs | [Architecture README §9](../README.md#9-testing-conventions) |

## 1. Purpose and scope

Give every .NET test project the same tools so tests are fast, deterministic and consistent:

- common helpers (time, ids, logs, async waiting without sleeps);
- real infrastructure in containers (PostgreSQL + TimescaleDB, NATS JetStream), isolated per
  test class;
- architecture tests enforcing project dependency rules;
- Roslyn analyzers enforcing coding rules that tests and reviews otherwise miss.

UI test infrastructure (Vitest, Testing Library, MSW mocks) is specified in
[UIC §10](UIC-ui-core.md); end-to-end infrastructure in
[tests/e2e-scenarios.md](../tests/e2e-scenarios.md).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K (Kernel), xUnit v3, Shouldly, NSubstitute, Testcontainers for .NET, NetArchTest.Rules, Microsoft.Extensions.TimeProvider.Testing, Microsoft.CodeAnalysis (analyzers) | All test projects; analyzers by all service and connector projects |

All are free and allow closed-source use (MIT, BSD-3, Apache-2).

## 3. Project layout

```
tests/service/
├── Watchpoint.Testing/
│   ├── Time/TestClock.cs                 FakeTimeProvider wrapper with helpers
│   ├── Ids/TestIds.cs                    deterministic ids
│   ├── Logging/LogCollector.cs           ILoggerProvider capturing structured logs
│   ├── Logging/SecretLeakDetector.cs
│   ├── Async/Eventually.cs               condition polling driven by a signal or clock
│   ├── Async/TestTimeouts.cs
│   └── Json/JsonAssert.cs                semantic JSON comparison
├── Watchpoint.Testing.Containers/
│   ├── PostgresContainerFixture.cs
│   ├── PostgresDatabase.cs               per-test-class database
│   ├── NatsContainerFixture.cs
│   ├── NatsScope.cs                      per-test-class stream/subject prefix
│   └── ContainerImages.cs                pinned image references
├── Watchpoint.ArchitectureTests/
│   └── DependencyRulesTests.cs
src/service/
└── Watchpoint.Analyzers/                 Roslyn analyzers (netstandard2.0)
    ├── DefaultResultAnalyzer.cs          WP0001
    ├── TestNamingAnalyzer.cs             WP0002
    ├── WallClockAnalyzer.cs              WP0003
    ├── BlockingWaitAnalyzer.cs           WP0004
    └── SleepInTestsAnalyzer.cs           WP0005
```

`Directory.Build.props` adds `Watchpoint.Analyzers` as an analyzer reference to every
project under `src/` and `tests/`.

## 4. Helpers (`Watchpoint.Testing`)

### 4.1 `TestClock`

```csharp
public sealed class TestClock : FakeTimeProvider
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    public TestClock(DateTimeOffset? start = null);         // auto-advance disabled
    public void Advance(TimeSpan by);                         // fires due timers in order
    public void AdvanceTo(DateTimeOffset utc);
}
```

### 4.2 `TestIds`

`TestIds.Device(1)` → `DeviceId` with value `00000000-0000-7000-8000-000000000001`; one
method per id type; sequence numbers make test failures readable.

### 4.3 `LogCollector`

```csharp
public sealed class LogCollector : ILoggerProvider
{
    public IReadOnlyList<CapturedLog> Entries { get; }
    public IEnumerable<CapturedLog> WithEventId(int id);
    public void Clear();
}
public sealed record CapturedLog(string Category, LogLevel Level, int EventId,
    string MessageTemplate, IReadOnlyDictionary<string, object?> Properties,
    string RenderedMessage, Exception? Exception);
```

### 4.4 `SecretLeakDetector`

```csharp
public sealed class SecretLeakDetector
{
    public void Register(string secretValue);   // e.g. a password used in the test
    public void AssertNoLeak(LogCollector logs); // fails if any rendered message, property or exception text contains a registered value (case-insensitive, also base64 and URL-encoded forms)
}
```

Every module that handles secrets uses it in at least one test (security layer `S`).

### 4.5 `Eventually`

```csharp
public static class Eventually
{
    // Polls condition each time `signal` fires or every `pollInterval` (real time, default 20 ms),
    // until true or `timeout` (default TestTimeouts.Default = 5 s; 30 s in CI for container tests).
    public static Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null,
                                  string? because = null, CancellationToken ct = default);
    public static Task<T> UntilAsync<T>(Func<T?> probe, TimeSpan? timeout = null, ...) where T : class;
}
```

Used only for real asynchronous infrastructure (containers, background services). Unit
tests use `TestClock` and direct calls instead.

### 4.6 `JsonAssert`

`JsonAssert.Equivalent(expectedJson, actualJson, options)` — compares parsed JSON
(ignores property order and whitespace; options to ignore paths like `/traceId`).

## 5. Containers (`Watchpoint.Testing.Containers`)

### 5.1 Images (pinned)

| Purpose | Image | Notes |
|---|---|---|
| PostgreSQL + TimescaleDB | `timescale/timescaledb:<version>-pg17` pinned by digest in `ContainerImages.cs` | Same version as bundled |
| NATS | `nats:<2.x>` pinned by digest, started with `-js` | JetStream enabled, memory storage for speed |

Digests are updated by the dependency-upgrade task, never ad hoc.

### 5.2 PostgreSQL fixture

```csharp
public sealed class PostgresContainerFixture : IAsyncLifetime   // xUnit assembly fixture
{
    public string AdminConnectionString { get; }
    public Task<PostgresDatabase> CreateDatabaseAsync(string testClassName, CancellationToken ct);
}
public sealed class PostgresDatabase : IAsyncDisposable
{
    public string ConnectionString { get; }   // dedicated database "t_<hash>"
    // DisposeAsync drops the database (WITH (FORCE))
}
```

- One container per test assembly run (assembly fixture), started lazily.
- One **database per test class**, created from a template database that has the
  extensions (`timescaledb`, `pgcrypto`) already installed, so creation takes < 200 ms.
- Migrations are applied by the module's own test setup (so migrations are tested too).
- Container settings for speed: `fsync=off`, `synchronous_commit=off`,
  `full_page_writes=off` (tests only).

### 5.3 NATS fixture

```csharp
public sealed class NatsContainerFixture : IAsyncLifetime
{
    public string Url { get; }
    public NatsScope CreateScope(string testClassName);
}
public sealed record NatsScope(string SubjectPrefix, string StreamPrefix);
// e.g. SubjectPrefix "t1a2b.wp", StreamPrefix "T1A2B_" — modules accept prefixes via options
```

Modules using NATS must accept a subject/stream prefix in their options (see PIP §8), so tests
run in parallel on one server.

### 5.4 Docker availability

If Docker is not available locally, container tests are **skipped with an explicit reason**
(`Assert.Skip("Docker not available")`) locally, but CI sets
`WATCHPOINT_REQUIRE_CONTAINERS=1`, which turns the skip into a failure.

## 6. Architecture tests (`Watchpoint.ArchitectureTests`)

Rules (NetArchTest), each a test:

| Rule | Description |
|---|---|
| ARCH-1 | `Watchpoint.Kernel` depends on no other `Watchpoint.*` assembly |
| ARCH-2 | No assembly depends on `Watchpoint.Host` |
| ARCH-3 | A module assembly may depend on another module only through types in namespaces ending with `.Contracts` or assemblies ending with `.Abstractions` |
| ARCH-4 | Types in `*.Internal` namespaces are not public |
| ARCH-5 | Only `Watchpoint.Storage*` assemblies reference `Microsoft.EntityFrameworkCore.*` providers (`Npgsql.EntityFrameworkCore.*`, `Microsoft.EntityFrameworkCore.SqlServer`…) |
| ARCH-6 | `Watchpoint.Integration.Abstractions` depends only on `Watchpoint.Kernel` and BCL |
| ARCH-7 | Connector assemblies (`Watchpoint.Integration.<Name>`) do not reference `Watchpoint.*` service modules (only Abstractions, Sdk, Kernel) |
| ARCH-8 | No type named `*Controller` (minimal APIs only) |

The module list is discovered by assembly name pattern so new modules are covered
automatically.

## 7. Analyzers (`Watchpoint.Analyzers`)

| Id | Severity | Rule | Applies to |
|---|---|---|---|
| WP0001 | Error | `default(Result)` / `default(Result<T>)` / `new Result<T>()` is not allowed | All |
| WP0002 | Error | A test method (`[Fact]`/`[Theory]`) name must start with a test id: `^[A-Z][A-Z0-9-]*_[UICEPS]_\d{3}_` (e.g. `REG_U_012_…`) | Test projects |
| WP0003 | Error | `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now/UtcNow`, `Stopwatch.StartNew` outside `TimeProvider` usage are forbidden | `src/` (not tests' infrastructure) |
| WP0004 | Error | `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` on tasks are forbidden | All except `Main` methods |
| WP0005 | Error | `Thread.Sleep` and `Task.Delay` without a `TimeProvider` argument are forbidden in tests | Test projects |

Each analyzer ships with a code-fix where obvious (WP0003 → inject `TimeProvider`).

## 8. Test project conventions

- One test project per module: `tests/service/Watchpoint.<Module>.Tests` (unit).
- Integration tests: `tests/service/Watchpoint.IntegrationTests` with one folder per module;
  contract tests: `tests/service/Watchpoint.ContractTests`.
- xUnit v3 with `[assembly: CollectionBehavior(MaxParallelThreads = -1)]`; tests must be
  safe in parallel (no shared static state, separate databases/prefixes).
- Traits: `[Trait("layer", "U|I|C|E|P|S")]`, `[Trait("spec", "REG")]` — CI filters by layer.
- Builders for a module's types live in that module's test project (`Builders/`); builders
  used across modules live in `Watchpoint.IntegrationTests/Builders`.

## 9. Observability, security

Not applicable (test-only), except `SecretLeakDetector` (§4.4).

## 10. Tests of the test infrastructure

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| TST-U-001 | U | `TestClock.Advance` fires timers in order | Timers at +1 s and +2 s; advance 3 s → callbacks fired in order, each once |
| TST-U-002 | U | `TestIds` are deterministic and version-7 shaped | `TestIds.Device(1)` twice → equal; `Value.Version == 7` |
| TST-U-003 | U | `LogCollector` captures structured properties | Log `"Opened {DeviceId}"` → entry with property `DeviceId` and EventId |
| TST-U-004 | U | `SecretLeakDetector` finds plain, base64 and URL-encoded leaks | Register "p@ss"; log each form → `AssertNoLeak` fails with the location |
| TST-U-005 | U | `SecretLeakDetector` passes when absent | No leak → passes |
| TST-U-006 | U | `Eventually` returns when condition becomes true | Condition true after 3 polls → completes |
| TST-U-007 | U | `Eventually` fails with message | Never true, timeout 100 ms → exception containing `because` text |
| TST-U-008 | U | `JsonAssert` ignores order and listed paths | Same objects different order, differing `/traceId` ignored → equal |
| TST-U-009 | U | WP0001 reports `default(Result<int>)` | Analyzer test (Microsoft.CodeAnalysis.Testing) → diagnostic at location |
| TST-U-010 | U | WP0002 reports a badly named test | `[Fact] void Works()` → diagnostic; `REG_U_001_Works` → none |
| TST-U-011 | U | WP0003 reports `DateTime.UtcNow` | diagnostic + code fix test |
| TST-U-012 | U | WP0004 reports `.Result` | diagnostic |
| TST-U-013 | U | WP0005 reports `Thread.Sleep` in a test project | diagnostic |
| TST-I-001 | I | PostgreSQL container starts with TimescaleDB | `SELECT extname FROM pg_extension` contains `timescaledb` |
| TST-I-002 | I | Per-class databases are isolated | Two databases; table created in one not visible in the other |
| TST-I-003 | I | Database creation from template under 500 ms | Measure 10 creations → p95 < 500 ms (CI) |
| TST-I-004 | I | Database dropped on dispose | After dispose, `pg_database` has no row |
| TST-I-005 | I | NATS container has JetStream | Create stream with scope prefix → succeeds |
| TST-I-006 | I | NATS scopes isolate subjects | Publish on scope A not received by consumer of scope B |
| TST-I-007 | I | Missing Docker locally → skip; with `WATCHPOINT_REQUIRE_CONTAINERS=1` → fail | Simulated by fixture option |
| TST-I-008 | I | ARCH-1…ARCH-8 pass on the solution | Architecture tests green |
| TST-I-009 | I | ARCH-3 detects a violation | A test-only fixture assembly referencing another module's `Internal` type → rule fails |
| TST-I-010 | I | Parallel test classes do not interfere | 8 classes in parallel each create/drop DB and NATS scope → all pass |

## 11. Tasks

| Task | Sections | Tests |
|---|---|---|
| T0.05 | §3–§8 | TST-U-001…TST-U-013, TST-I-001…TST-I-010 |
