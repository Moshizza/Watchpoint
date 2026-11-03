# Watchpoint — Implementation Architecture

This folder contains the **implementation specifications** of Watchpoint: what to build,
in which order, with which interfaces, data structures, behaviour and tests. It is written
so that a developer or an AI coding agent can implement a task **without guessing**.

The **why** (product design, choices, trade-offs) lives in [`docs/`](../docs/README.md).
Specs link to it instead of repeating it. When a spec and `docs/` disagree, the spec wins
for implementation, and the discrepancy must be reported (issue labelled `docs-mismatch`).

## 1. Contents

| Document | Purpose |
|---|---|
| [README.md](README.md) (this file) | Conventions: repository layout, versions, coding rules, errors, logging, testing, CI, Git workflow, definition of done |
| [build-order.md](build-order.md) | Milestones in build order; each ends with something runnable |
| [tasks.md](tasks.md) | Task sheet: every task, dependencies, specs, tests, owner, status |
| [specs/](specs/) | One specification per module or connector |
| [tests/](tests/) | Cross-module test specifications: integration between modules and end-to-end scenarios |

## 2. How to read a spec

Every spec follows the same template:

| Section | Content |
|---|---|
| 0. Header | Module id, project names, milestone, owner tasks, design document links |
| 1. Purpose and scope | What the module does and explicitly does **not** do |
| 2. Dependencies | Modules it uses; modules that use it |
| 3. Project layout | Projects, folders, main files |
| 4. Public contract | Interfaces and types other modules use (C# or TypeScript sketches, no bodies) |
| 5. Internal design | Main internal classes, state machines, algorithms |
| 6. Data | Tables (columns, types, keys, indexes), JSON schemas, log messages |
| 7. API | REST endpoints and live-stream messages owned by the module |
| 8. Configuration | Settings with types, defaults and validation |
| 9. Errors | Error codes the module returns |
| 10. Observability | Logs (event ids), metrics, health checks |
| 11. Security | Permissions checked, data protection, threats handled |
| 12. Tests | Test cases with ids, layer, setup, steps, expected result |
| 13. Tasks | Task ids from [tasks.md](tasks.md) that implement the spec |

Code blocks in specs are **sketches**: they fix names, signatures, fields and semantics.
Implementers may add private members, overloads and helper classes, but must not change
public names or semantics without updating the spec in the same pull request.

Words used with precise meaning: **must** (mandatory), **should** (expected unless there is
a documented reason), **may** (optional).

## 3. Repository layout

```
/                                   repository root
├── Architecture/                   implementation specs (this folder)
├── docs/                           product and design documentation
├── src/
│   ├── service/                    the node (.NET)
│   │   ├── Watchpoint.slnx         solution (XML solution format)
│   │   ├── Directory.Build.props   shared build settings
│   │   ├── Directory.Packages.props central package versions
│   │   ├── Watchpoint.Kernel/      shared primitives (ids, results, errors, clock, paging)
│   │   ├── Watchpoint.<Module>/    one project per module (e.g. Watchpoint.Registry)
│   │   ├── Watchpoint.<Module>.Abstractions/  public contract when other processes need it
│   │   ├── Watchpoint.Host/        the service executable
│   │   ├── Watchpoint.IntegrationHost/  isolated integration host executable
│   │   ├── Watchpoint.ScriptHost/  script sandbox executable
│   │   └── Watchpoint.Ctl/         wpctl command-line tool
│   ├── integrations/
│   │   ├── Watchpoint.Integration.Abstractions/  connector contract (published)
│   │   ├── Watchpoint.Integration.Sdk/           helpers for connectors
│   │   ├── Watchpoint.Integration.Testing/       conformance suite + simulator base
│   │   └── Watchpoint.Integration.<Name>/        one project per integration package
│   ├── ui/                         TypeScript monorepo (pnpm workspace)
│   │   ├── packages/<name>/        @watchpoint/<name> libraries
│   │   └── apps/
│   │       ├── desktop/            Electron app
│   │       └── web/                web client (operation only)
│   └── mobile/                     React Native + Expo app
├── schemas/                        JSON Schemas (device types, capabilities, manifest, synoptic, automation…)
├── types/                          standard device type and capability library (JSON documents)
├── tests/
│   ├── service/                    Watchpoint.<Module>.Tests (unit) and Watchpoint.IntegrationTests
│   ├── integrations/               connector tests
│   ├── e2e/                        end-to-end scenarios (Playwright + test harness)
│   └── performance/                k6 scripts and load scenarios
├── tools/                          wppack (package builder), dev scripts, code generators
├── deploy/                         docker compose, Helm chart, installer definitions
└── .github/                        workflows, issue and pull-request templates
```

Rules:

- A module project may reference `Watchpoint.Kernel`, other modules' **public contract**
  (interfaces in a `Contracts` folder/namespace or an `.Abstractions` project), never their
  internals. This is enforced by an architecture test (see §9.4).
- `Watchpoint.Host` composes all modules; it is the only project that references them all.
- No project references `Watchpoint.Host`.

## 4. Versions and tools

| Tool | Version policy |
|---|---|
| .NET SDK | **.NET 10 (LTS)**, C# latest stable; `global.json` pins the SDK feature band |
| Node.js | **Node 24 LTS** (`.nvmrc`), **pnpm 10** (`packageManager` field) |
| TypeScript | Latest stable 5.x at project start, pinned; `strict: true` |
| React | 19.x |
| Electron | Latest stable at project start, pinned; upgraded each quarter |
| React Native / Expo | Latest Expo SDK at project start, pinned |
| PostgreSQL | **17** bundled; 16–18 supported |
| TimescaleDB | Latest 2.x compatible with the bundled PostgreSQL |
| NATS server | Latest 2.x (≥ 2.11) |
| go2rtc | Latest 1.x |
| Docker / Podman | For tests (Testcontainers) and container images |

Exact versions are pinned in `global.json`, `Directory.Packages.props`, `package.json`,
`pnpm-lock.yaml` and `deploy/versions.env`. Upgrades are tasks of their own.

## 5. Coding conventions — .NET

### 5.1 Build settings (`Directory.Build.props`)

- `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`,
  `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`,
  `<InvariantGlobalization>false</InvariantGlobalization>` (ICU needed for localisation).
- `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`; `.editorconfig` at the root.
- Deterministic builds, SourceLink, `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
  for contract projects.

### 5.2 Naming and style

- Namespaces = project names: `Watchpoint.Registry`, `Watchpoint.Registry.Contracts`,
  `Watchpoint.Registry.Internal`.
- Interfaces `IThing`; async methods end with `Async` and take a `CancellationToken ct` as
  the last parameter (mandatory, no default value in contracts).
- Records for immutable data (`sealed record`), classes for services. File-scoped
  namespaces. One public type per file.
- No static mutable state. Services are registered in DI with explicit lifetimes.
- `internal` by default; `public` only for the contract.

### 5.3 Shared primitives (`Watchpoint.Kernel`, see [spec K](specs/K-kernel.md))

- **Identifiers**: strongly typed record structs wrapping a UUID v7
  (`DeviceId`, `ServerId`, `UserId`…), created with `Guid.CreateVersion7()`. Serialised as
  lowercase UUID strings.
- **Results**: operations that can fail for business reasons return `Result<T>` /
  `Result` carrying an `Error`. Exceptions are for programming errors and infrastructure
  failures only.
- **Errors**: `Error(string Code, string MessageKey, IReadOnlyDictionary<string, object?> Args, ErrorKind Kind)`.
  Codes are lowercase dotted: `<module>.<entity>.<problem>`, e.g. `registry.device.not_found`.
  The translation key is `errors.<code>`. `ErrorKind` maps to HTTP status (§6.2).
- **Time**: inject .NET `TimeProvider` (tests use `FakeTimeProvider`); always UTC
  `DateTimeOffset` with millisecond precision; NodaTime for time zones and calendars.
- **Paging**: `PageRequest(int Limit, string? Cursor)`, `Page<T>(IReadOnlyList<T> Items, string? NextCursor)`;
  default limit 100, maximum 1,000.

### 5.4 Asynchrony and concurrency

- All I/O is async. No `.Result`/`.Wait()`.
- Producer/consumer queues use `System.Threading.Channels` with **bounded** capacity and an
  explicit full mode.
- Background work runs in `BackgroundService` implementations registered by the module.
- Every loop that waits must be cancellable and must back off on errors.

### 5.5 Serialisation

- **System.Text.Json** with source generators for every serialised type; camelCase names;
  enums as camelCase strings; no polymorphic type names from input (no `TypeNameHandling`
  equivalents). Unknown properties: ignored on reading API inputs only where the spec says
  so; rejected for configuration documents.
- Event log records are JSON (UTF-8) with an envelope (see [spec PIP](specs/PIP-pipeline.md)).

### 5.6 Data access

- Only through `Watchpoint.Storage` store interfaces (see [spec STO](specs/STO-storage.md)).
- EF Core for configuration; provider-specific bulk paths for history.
- Migrations per provider, generated then reviewed; names `YYYYMMDDHHmm_<Description>`.

## 6. Conventions — API

### 6.1 REST

- Base path `/api/v1`. JSON bodies, camelCase. Plural resource names.
- `GET /things?limit=&cursor=&sort=&filter...` → `{ "items": [...], "nextCursor": "..." }`.
- `GET /things/{id}`, `POST /things` (201 + `Location`), `PATCH /things/{id}` (JSON Merge
  Patch, RFC 7396), `DELETE /things/{id}` (204).
- Concurrency: responses carry `ETag: "<version>"`; updates require `If-Match`; mismatch →
  412 with error `common.version.conflict`.
- Idempotency: `Idempotency-Key` header on non-idempotent POSTs; same key + same body
  within 24 h returns the original response.
- Every endpoint declares its permission(s) in its spec and in code via an endpoint filter.

### 6.2 Errors (RFC 9457 problem details)

```json
{
  "type": "https://watchpoint.dev/errors/registry.device.not_found",
  "title": "Device not found",
  "status": 404,
  "code": "registry.device.not_found",
  "messageKey": "errors.registry.device.not_found",
  "args": { "deviceId": "0192…" },
  "traceId": "00-…"
}
```

| ErrorKind | HTTP |
|---|---|
| `Validation` | 400 (with `errors` array of field problems) |
| `Unauthenticated` | 401 |
| `Forbidden` | 403 |
| `NotFound` | 404 |
| `Conflict` | 409 |
| `PreconditionFailed` | 412 |
| `Unprocessable` (business rule) | 422 |
| `TooManyRequests` | 429 |
| `Unavailable` (dependency down) | 503 |
| `Internal` | 500 (no details leaked) |

Validation errors: `"errors": [{ "path": "/settings/port", "code": "common.value.out_of_range", "args": { "min": 1, "max": 65535 } }]`.

### 6.3 Live stream

SignalR hub at `/hub/v1`, MessagePack protocol. Message envelope and subscription model in
[spec API](specs/API-api-and-live.md).

## 7. Conventions — TypeScript / UI

- pnpm workspace; packages `@watchpoint/<name>`; ESM only; `strict` TypeScript; ESLint
  (typescript-eslint), Prettier.
- React function components and hooks; state with Zustand for UI state, TanStack Query for
  server state; no business logic in components (put it in hooks/services).
- All user-visible strings through `@watchpoint/i18n` (no literals in JSX). CSS with
  Tailwind **logical** utilities only (`ms-`, `me-`, `ps-`, `pe-`, `start-`, `end-`), so RTL
  works.
- API types are **generated** from the OpenAPI document (`pnpm gen:api`), never hand-written.
- Tests: Vitest + Testing Library for units, Playwright for end-to-end.

## 8. Observability conventions

- **Logs**: Serilog, structured message templates, no string concatenation. Each module owns
  an EventId range (see table) so logs can be filtered by number.
- Never log secrets, tokens, passwords, personal data (cardholder names, plates) except
  through the redaction-aware helpers.
- **Metrics**: OpenTelemetry `Meter` named `Watchpoint.<Module>`; instrument names
  `watchpoint.<module>.<what>` with units (`{events}`, `ms`, `By`).
- **Traces**: `ActivitySource` named `Watchpoint.<Module>`; propagate trace context through
  API calls, the event log envelope and gRPC.
- **Health checks**: each module registers ASP.NET Core health checks tagged `live` and/or
  `ready`.

| Module | EventId range | Module | EventId range |
|---|---|---|---|
| Kernel | 1000–1099 | Commands | 2400–2499 |
| Host | 1100–1199 | Events/Alarms | 2500–2699 |
| Storage | 1200–1299 | Identity | 2700–2799 |
| Configuration | 1300–1399 | Rights/Audit | 2800–2899 |
| Model | 1400–1499 | Automations | 2900–2999 |
| Registry | 1500–1599 | Scripting | 3000–3099 |
| Pipeline | 1600–1699 | Notifications | 3100–3199 |
| State | 1700–1799 | History/Reports | 3200–3299 |
| Integrations runtime | 1800–1999 | Video | 3300–3399 |
| API | 2000–2199 | Licensing | 3400–3499 |
| Secrets | 2200–2299 | Cluster/Federation | 3500–3699 |
| Files/Scheduling | 2300–2399 | Health/Maintenance | 3700–3799 |
| Connectors | 5000–5999 (100 per package, assigned in each connector spec) | Licensing portal / push relay (separate products) | 6000–6099 / 6100–6199 |

## 9. Testing conventions

### 9.1 Test layers

| Layer | Code | What | Where | Runs |
|---|---|---|---|---|
| Unit | `U` | One class or function, no I/O (fakes for dependencies) | `tests/service/Watchpoint.<Module>.Tests`, `*.test.ts` | Every push |
| Integration | `I` | A module with its real infrastructure (PostgreSQL, NATS via Testcontainers) | `tests/service/Watchpoint.IntegrationTests/<Module>` | Every push |
| Contract | `C` | The agreement between two modules or processes (interface behaviour, API schema, gRPC messages), run against the real provider | `tests/service/Watchpoint.ContractTests` | Every push |
| End-to-end | `E` | Full node + client scenarios | `tests/e2e` | Nightly and before merge of milestone branches |
| Performance | `P` | Throughput, latency, capacity | `tests/performance` | Nightly on reference hardware |
| Security | `S` | Authorisation, injection, secrets in logs | Mixed | Every push (automated parts) |

### 9.2 Test ids

`<SPEC>-<LAYER>-<NNN>`, e.g. `REG-U-012` (Registry unit test 12), `PIP-I-003`,
`X-API-REG-C-001` for cross-module contract tests (in [tests/](tests/)). Test method names
include the id: `REG_U_012_Rediscovery_matches_devices_by_native_id`. A test id is never
reused; deleted tests leave a gap.

### 9.3 Test case format in specs

```
REG-U-012 — Rediscovery matches devices by native id
Layer: Unit
Given: device D1 (nativeId "door-7", name "Lobby") registered on server S1
When:  connection reports inventory with nativeId "door-7", name "Main lobby"
Then:  no new device is created; D1.vendorName = "Main lobby"; D1.name unchanged if user-overridden
```

### 9.4 Rules

- Every public contract method has at least one unit test for success and one per error
  code it can return.
- Every module has integration tests against PostgreSQL (and NATS where used).
- Architecture tests (NetArchTest, MIT) enforce project dependency rules (§3).
- Tests are deterministic: time through `FakeTimeProvider`, randomness seeded, no sleeps
  (use clocks and signals).
- Test data builders in `Watchpoint.Testing` (shared test helpers project).
- Coverage target: 80 % lines for modules, 90 % for Kernel, Rights and Commands. Coverage is
  a signal, not a goal: the listed test cases are mandatory regardless of coverage.

## 10. Continuous integration (GitHub Actions)

Workflows in `.github/workflows/`:

| Workflow | Trigger | Jobs |
|---|---|---|
| `ci.yml` | Every push and pull request | Restore → build service (warnings as errors) → unit tests → integration and contract tests (Testcontainers on Linux runner) → UI lint, type-check, unit tests, build → architecture tests → secret scan → Semgrep |
| `e2e.yml` | Nightly, manual, PRs labelled `e2e` | Build images, start compose stack, run Playwright scenarios |
| `perf.yml` | Nightly (self-hosted reference runner when available) | k6 scenarios, publish results |
| `release.yml` | Tags `v*` | Build installers and images, SBOM, sign, publish |

GitHub Free includes 2,000 Actions minutes per month for private repositories; Linux jobs
are preferred (Windows jobs count double, macOS ten times). Windows/macOS builds of the
desktop client run on release and nightly only.

## 11. Git workflow

- `main` is the reference branch; it must always build and pass `ci.yml`.
- **One branch per task**: `<task-id>-<short-slug>`, e.g. `T1.04-registry-core`.
- Commit messages: Conventional Commits (`feat(registry): …`, `fix(pipeline): …`,
  `test(…)`, `docs(…)`), body explaining why, reference the task id.
- Pull request per task: title `<task-id> <title>`; description lists implemented test ids;
  at least **one approval** from another team member and green CI before merge; **squash
  merge**; delete the branch after merge.
- Specs change in the same pull request as the code when behaviour changes.
- During the documentation phase only, the documentation author commits directly to `main`.

## 12. Definition of done (every task)

1. Code implements the spec sections listed in the task.
2. All test cases listed in the task exist, pass, and carry their ids.
3. CI green; no new warnings; no `TODO` without an issue reference.
4. Public contract documented (XML docs / TSDoc).
5. Logs, metrics and health checks from the spec's observability section exist.
6. User-visible strings have translation keys in `en`, `fr`, `ar` (machine translation
   allowed for `fr`/`ar` until reviewed; marked `needs-review` in Weblate).
7. Spec updated if anything changed; [tasks.md](tasks.md) status set to *done*.
8. Reviewed and approved by another team member.
