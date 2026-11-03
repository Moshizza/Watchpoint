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

## 3. Contract tests (M2)

| Id | Test |
|---|---|
| X-CMD-IRT-C-001 | Command dispatch through the real IRT channel to the simulator: result categories mapped for every simulator failure mode |
| X-CMD-STA-C-001 | Verification uses real STA records: verified when the simulator reports the effect; unverified at deadline |
| X-CMD-MOD-C-001 | Every standard command definition's arguments schema accepted by MOD validation with defaults |
| X-CMD-PIP-C-001 | Command records ordered with the device's observations in its partition |
| X-ALM-PIP-C-001 | Alarm follow-up records appended before the next input record of the partition |
| X-ALM-STA-C-001 | State rules evaluated on real STA values for each standard type template |
| X-ALM-EXP-C-001 | Every standard rule template compiles with the real expression compiler against the standard library |
| X-ALM-SCH-C-001 | Rule with ActiveCalendar switches at calendar edges with real SCH triggers |
| X-ALM-CMD-C-001 | Alarm action Command creates a CMD command with AlarmAction issuer and correlation |
| X-EVT-HIS-C-001 | Events written by HIS are returned by EVT journal queries with identical DTOs as the live topic |
| X-HIS-PIP-C-001 | HIS durable consumers resume after node restart without loss or duplicates |
| X-API-ALM-C-001 | Live `alarms` topic snapshot equals `GET /alarms` for the same filter |
| X-API-CMD-C-001 | Live `commands` topic delivers every transition of a REST-requested command in order |
| X-UIO-ALM-C-001 | UI alarm console (real API, FakeLiveHub disabled) shows a simulator alarm and acknowledges it (Playwright component test against compose stack) |

## 4. Contract tests (M3)

| Id | Test |
|---|---|
| X-CFG-REG-C-001 | Publish of devices/locations/tags drafts through the real REG object types: one transaction, `config.published` on WP_CFG, REG caches reloaded |
| X-CFG-IRT-C-001 | Publishing a connection settings change restarts only that connection with the new settings; draft secrets moved to the connection's secret |
| X-CFG-ALM-C-001 | Publishing alarm rules swaps the rule set atomically; alarms of removed rules follow the configured policy |
| X-CFG-RGT-C-001 | `rights.grants` publish triggers `rights.changed` and evaluator cache invalidation on every node |
| X-CFG-AUD-C-001 | Every publish/rollback produces an audit record with version, diff and actor |
| X-RGT-API-C-001 | For every list/read endpoint and live topic registered by M1–M3 modules, a user scoped to Building A never receives Building B items (generated test over the endpoint catalogue) |
| X-RGT-CMD-C-001 | Command authorizer constraints from real grants: reason, four-eyes, step-up, client kinds, outside schedule |
| X-RGT-ALM-C-001 | Alarm visibility with priority ranges and scope with real grants |
| X-RGT-IDN-C-001 | Directory mapping applies template grants through the copy engine (Merge, DirectoryMapping source) and removes them on group loss |
| X-RGT-REG-C-001 | Location move and tag changes update visible scopes and emit `rights.changed` for affected users only |
| X-AUD-ALL-C-001 | Each action in the AUD catalogue v1 is produced by its module in at least one test (catalogue coverage) and chain verifies |
| X-MNT-IDN-C-001 | Site maintenance revokes operation sessions and refuses operation sign-in; configuration sessions untouched |
| X-MNT-API-C-001 | Live connections receive `Closing("maintenance")`; `system` topic and `/system/status` reflect start/end |
| X-MNT-RGT-C-001 | Location maintenance post-filter removes suspended subtrees from visible scopes and restores them on end |
| X-UIF-CFG-C-001 | UI draft flow (create draft, edit device, diff, publish) against the compose stack (Playwright component test) |
| X-UIF-RGT-C-001 | UI rights-copy wizard preview equals API preview for the same request |

## 5. Contract tests (M4)

| Id | Test |
|---|---|
| X-IHO-IRT-C-001 | Every `IConnectionHost` implementation (in-process, isolated, remote over in-memory transport) passes the same host contract test set: start/stop/describe/test/execute/refresh/diagnostic, faults, features |
| X-IHO-IRT-C-002 | Conformance suite INT-C-001…040 for C-SIM passes identically in the three host modes |
| X-IHO-PIP-C-001 | Observations acked to the host only after PIP durability; host-side FlushAsync semantics preserved (S4) |
| X-IHO-SEC-C-001 | Pairing issues a site-CA client certificate; revoked host refused at handshake |
| X-IHO-CMD-C-001 | Command lost reply on link drop → CMD `Unverified`/failed per rules; never resent |
| X-IHO-ALM-C-001 | Replayed events beyond cutoff do not raise event alarms; state alarms follow current state |
| X-CONN-INT-C-001 | C-PING, C-MODBUS, C-MQTT, C-SNMP each pass the conformance suite (applicable cases) in-process and isolated |
| X-CONN-MOD-C-001 | Package types, capabilities and rule templates of the four connectors load in MOD and validate against the standard library (`knownTypes.json` generated from the same build) |
| X-SNMP-IRT-C-001 | Shared UDP trap port fan-out across two SNMP connections in-process; separate sockets per isolated host |

## 6. Contract tests (M5)

| Id | Test |
|---|---|
| X-NOT-ALM-C-001 | Notify action and escalation steps produce notification requests; delivery status records land in the alarm timeline (HIS) in order |
| X-NOT-RGT-C-001 | Users never receive alarm notifications for alarms outside their scope or priority range (generated over rules × users fixture) |
| X-NOT-MNT-C-001 | Maintenance notification modes Suppress/CriticalOnly applied by the router |
| X-NOT-SCH-C-001 | Rosters and quiet hours with real SCH calendars across DST |
| X-RPT-HIS-C-001 | Every data source query runs against the real HIS schema (PostgreSQL + TimescaleDB and plain-PostgreSQL fallback) with identical results |
| X-RPT-RGT-C-001 | Report data sources filter by the identity's visible scope |
| X-RPT-FIL-C-001 | Report outputs stored and downloadable with category rules |
| X-RPT-NOT-C-001 | Scheduled delivery with attachment/link rules |
| X-FIL-API-C-001 | Upload/download/signed URL flows through the real API pipeline (auth, permission policies, range requests) |
| X-FIL-CFG-C-001 | File references follow CFG publish/rollback |
| X-HIS-STO-C-001 | Retention, compression and aggregates jobs on PostgreSQL with and without TimescaleDB |

## 7. Contract tests (M6)

| Id | Test |
|---|---|
| X-VID-IRT-C-001 | `IVideoProvider` calls through in-process and isolated hosts give identical results (C-RTSP with test device) |
| X-VID-GW-C-001 | Stream registration, ticketed proxy and WebRTC signalling against the real go2rtc binary of the release (pinned version) |
| X-VID-RGT-C-001 | Live/playback/PTZ/export permissions and lookback constraints with real grants |
| X-VID-ALM-C-001 | ALM Snapshot/Bookmark actions produce attachments and timeline entries |
| X-SYN-CFG-C-001 | Synoptic publish/rollback with file references through real CFG and FIL |
| X-SYN-REG-C-001 | Binding validation reacts to registry deletions (warnings) and moves |
| X-SYN-STA-C-001 | Viewer viewport subscriptions receive the same states as the device list for the same devices |
| X-SYN-TS-CS-C-001 | Document migrations C# and TS produce identical outputs on shared fixtures |
| X-WEB-API-C-001 | Web cookie authentication, CSRF, allowed networks and client-kind refusals across all configuration endpoints (generated over the endpoint catalogue) |

## 8. Contract tests (M7)

| Id | Test |
|---|---|
| X-LIC-LPT-C-001 | Licence and request documents round-trip between portal and product through the shared `Licensing.Format` (golden documents committed in `tests/vectors/licensing/`) |
| X-LIC-LPT-C-002 | Site-facing portal API (requests, status poll, check-in) contract: product client vs portal server in compose |
| X-LIC-REG-C-001 | Device units computed from the real registry and type licence classes (enabled/orphaned rules) |
| X-LIC-CFG-C-001 | Publish quota gate on real drafts (create, enable, delete devices) |
| X-LIC-IDN-C-001 | Seat admission with real sessions per client kind; never closes existing sessions |
| X-LIC-IRT-C-001 | Feature gate on connection creation for packages by licence class |
| X-LIC-HIS-C-001 | Restricted mode history window across every HIS query endpoint |

## 9. Contract tests (M8)

| Id | Test |
|---|---|
| X-AUT-PIP-C-001 | Run records in the automation partition rebuild identical run state after replay (snapshot + tail) |
| X-AUT-CMD-C-001 | Automation commands carry issuer Automation and ClientCommandId; resume queries outcomes without duplicates |
| X-AUT-ALM-C-001 | ALM Automation action starts runs; automation-raised alarms idempotent by key |
| X-AUT-RGT-C-001 | Identity rights validation (publish) and enforcement (run) agree for every block kind |
| X-AUT-SCH-C-001 | Cron, calendar edge and sun time triggers with real SCH |
| X-SCR-HOST-C-001 | Script host gRPC protocol: every message type round-trips; version mismatch refused |
| X-SCR-GW-C-001 | Every script API method declared in `watchpoint.d.ts` has a gateway handler and schema (generated check) |
| X-SCR-RGT-C-001 | Script API calls denied outside identity scope with `scr.api.forbidden` |
| X-SCR-VIRT-C-001 | Virtual devices flow through IRT/REG/PIP like real devices (states, alarms, history) |
| X-SCR-TS-C-001 | Server compiler and IDE TypeScript versions identical; same diagnostics on a shared corpus |

## 10. Contract tests (M9)

| Id | Test |
|---|---|
| X-CLU-PIP-C-001 | Partition ownership gain/loss with real PIP workers: no record processed twice, no gap (sequence audit over a run with forced moves) |
| X-CLU-IRT-C-001 | Connection ownership moves with cursors in the replicated connection store; catch-up after move |
| X-CLU-CMD-C-001 | Stale fencing token dispatch rejected by IRT channel; pending commands reconciled on new owner |
| X-CLU-API-C-001 | Tokens and live stream resume across nodes |
| X-CLU-FIL-C-001 | File replication through Object Store visible on all nodes |
| X-CLU-LIC-C-001 | Node footprints licensing: unlicensed node Standby, then refused after grace |
| X-HLT-ALL-C-001 | Every module exposes its health items/metrics expected by HLT collectors (catalogue test) |
| X-FED-PIP-C-001 | Outbound durable consumer acks only after peer ack; resume exactness over 1,000 random cuts (property test) |
| X-FED-RGT-C-001 | Site scopes on central and federation identity on child agree with RGT evaluator for all request kinds |
| X-FED-ALM-C-001 | Ownership changes recorded in child alarm state and mirrored at central |

## 11. Later milestones (rows to add when their specs are written)

| Milestone | Pairs to cover |
|---|---|
| M10 | MOB↔API, PSH↔NOT |
| M11 | STO-MS certification suite, DEP update protocol ↔ nodes and clients |
