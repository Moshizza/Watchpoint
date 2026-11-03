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

## 3. M2 scenarios — "Act and react"

All use seed `m2-site.json`: simulator (office, seed 42) with a scenario timeline, default alarm
rule templates installed, admin and an operator user, desktop client.

### E-010 — Forced door alarm, full lifecycle

| Step | Action | Expected |
|---|---|---|
| 1 | Timeline forces door-0001 at +20 s, closes it at +60 s | At +20 s: alarm `std.ac.door.forced` (High) appears in the console within 2 s, High sound plays, details pop up (desktop) |
| 2 | Operator acknowledges | State Acknowledged, owner = operator, sound stops |
| 3 | +60 s door closes | Alarm Resolved then auto-closed (report not required) |
| 4 | Open alarm history (device page → Alarms) | Timeline: raised, acknowledged (by, workstation), resolved, closed; times consistent |

### E-011 — Open door command with verification

| Step | Action | Expected |
|---|---|---|
| 1 | Operator opens door-0002 page → "Open" (sensitive → confirm) | Inline status Sending → Queued → Sent → Succeeded → Verified within 5 s |
| 2 | Command journal | Entry with issuer, duration, verified |
| 3 | `GET /history/commands?device=…` (after HIS lag) | Same command with timestamps |

### E-012 — Refused and unverified commands

| Step | Action | Expected |
|---|---|---|
| 1 | Simulator diagnostics `setFailures` refuse `openable.open` | "Open" → Failed with translated message "The access control system refused the command." (en/fr/ar checked) |
| 2 | `setDoorMode lockedDown` then "Open" | Failed with "Door is locked down" (simulator key) |
| 3 | Failure mode: success without effect (`setFailures` with mode `noEffect`, simulator test hook) | Succeeded then Unverified; alarm `std.command.unverified` raised |

### E-013 — Arm area refused with open zones

| Step | Action | Expected |
|---|---|---|
| 1 | Activate zone-001-01 (diagnostics `setProperty`) and arm area-001 | Failed InvalidState listing "Zone 001-01" |
| 2 | Bypass zone-001-01, arm again | Area Arming → Exit delay → Full; armed event in journal |
| 3 | Activate zone-001-02 while armed | Critical alarm `std.intrusion.zone.alarm` with looping sound |

### E-014 — Delayed alarm and escalation

| Step | Action | Expected |
|---|---|---|
| 1 | Admin creates rule "Analog high" (OnState `device.measurement.value > 30`, StartDelay PT10S, escalation NotAcknowledged after PT30S → priority 50) via API | Rule validates |
| 2 | Timeline sets ana-0001 to 35 for 5 s then back to 21 | No alarm (delay not reached) |
| 3 | Set 35 for 15 s | Alarm raised after 10 s; after 30 s unacknowledged → priority Critical, escalation in timeline, louder sound flag |

### E-015 — Restart with active alarms and pending commands

| Step | Action | Expected |
|---|---|---|
| 1 | Raise two alarms (one acknowledged), queue a command on a disconnected remote-simulator door (fail-fast disabled for this test) | — |
| 2 | Kill the node, restart | Alarms identical (states, counts, owners); queued command still queued then dispatched when connected or expired at expiry; no duplicate alarm actions |

### E-016 — Event journal

| Step | Action | Expected |
|---|---|---|
| 1 | Run simulator busy activity 2 minutes | Live journal shows events newest first; pause buffers and counts |
| 2 | History mode last 10 minutes with filter `accessEvents.denied` | Only denied events; paging through > 200 events |
| 3 | Export CSV | File downloaded with same rows; holder names masked for the operator without personal-data permission (M3: permission; M2: masked for non-admins) |

## 4. M3 scenarios — "Configure safely, many users"

All use seed `m3-site.json`: simulator (campus, seed 7) with locations Building A, Building B,
Building C; default alarm rule templates; users `admin` (administrator), `config1` and `config2`
(configuration permissions incl. `config.rights.edit`, `config.maintenance`), template user
`tpl-operator-a` (operation grants scoped to Building A, `commands.openable.*` with
`RequireReason`), user group `Night shift` with operators `op1`…`op3` (no grants), operator
`op4` (grants on whole site). OpenLDAP container for E-026 (`tests/e2e/fixtures/ldap/seed.ldif`).

### E-020 — Template rights copied to a group

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` opens Rights copy, source `tpl-operator-a`, target group `Night shift`, Replace, no translation | Preview: 3 targets, each +N grants, warning-free; `config1` itself not in targets |
| 2 | Apply with reason | Operation in history; users grid shows drift badge "in sync" for op1…op3 |
| 3 | `op1` signs in on desktop operation side | Navigator shows only Building A; device search for a Building B door returns nothing |
| 4 | `op1` opens a Building A door with reason | Command succeeds (reason dialog required) |
| 5 | `op1` calls `POST /commands` on a Building B door through the API | `403 common.forbidden` (device outside scope) |
| 6 | Building B door forced (timeline) | No alarm shown to `op1`; `op4` sees it |
| 7 | `config1` undoes the operation | op1…op3 have no grants; `op1` session re-filtered: navigator empty |

### E-021 — Rights reduced while connected

| Step | Action | Expected |
|---|---|---|
| 1 | `op4` signed in, device list of Building B open with live values, alarm console open | Values update |
| 2 | `config1` quick-edits `op4` grants: scope Building A only | Within 2 s `op4`'s Building B list empties (live resync), Building B alarms disappear from console, open Building B device tab shows "no longer available" |
| 3 | `op4` REST call for a Building B device | `403` |
| 4 | `config1` restores grant | Building B reappears without sign-in |

### E-022 — Draft, plain-language validation, publish, rollback

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` creates draft "Night rules", edits forced-door rule priority to Critical and adds condition `prop("door.statee") == "open"` | Problem shown in plain language at the field ("Property 'door.statee' does not exist. Did you mean 'door.state'?") with Fix button |
| 2 | Click Fix | Expression corrected; problems 0 |
| 3 | Delete location Building C in the draft | Error "Building C still contains 24 devices" with fix "move contents to parent" |
| 4 | Revert that entry, Publish with comment | Version v(n+1); rule effective: next forced door alarm is Critical |
| 5 | Versions page → roll back v(n+1) → publish | Rule priority back to High; both versions in audit viewer with diffs |

### E-023 — Soft lock, concurrent drafts and merge

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` opens device door-0005 in draft D1 | Soft lock acquired |
| 2 | `config2` opens door-0005 in draft D2 | Editor read-only: "being edited by config1 since …"; no take-over button (no `config.locks.break`) |
| 3 | `config1` changes tags, saves into D1, closes the editor | Lock released; `config2`'s editor becomes editable after refresh prompt |
| 4 | `config2` changes the name, saves into D2 | Entry with base version = live version |
| 5 | `config1` publishes D1, then `config2` publishes D2 | D2 auto-merged (different fields); both changes live |
| 6 | Both change the location in new drafts (sequentially, lock released between); `config1` publishes first, then `config2` | `config2` gets a conflict; ConflictResolver shows base/live/mine; picks mine; publish succeeds |

### E-024 — Connection wizard with discovery and import

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` adds connection: type Simulator, settings (scenario campus-2, seed 9), Test | Test passes with steps listed |
| 2 | Placement: default location Building C, auto-import off; discovery | Job progress then review tree with ~120 devices |
| 3 | Select only subtree "Floor 1", set tag `new`, import | Devices created in the draft with Building C and tag `new`; publish |
| 4 | Operation side (admin) | New devices live with states; others not imported |
| 5 | Stop connection | Imported devices offline; connection status Stopped in details |

### E-025 — Site maintenance mode

| Step | Action | Expected |
|---|---|---|
| 1 | `op4` on desktop operation and on web; `config1` opens Maintenance dialog (site, reason, end in 10 min, auto-end on) | Impact lists op4 sessions (2) to close, op4 locked out, config1/config2 keep access |
| 2 | Confirm and start | `op4` desktop shows full-page message with initiator, reason, end; web session closed; `config1` sees banner |
| 3 | `op4` tries to sign in on web | Refused with the same message |
| 4 | Door forced during maintenance | Alarm recorded (visible later in history), notification mode Continue |
| 5 | Fake clock +10 min | Reminder at −5 min to `config1`; auto-end; `op4` desktop returns to sign-in automatically and signs in |
| 6 | Audit viewer | `maintenance.started`, `maintenance.autoEnded` entries |

### E-026 — LDAP sign-in with group mapping

| Step | Action | Expected |
|---|---|---|
| 1 | `admin` configures LDAP provider (OpenLDAP container) with mapping group `cn=guards` → template `tpl-operator-a`, user group `Night shift`; test connection | Test OK |
| 2 | LDAP user `guard7` (member of guards) signs in | JIT user created, source Directory, member of Night shift, grants copied from template (source DirectoryMapping), sees Building A only |
| 3 | Remove `guard7` from `cn=guards` in LDAP; sign in again | Mapped grants removed; operation side shows no devices |
| 4 | LDAP container stopped; `guard7` sign-in | Refused (`identity.directory.unavailable`); local `admin` still signs in |

### E-027 — MFA and step-up for a critical command

| Step | Action | Expected |
|---|---|---|
| 1 | `op4` enrols TOTP (test reads the secret from the QR payload) | MFA active; recovery codes shown once |
| 2 | Sign out/in | TOTP step required; wrong code refused; correct code accepted |
| 3 | `op4` grant for `commands.armable.disarm` has `RequireStepUp`; disarms Area 1 | Step-up dialog; TOTP accepted; command succeeds; audit shows step-up |
| 4 | Disarm again within 5 min | No new prompt (step-up proof valid) |

### E-028 — Configuration endpoints refuse non-desktop clients (security)

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` signs in on the web client | Operation only; no side switch; bundle has no configuration module |
| 2 | With the web access token call `PUT /api/v1/drafts/{id}/entries`, `POST /api/v1/rights-copy/apply`, `POST /api/v1/maintenance` | `403 api.client_kind.not_allowed` for each; audit records `Denied` |
| 3 | Same with a mobile token | Same results |
| 4 | Desktop token of `op4` (no config permissions) | `403 common.forbidden` |

## 5. Later milestones (to be detailed with their specs)

| Id | Milestone | Scenario |
|---|---|---|
| E-030…E-034 | M4 | Ping, Modbus simulator, MQTT broker supervision with thresholds; isolated host crash and auto-restart; remote host pairing |
| E-040…E-044 | M5 | Alarm e-mail notification; SMS via fake provider; scheduled PDF shift report; retention purge; privacy pseudonymisation |
| E-050…E-056 | M6 | Plan import and symbol placement; alarm focus on plan; RTSP camera live in alarm details (desktop and web); PTZ; web client operation-only |
| E-060…E-064 | M7 | Offline activation round-trip with the portal; quota enforcement; restricted mode keeps alarms working |
| E-070…E-075 | M8 | Fire evacuation automation; script reacting to events with dry-run test; script limits breach isolated |
| E-080…E-088 | M9 | Failover of a redundant pair under load (< 30 s, no lost events); split-brain prevention; rolling upgrade; federation link with WAN cut and resume; handover schedule |
| E-090…E-093 | M10 | Mobile push and acknowledgement; on-site in-app alerts without internet |
| E-100…E-105 | M11 | Fresh install on Windows and Linux with setup wizard; SQL Server provider end-to-end; store migration; signed update and rollback |
