# End-to-End Scenarios

End-to-end (layer `E`) scenarios exercise a **running system**: node(s) built from source,
real PostgreSQL + TimescaleDB and NATS, simulators, and real clients (desktop shell through
Playwright for Electron, browsers through Playwright, API-level checks through the generated
TypeScript client). Each milestone's exit criteria reference these scenarios.

## 1. Harness (`tests/e2e`, built in T1.25)

```
tests/e2e/
├── compose/e2e.yml              postgres (timescale), nats, wpsim (remote simulator)
├── harness/                     TypeScript: NodeProcess (build + start node with settings), Seed, ApiDriver, Clock helpers
├── fixtures/seeds/*.json        dev-seed documents per scenario (HST §9 format)
├── playwright.config.ts         projects: "desktop" (Electron), "web" (Chromium, Firefox, WebKit from M6), "api"
├── scenarios/M1/*.spec.ts       one file per scenario id
└── reports/                     HTML report, traces, videos on failure (CI artefacts)
```

Rules:

- Each scenario starts from a **fresh database** (template copy) and fresh NATS streams (unique
  prefixes), node in `Development` with the scenario's seed.
- The simulator runs with fixed seeds and scenario timelines, so expected values are deterministic.
- Time-based checks use generous bounds (CI variance) and explicit polling (`expect.poll`) — no
  fixed sleeps.
- Every scenario records the node logs; a scenario fails if the node logged any Error/Critical not
  expected by the scenario (allow-list per scenario).
- Runs: on every pull request touching `src/` (M1 scenarios, Linux, desktop project with Electron on
  Xvfb), nightly for all scenarios and platforms (Windows runner for desktop weekly).

## 2. M1 scenarios — "First device on screen"

### E-001 — First start and sign-in from the desktop app

| Step | Action | Expected |
|---|---|---|
| 1 | Start compose stack and the node with seed `m1-basic.json` (site "E2E Site", admin user, simulator connection: profile office, seed 42, scenario off) | `/health/ready` 200 within 60 s |
| 2 | Launch the desktop shell with an empty profile store | Profiles page shown |
| 3 | Add site `http://localhost:5080` (Development) | Site name "E2E Site" displayed |
| 4 | Sign in as admin / seed password | Explorer route opened; location tree shows HQ → Building A → Floor 0 |
| 5 | Switch language to Arabic | Layout mirrored (navigator on the right), texts in Arabic, no untranslated keys (pseudo-check helper) |
| 6 | Sign out | Sign-in page; refresh token removed from vault |

### E-002 — Simulator devices visible and live

| Step | Action | Expected |
|---|---|---|
| 1 | Seed `m1-sim-scenario.json`: simulator with scenario timeline (door-0001 opens at +10 s, closes at +20 s, is forced at +40 s; `ana-0001` 35.2 at +60 s) | Connection `Connected` in `GET /connections` |
| 2 | Open explorer Floor 0 | 62 devices listed: 20 doors, 20 readers, 2 areas, 10 zones, 4 inputs, 4 outputs, 2 analogs (office profile) |
| 3 | Open door-0001 device page | Position `closed` |
| 4 | Wait for +10 s | Position becomes `open` on the page within 3 s of the simulator change; state badge `normalActive` |
| 5 | Wait for +40 s | Position `forced`, overall state `alarm` with cause shown; Floor 0 tree node shows 1 alarm |
| 6 | Wait for +60 s | `ana-0001` shows 35.2 °C, state `alarm` (above 32) |

### E-003 — Restart and crash persistence

| Step | Action | Expected |
|---|---|---|
| 1 | Seed `m1-sim-scenario.json`, run until +45 s (door forced) | Device page shows forced |
| 2 | Stop the node gracefully (SIGTERM) | UI shows connection banner within 5 s |
| 3 | Start the node | UI reconnects automatically within 30 s without user action; door-0001 still `forced` (or later scenario value), no duplicate events in `GET /events` (M2: checked when EVT exists; M1: log stream inspected via harness) |
| 4 | Kill the node (SIGKILL) during simulator stress activity | — |
| 5 | Start the node | States equal to the last acknowledged observations (harness compares `GET /states/batch` with simulator ground truth for devices whose last change is older than 2 s before the kill) |

### E-004 — Connection loss and recovery

| Step | Action | Expected |
|---|---|---|
| 1 | Seed `m1-remote-sim.json`: simulator in **remote** mode against `wpsim` | Connected |
| 2 | `POST wpsim /control/drop` and keep the simulator refusing connections (`/control/pause`) | Within 10 s: connection state `Reconnecting`; devices shown with stale quality (grey, clock icon) in the UI |
| 3 | Inject 5 events in `wpsim` while disconnected | — |
| 4 | `/control/resume` | Connection `Connected` within 70 s (back-off); devices fresh; the 5 events present exactly once in the log (harness reads `WP_LOG`) |
| 5 | Stop the connection from the API | Devices stale; connection `Configured`/stopped on the `connections` topic |

## 3. Later milestones (to be detailed with their specs)

| Id | Milestone | Scenario |
|---|---|---|
| E-010 | M2 | Forced door → High alarm with sound and pop-up → acknowledge → door closes → resolved → close with report; timeline complete |
| E-011 | M2 | Open door command: sending → succeeded → verified; command journal entry |
| E-012 | M2 | Command refused by the simulator → readable translated reason; not verified case |
| E-013 | M2 | Arm area refused because of open zones → zones listed; bypass zone → arm succeeds |
| E-014 | M2 | Alarm with start delay and escalation after 2 min unacknowledged |
| E-015 | M2 | Restart node with active alarms and pending commands → alarms intact, commands resolved or failed clearly |
| E-016 | M2 | Event journal: live, history query, export CSV |
| E-020 | M3 | Template user → copy rights to a group → operator sees only Building A, cannot command Building B |
| E-021 | M3 | Rights reduced while operator connected → screens and live data re-filtered immediately |
| E-022 | M3 | Configurator edits rules in a draft, validation errors in plain language, publish, rollback |
| E-023 | M3 | Two configurators edit the same device → merge view |
| E-024 | M3 | Connection wizard: test, discover, import selected devices with locations |
| E-025 | M3 | Maintenance mode (site): operators locked out with message, configurator continues, alarms still recorded, auto-end |
| E-026 | M3 | LDAP sign-in with group mapping to a template (OpenLDAP container) |
| E-027 | M3 | MFA TOTP enrolment and sign-in; critical command requiring step-up |
| E-028 | M3 | Web/mobile token cannot call configuration endpoints (security scenario) |
| E-030…E-034 | M4 | Ping, Modbus simulator, MQTT broker supervision with thresholds; isolated host crash and auto-restart; remote host pairing |
| E-040…E-044 | M5 | Alarm e-mail notification; SMS via fake provider; scheduled PDF shift report; retention purge; privacy pseudonymisation |
| E-050…E-056 | M6 | Plan import and symbol placement; alarm focus on plan; RTSP camera live in alarm details (desktop and web); PTZ; web client operation-only |
| E-060…E-064 | M7 | Offline activation round-trip with the portal; quota enforcement; restricted mode keeps alarms working |
| E-070…E-075 | M8 | Fire evacuation automation; script reacting to events with dry-run test; script limits breach isolated |
| E-080…E-088 | M9 | Failover of a redundant pair under load (< 30 s, no lost events); split-brain prevention; rolling upgrade; federation link with WAN cut and resume; handover schedule |
| E-090…E-093 | M10 | Mobile push and acknowledgement; on-site in-app alerts without internet |
| E-100…E-105 | M11 | Fresh install on Windows and Linux with setup wizard; SQL Server provider end-to-end; store migration; signed update and rollback |
