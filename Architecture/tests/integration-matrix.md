# Integration Matrix — Contract Tests Between Modules

Each module's spec tests the module itself. This document lists the tests of the **connections
between modules and processes**: one module calling another's contract, or two processes talking
through a protocol. They live in `tests/service/Watchpoint.ContractTests` (in-process contracts,
real implementations of both sides, infrastructure in containers) and in `tests/ui/contract`
(UI ↔ API).

Id format: `X-<CALLER>-<CALLEE>-C-NNN`. A contract test uses the **real** implementations of both
sides (no fakes for the pair under test), fakes only for third parties.

This matrix grows with each milestone; the milestone's e2e task (T*.xx "E2E scenarios") is also
responsible for adding the missing rows of its milestone.

## 1. Matrix overview (M1)

| Caller ↓ / Callee → | STO | SEC | MOD | REG | PIP | STA | IDN | IRT | API |
|---|---|---|---|---|---|---|---|---|---|
| HST | §2.1 | §2.1 | §2.1 | §2.1 | §2.1 | §2.1 | §2.1 | §2.1 | §2.1 |
| SEC | §2.2 | | | | | | | | |
| REG | §2.2 | | §2.3 | | §2.4 | | | | |
| PIP | | | §2.5 | §2.5 | | §2.6 | | | |
| STA | §2.2 | | §2.7 | §2.7 | §2.6 | | | | |
| IRT | §2.2 | §2.8 | §2.8 | §2.8 | §2.9 | | | | |
| C-SIM (via INT) | | | | | | | | §2.10 | |
| API | §2.2 | | §2.11 | §2.11 | | §2.11 | §2.11 | §2.11 | |
| UIC (TypeScript) | | | | | | | | | §2.12 |
| DSK | | | | | | | | | §2.13 |

## 2. Contract tests (M1)

### 2.1 Host start-up orchestration (all modules)

| Id | Test |
|---|---|
| X-HST-ALL-C-001 | Full host with every M1 module starts in phase order on empty PostgreSQL + NATS: migrations applied for each module schema, streams provisioned, standard library loaded, registry site created (dev seed), readiness true |
| X-HST-ALL-C-002 | Start-up task failure in any module (injected) stops the host with exit code 70 and the failing module named in log 1112 |
| X-HST-ALL-C-003 | Graceful shutdown runs STA final snapshot, IRT stops connections, PIP flushes — then exit 0 within `Shutdown:Timeout` |
| X-HST-ALL-C-004 | Second start on the same database is idempotent (no duplicate seed, no migration rerun) |

### 2.2 Modules ↔ Storage

| Id | Test |
|---|---|
| X-ALL-STO-C-001 | Every module context's migrations apply on PostgreSQL 16, 17 and 18 (matrix in CI nightly) |
| X-ALL-STO-C-002 | Every `IVersioned` entity of every module raises `storage.concurrency.conflict` on stale update (reflection-driven generic test) |
| X-ALL-STO-C-003 | No module table lives outside its own schema (`information_schema` check) |
| X-SEC-STO-C-001 | Vault rows contain no plaintext (dump scan) while REG/IRT/IDN store secret ids only |
| X-STA-STO-C-001 | STA snapshot batches respect the 5,000-item limit and single checkpoint rule with the real state store |

### 2.3 REG ↔ MOD

| Id | Test |
|---|---|
| X-REG-MOD-C-001 | Device with a standard type is accepted; same with a package type registered through IRT |
| X-REG-MOD-C-002 | Attribute validation errors from MOD surface with REG paths (`/changes/0/device/attributes/...`) |
| X-REG-MOD-C-003 | Type minor upgrade by reconcile accepted; device attributes revalidated |
| X-REG-MOD-C-004 | Devices referencing a removed package version keep resolving their type (MOD persisted copy) |

### 2.4 REG → PIP (configuration records)

| Id | Test |
|---|---|
| X-REG-PIP-C-001 | Every applied batch produces exactly one `registry.changed` record on `WP_CFG` with matching revision |
| X-REG-PIP-C-002 | Record order equals revision order under concurrent writes |

### 2.5 PIP ↔ MOD, REG (ingest validation and enrichment)

| Id | Test |
|---|---|
| X-PIP-MOD-C-001 | Every standard capability property accepts the values the simulator produces (round-trip of simulator outputs through real MOD validation) |
| X-PIP-MOD-C-002 | Unit conversion path (°F reported for a `Cel` analog) stores the converted value |
| X-PIP-REG-C-001 | Observation for a device disabled in REG is dropped; re-enabled → accepted |
| X-PIP-REG-C-002 | Device moved to another server/connection: observations from the old connection rejected as foreign |
| X-PIP-REG-C-003 | Enrichment after a location rename reflects the new path on the next record |

### 2.6 PIP ↔ STA (subscriber and recovery)

| Id | Test |
|---|---|
| X-PIP-STA-C-001 | Records applied in partition order; STA sequence equals last applied record sequence |
| X-PIP-STA-C-002 | Recovery: STA snapshot at sequence S + log replay → identical state to an uninterrupted run (randomised 10,000-observation run, compared field by field) |
| X-PIP-STA-C-003 | PIP last-value cache filled from STA after recovery: first unchanged observation after restart produces no record |
| X-PIP-STA-C-004 | Posted work (server health change) reaches devices in other partitions; aggregates consistent |
| X-PIP-STA-C-005 | Staleness through `OnTick` with real timing (scaled `StaleAfter` = 2 s) |

### 2.7 STA ↔ MOD, REG

| Id | Test |
|---|---|
| X-STA-MOD-C-001 | Overall state for each standard type computed with the real evaluator for representative value sets (table-driven, shared fixture with MOD tests) |
| X-STA-REG-C-001 | Device created/deleted/moved/re-tagged in REG → STA initialisation and aggregate moves within 1 s |
| X-STA-REG-C-002 | 10,000-device bulk import → STA initialises all, aggregates correct |

### 2.8 IRT ↔ SEC, MOD, REG

| Id | Test |
|---|---|
| X-IRT-SEC-C-001 | Connection secrets stored with owner (Integrations, connectionId); another connection's context cannot read them |
| X-IRT-MOD-C-001 | Package activation registers types; package with invalid types stays Invalid and registers nothing |
| X-IRT-MOD-C-002 | Two versions of a package register two type versions; devices pinned correctly |
| X-IRT-REG-C-001 | Describe → reconcile → devices created with origin `integration:<id>`; second describe idempotent |
| X-IRT-REG-C-002 | Device map changes (import, disable) reach the running connector without restart |

### 2.9 IRT → PIP (sink durability)

| Id | Test |
|---|---|
| X-IRT-PIP-C-001 | `FlushAsync` completes only when records are in JetStream (verified by reading the stream) |
| X-IRT-PIP-C-002 | Unknown native ids counted, not rejected |
| X-IRT-PIP-C-003 | Back-pressure from a stalled partition propagates to `FlushAsync` without blocking `Report*` |
| X-IRT-PIP-C-004 | Kill the node after a connector persisted its cursor → after restart no event between cursor and crash is missing in the log |

### 2.10 C-SIM ↔ IRT (through the integration contract)

| Id | Test |
|---|---|
| X-SIM-IRT-C-001 | Embedded simulator connection reaches `Connected`, inventory reconciled, states flowing |
| X-SIM-IRT-C-002 | Remote simulator: drop/resume → states stale then fresh; catch-up exactly once |
| X-SIM-IRT-C-003 | Command through `IConnectionCommandChannel` with simulator failure modes → mapped results |
| X-SIM-IRT-C-004 | Conformance suite (INT §12) green — added in M4 (T4.03) |

### 2.11 API ↔ IDN, MOD, REG, STA, IRT

| Id | Test |
|---|---|
| X-API-IDN-C-001 | Tokens issued by IDN accepted by API; revoked session rejected within 1 s across REST and hub |
| X-API-IDN-C-002 | Client kind from IDN token drives `ConfigurationEndpoint` refusal for web/mobile |
| X-API-REG-C-001 | Every REG error code returned through the API maps to the documented HTTP status and has translations |
| X-API-STA-C-001 | Live `devices` topic: snapshot equals `GET /states/batch` for the same ids at the same sequence |
| X-API-STA-C-002 | Resume after reconnect uses STA ring; outside ring → snapshot |
| X-API-MOD-C-001 | Types bundle and symbols served for every standard type and state |
| X-API-IRT-C-001 | Connection create/test/start/stop through API drives IRT states visible on the `connections` topic |

### 2.12 UI ↔ API (TypeScript contract tests, `tests/ui/contract`)

Run the generated client and live client against a real node (compose stack).

| Id | Test |
|---|---|
| X-UIC-API-C-001 | Generated client compiles against the committed OpenAPI document and calls every M1 GET endpoint successfully with seed data |
| X-UIC-API-C-002 | Problem details parsed into `ApiError` for representative errors (404, 409, 412, 422 with field errors) |
| X-UIC-API-C-003 | Auth provider: sign-in, refresh rotation, sign-out against real IDN |
| X-UIC-API-C-004 | Live client: subscribe devices, receive simulator deltas, resume after socket drop |
| X-UIC-API-C-005 | MessagePack DTO shapes match TypeScript types (decode every topic item type from a recorded stream) |

### 2.13 DSK ↔ API (node UI hosting and desktop specifics)

| Id | Test |
|---|---|
| X-DSK-API-C-001 | `/app/desktop/` served with CSP and cache headers; hashed assets immutable |
| X-DSK-API-C-002 | Shell pinning with the real site CA from SEC: accepts node, rejects another CA |
| X-DSK-API-C-003 | Desktop token (`ck=desktop`) can call configuration endpoints; web token cannot |

## 3. Later milestones (rows to add when their specs are written)

| Milestone | Pairs to cover |
|---|---|
| M2 | CMD↔IRT (dispatch, results), CMD↔STA (verification), ALM↔PIP (follow-up records), ALM↔STA (state rules), ALM↔EXP, ALM↔SCH, ALM↔CMD (actions), HIS↔PIP (durable consumer), EVT↔HIS, API↔ALM/CMD/EVT topics, UIO↔ALM/CMD/EVT |
| M3 | CFG↔REG/IRT/ALM (publish), RGT↔API (filtering everywhere), RGT↔IDN (templates, directory mappings), AUD↔all writers, MNT↔IDN/API, UIF↔CFG |
| M4 | IHO↔IRT (gRPC host protocol), remote host pairing ↔ SEC, each connector ↔ conformance suite |
| M5 | NOT↔ALM, RPT↔HIS, FIL↔API, HIS retention ↔ STO |
| M6 | VID↔IRT (video provider), VID↔go2rtc, SYN↔STA/REG, WEB↔API |
| M7 | LIC↔REG (units), LIC↔IRT (feature keys), LIC↔IDN (seats), LIC↔LPT (activation protocol) |
| M8 | AUT↔CMD/ALM, SCR↔script host gRPC, SCR↔RGT (script identities) |
| M9 | CLU↔PIP/IRT/CMD (leases, fencing), FED link protocol between two clusters |
| M10 | MOB↔API, PSH↔NOT |
| M11 | STO-MS certification suite, DEP update protocol ↔ nodes and clients |
