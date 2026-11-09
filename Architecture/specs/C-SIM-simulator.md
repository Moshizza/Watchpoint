# C-SIM — Simulator Connector

| | |
|---|---|
| Spec id | C-SIM |
| Projects | `src/integrations/Watchpoint.Integration.Simulator` (package), `tools/wpsim` (standalone simulator server for the remote mode) |
| Package id | `watchpoint.integration.simulator` (builtin, licence class `included`) |
| Milestone | M1 |
| Tasks | T1.17 |
| Event id range | 5000–5099 |

## 1. Purpose and scope

A connector that simulates a realistic site (doors with readers, intrusion areas and zones,
inputs, outputs, analog sensors) so that Watchpoint can be developed, tested, demonstrated and
load-tested **without real equipment**. It exercises every feature of the contract: discovery,
live observation, refresh, commands with configurable outcomes, event catch-up, diagnostics.

Two modes:

| Mode | Description | Use |
|---|---|---|
| `embedded` (default) | The simulated site runs inside the connector | Demos, UI work, e2e tests, load tests |
| `remote` | The connector talks to `wpsim` (separate process) over WebSocket | Tests of network behaviour: disconnections, latency, reconnection, catch-up; conformance suite |

## 2. Dependencies

Abstractions, SDK (`ConnectorBase`, polling, gate). `wpsim`: ASP.NET Core minimal app (same
simulation engine library `Watchpoint.Integration.Simulator.Engine` shared by both).

## 3. Project layout

```
Watchpoint.Integration.Simulator/
├── manifest.json, i18n/{en,fr,ar}.json, types/sim.server.json, settings/simulator.settings.json, docs/en.md
├── SimulatorConnector.cs          ConnectorBase; IDiscovery, IStateRefresh, ICommandExecutor, IBatchCommandExecutor, IEventCatchUp, IDiagnostics
├── Embedded/EmbeddedTransport.cs
├── Remote/RemoteTransport.cs      WebSocket client (System.Net.WebSockets)
└── Mapping/SimMapping.cs
Watchpoint.Integration.Simulator.Engine/   (no Watchpoint references except Kernel-free POCOs; also used by wpsim)
├── SiteGenerator.cs               deterministic from seed and profile
├── SimulatedSite.cs               devices, states, behaviour, event log with sequence
├── Behaviours/DoorBehaviour.cs, IntrusionBehaviour.cs, IoBehaviour.cs, AnalogBehaviour.cs, HealthBehaviour.cs
├── Scenario/ScenarioPlayer.cs
└── FailureInjector.cs
tools/wpsim/
└── Program.cs                     WebSocket endpoint /sim, control REST endpoints /control/*
```

## 4. Settings (`settings/simulator.settings.json`)

| Field | Type | Default | Rules |
|---|---|---|---|
| `mode` | enum `embedded`, `remote` | `embedded` | |
| `url` | string (uri) | `ws://localhost:5090/sim` | visible when mode = remote |
| `profile` | enum `office`, `bank`, `campus`, `custom` | `office` | profiles set counts and behaviour rates |
| `doors` | integer 0–20,000 | 20 (office) | custom only, else from profile |
| `readersPerDoor` | integer 0–2 | 1 | |
| `areas` | integer 0–1,000 | 2 | |
| `zonesPerArea` | integer 0–200 | 5 | |
| `inputs`, `outputs`, `analogs` | integer 0–20,000 | 4, 4, 2 | |
| `seed` | integer | 1 | deterministic generation |
| `activity` | enum `calm`, `normal`, `busy`, `stress` | `normal` | rates in §6.2 |
| `commandLatency` | duration PT0S–PT30S | PT0.2S | |
| `failures` | array of `{ command: string, mode: refuse|timeout|error|noEffect, probability: number 0–1 }` | [] | |
| `scenario` | string (JSON timeline, §6.5) | empty | |
| `timeZone` | string IANA | `Europe/Paris` | simulated site's local time (arming schedules) |

Profiles:

| Profile | Doors | Areas × zones | Inputs/outputs/analogs | Notes |
|---|---|---|---|---|
| `office` | 20 | 2 × 5 | 4/4/2 | Busy 08:00–18:00 weekdays, armed at night |
| `bank` | 12 | 4 × 8 | 8/8/4 | Strict arming, more denied events |
| `campus` | 400 | 40 × 10 | 100/100/50 | Large site for load and UI scale |
| `custom` | from settings | | | |

No secrets (secrets schema empty); `remote` mode uses an optional shared token setting
`token` declared as a secret field `token` (kind `token`) to test secret plumbing.

## 5. Generated inventory

- Root server: native id `sim-root`, type `sim.server` (role server, capabilities `health`),
  vendor name `Simulator (<profile>, seed <n>)`.
- Sub-servers: one per 100 doors (`sim-ctrl-01`…) of type `generic.server`, to exercise
  server hierarchies.
- Devices (native ids zero-padded, names localised in English with numbers):

| Device | Native id | Type | Parent | Vendor name |
|---|---|---|---|---|
| Door | `door-0001` | `ac.door` | — | `Door 0001` |
| Reader | `door-0001-r1` | `ac.reader` | door | `Door 0001 reader IN` (r2 `OUT`) |
| Area | `area-001` | `intrusion.area` | — | `Area 001` |
| Zone | `zone-001-01` | `intrusion.zone` | — | `Zone 001-01`; link `memberOf` → area |
| Input | `in-0001` | `generic.binary-input` | — | attributes `trueSeverity: warning` |
| Output | `out-0001` | `generic.binary-output` | — | |
| Analog | `ana-0001` | `generic.analog-input` | — | attributes `unit: Cel`, `warningAbove: 28`, `alarmAbove: 32`, `hysteresis: 0.5` |

Every 10th door has `tamper` unsupported (optional capability) to exercise
`UnsupportedOptionalCapabilities`.

## 6. Behaviour (engine)

### 6.1 Clock

The engine uses the injected `TimeProvider` (connector context `Time`), so tests drive it with
`FakeTimeProvider`. Behaviours are evaluated on a tick every 250 ms (embedded) — scheduled
through the connector scheduler with `RunWhenDisconnected=true` (the site lives on while the
connection is down; remote mode: `wpsim` has its own loop).

### 6.2 Rates by activity (per device, per hour, during "busy hours" of the profile; ÷10 outside)

| Activity | Door passages | Denied access | Held open | Forced | Zone activations (disarmed) | Analog step | Device offline |
|---|---|---|---|---|---|---|---|
| calm | 2 | 0.1 | 0.02 | 0.002 | 1 | every 60 s | 0.001 |
| normal | 10 | 0.5 | 0.1 | 0.01 | 5 | every 10 s | 0.005 |
| busy | 60 | 3 | 0.5 | 0.05 | 30 | every 2 s | 0.02 |
| stress | 3,600 | 100 | 10 | 1 | 600 | every 0.25 s | 0.1 |

Random draws use `System.Random(seed ^ deviceIndex)` per device so runs are reproducible.

### 6.3 Device behaviours

| Device | Behaviour |
|---|---|
| Door | Passage: reader event `accessEvents.granted` (holder from a seeded list of 200 generated names with mixed scripts incl. Arabic and accented Latin, `holderRef` `H-0001`…), position `open`, then `closed` after 3–8 s. Held open: stays open beyond `heldOpenAfter` → position `heldOpen` + event `doorPosition.heldOpen`, closes after 30–120 s. Forced: position `forced` + event `doorPosition.forced` without prior grant, closes after 5–20 s. Denied: event `accessEvents.denied` with a random reason. Lock state `locked` except during passage (`unlocked`), mode `normal` unless commanded |
| Area | Scheduled arming per profile (office: arm 20:00, disarm 07:30 local, weekdays; weekends armed). Arming: `arming` → `exitDelay` (30 s) → `full`. A zone activation while armed: area `alarm`, zone `active` event, area back to `full` after the alarm memory reset command or 10 min |
| Zone | Activations: `active` for 2–10 s then `normal`. Rare `tamper`/`masked` faults (0.1 %/h) for 1–5 min |
| Input | Toggles at the zone activation rate |
| Output | Changes only by command |
| Analog | Random walk around 21 °C (σ 0.2 per step), daily sine ±2 °C, occasional heat spike (+12 °C over 10 min, 0.1 %/h at normal) |
| Health | Random offline periods (1–10 min) per device rate; battery low on 2 % of readers; root server always online in embedded mode |

### 6.4 Commands

| Command | Success behaviour | Refusal rules |
|---|---|---|
| `openable.open` | After `commandLatency`: `opened` event, position `open`, `closed` after `duration` | Device offline → `Failed(DeviceOffline)`; mode `lockedDown` → `Failed(InvalidState, "sim.errors.lockedDown")` |
| `lockable.lock` / `unlock` | lock state changes | offline → DeviceOffline |
| `doorMode.setMode` | mode changes; `emergencyOpen` → lock unlocked, position open | — |
| `armable.arm(mode)` | `arming` → exit delay → `full`/`partial`; event `armed` | Any zone of the area active/tamper/masked and not bypassed → `Failed(InvalidState, "sim.errors.zonesOpen", { zones: [...] })` and event `armFailed` with `openZones` |
| `armable.disarm` | `disarmed`; event `disarmed` | — |
| `armable.cancelArming` | back to `disarmed` if arming/exitDelay | not arming → `Failed(InvalidState)` |
| `bypassable.bypass` / `unbypass` | flag set | — |
| `switchable.switchOn/Off/toggle`, `pulse.pulse` | output state | — |
| `health.refresh` | all properties re-reported | — |

`failures` settings override outcomes with the given probability: `refuse` →
`Failed(RefusedBySystem, "sim.errors.refused")`; `timeout` → never answers (until deadline);
`error` → throws (tests connector fault handling); `noEffect` → returns `Succeeded` but does not change
the device state (tests command verification → `Unverified`). Batch execution supported for `openable.open`,
`lockable.*`, `doorMode.setMode`, `switchable.*`.

### 6.5 Scenario timeline

`scenario` (JSON) plays deterministic steps, optionally looped:

```json
{ "loop": true, "steps": [
  { "at": "PT10S", "device": "door-0001", "set": { "doorPosition.position": "forced" } },
  { "at": "PT15S", "device": "door-0001", "event": "doorPosition.forced" },
  { "at": "PT40S", "device": "door-0001", "set": { "doorPosition.position": "closed" } },
  { "at": "PT60S", "device": "ana-0001", "set": { "measurement.value": 35.2 } }
]}
```

When a scenario is set, random behaviours are disabled unless `"withRandom": true`.

### 6.6 Event log and catch-up

The engine numbers every event (`seq`, ulong). The connector stores `lastSeq` in the
connection store (key `cursor`) **after** `FlushAsync` (INT S4). On reconnect (remote mode) or
restart, `CatchUpAsync` requests events with `seq > lastSeq` (engine keeps the last 100,000
events). Embedded mode: the engine persists nothing; after a node restart the embedded site is
regenerated from the seed with fresh state (documented limitation).

## 7. Remote protocol (`wpsim`)

WebSocket at `/sim`, JSON text messages `{ "t": "<type>", … }`:

| Direction | Type | Content |
|---|---|---|
| C→S | `hello` | `{ "v": 1, "token": "…"? }` |
| S→C | `welcome` | `{ "v": 1, "site": { profile, seed }, "lastSeq": n }` |
| C→S | `inventory` | — |
| S→C | `inventory` | servers and devices (shape of §5) |
| C→S | `snapshot` | `{ "devices": [...]? }` |
| S→C | `snapshot` | states of requested devices |
| S→C | `change` | `{ "seq": n, "device": "door-0001", "props": { "doorPosition.position": "open" } }` |
| S→C | `event` | `{ "seq": n, "device": "…", "event": "accessEvents.granted", "payload": {…}, "at": "…" }` |
| C→S | `catchUp` | `{ "afterSeq": n }` → server replays `event` messages then `catchUpDone` |
| C→S | `command` | `{ "id": "…", "device": "…", "command": "openable.open", "args": {…} }` |
| S→C | `commandResult` | `{ "id": "…", "outcome": "succeeded|failed", "failure": "…"?, "message": "…"?, "args": {…}? }` |
| both | `ping`/`pong` | every 15 s; 3 missed pongs → connection considered lost |

Control REST (for tests and demos, `wpsim` only, bound to localhost): `POST /control/drop`
(close all sockets), `POST /control/pause` / `resume` (stop answering), `POST /control/latency`
`{ "ms": 500 }`, `POST /control/inject` (event or property), `POST /control/failures` (same
format as settings), `POST /control/rename` `{ "device": "door-0001", "name": "Main entrance" }`,
`GET /control/stats` (request counts per type).

## 8. Diagnostics actions (`IDiagnostics`)

| Action id | Arguments | Effect |
|---|---|---|
| `injectEvent` | `{ device, event, payload? }` | Emit event now |
| `setProperty` | `{ device, path, value }` | Set property now |
| `pauseActivity` / `resumeActivity` | — | Stop/restart random behaviours |
| `setFailures` | `{ failures: [...] }` | Replace failure injection at runtime |
| `regenerate` | `{ seed? }` | Regenerate site (inventory changes → `ReportInventoryChanged`) |

Diagnostics items: mode, profile, devices count, events emitted, commands executed per outcome,
current `lastSeq`.

## 9. Translations

`i18n/*.json` under `packages.watchpoint.integration.simulator`: package name, settings field
names and help, profile and activity labels, error messages (`errors.lockedDown`,
`errors.zonesOpen` with ICU plural on `{count}` zones, `errors.refused`), diagnostics action
names — in en, fr, ar.

## 10. Observability

| EventId | Level | Message |
|---|---|---|
| 5001 | Information | Simulator site generated: profile {Profile}, seed {Seed}, {Devices} devices |
| 5002 | Information | Simulator connected to {Url} |
| 5003 | Warning | Simulator connection lost: {Reason} |
| 5010 | Debug | Command {Command} on {Device} → {Outcome} |

Connector metrics: `sim_events_emitted`, `sim_changes_emitted`, `sim_commands` (via
`IConnectorMetrics`).

## 11. Tests

Unit: `tests/integrations/Watchpoint.Integration.Simulator.Tests` (engine with
`FakeTimeProvider`). Integration: same project with `wpsim` hosted in-process
(`WebApplicationFactory`) and the IRT test harness.

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| SIM-U-001 | U | Deterministic generation | Same seed/profile → identical inventory (native ids, names, links) |
| SIM-U-002 | U | Profile counts | office → 20 doors, 20 readers, 2 areas, 10 zones, 4/4/2 IO |
| SIM-U-003 | U | Custom counts and limits | `doors: 20001` → settings validation error |
| SIM-U-004 | U | Sub-servers per 100 doors | campus → 4 controller servers, doors parented accordingly |
| SIM-U-005 | U | Optional capability unsupported every 10th door | |
| SIM-U-006 | U | Inventory valid against standard types | All items validated by MOD fake (strict sink) |
| SIM-U-007 | U | Door passage sequence | Granted event → open → closed within 3–8 s (FakeTimeProvider) |
| SIM-U-008 | U | Held open after `heldOpenAfter` | → `heldOpen` + event |
| SIM-U-009 | U | Forced without grant | → `forced` + event, then closed |
| SIM-U-010 | U | Activity rates within tolerance | busy, 10 simulated hours, 100 doors → passages within ±15 % of expected |
| SIM-U-011 | U | Busy hours vs off hours | Off-hours rate ≈ 1/10 |
| SIM-U-012 | U | Area arming schedule | Weekday 20:00 local → arming → exitDelay 30 s → full |
| SIM-U-013 | U | Zone activation while armed → area alarm | |
| SIM-U-014 | U | Analog thresholds reached by heat spike | Value crosses 28 and 32 within spike |
| SIM-U-015 | U | Holder names include Arabic and accented names | |
| SIM-U-016 | U | Open command success with latency | `commandLatency` PT1S → result after 1 s; opened event; closes after duration |
| SIM-U-017 | U | Open refused when lockedDown | → `Failed(InvalidState)` with message key `sim.errors.lockedDown` |
| SIM-U-018 | U | Open on offline door | → `DeviceOffline` |
| SIM-U-019 | U | Arm refused with open zones | → `InvalidState` with `zones` arg; `armFailed` event with `openZones` |
| SIM-U-020 | U | Arm succeeds when zones bypassed | |
| SIM-U-021 | U | Failure injection refuse/timeout/error | Probability 1 → respective behaviours (timeout returns `Timeout` via deadline in SDK; error throws) |
| SIM-U-022 | U | Batch commands return one result per command | |
| SIM-U-023 | U | Scenario timeline executes in order and loops | |
| SIM-U-024 | U | Scenario disables random activity unless `withRandom` | |
| SIM-U-025 | U | Event sequence numbers strictly increasing | |
| SIM-U-026 | U | Catch-up returns events after cursor once | |
| SIM-U-027 | U | Cursor saved only after flush | Spy sink/store |
| SIM-U-028 | U | Diagnostics actions | injectEvent/setProperty/setFailures/regenerate effects |
| SIM-U-029 | U | Regenerate reports inventory change | `ReportInventoryChanged` with complete snapshot |
| SIM-U-030 | U | Translations present for all keys in en/fr/ar | `wptypes validate` of the package folder with `--strict-i18n` |
| SIM-I-001 | I | Embedded connection end-to-end through IRT | Create connection (office, autoImport) → 52 devices imported (20 doors + 20 readers + 2 areas + 10 zones) + IO; states flowing in STA |
| SIM-I-002 | I | Remote mode against `wpsim` | Connects, inventory, live changes |
| SIM-I-003 | I | Remote disconnect and catch-up | `/control/drop`, inject 5 events while down, reconnect → exactly 5 events catch-up, no duplicates |
| SIM-I-004 | I | Rename in simulator keeps native id | `/control/rename` → registry vendor name updated, same device id |
| SIM-I-005 | I | Stress profile sustains load | campus + stress for 60 s → no rejected observations, pipeline throughput ≥ target (PIP-I-010) |

The conformance suite (INT §12) runs against this connector in remote mode in T4.03.

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.17 | All | SIM-U-001…SIM-U-030, SIM-I-001…SIM-I-005 |
