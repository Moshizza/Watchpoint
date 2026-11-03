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

## 5. M4 scenarios — "Real protocols"

Seed `m4-site.json`: admin, operator; compose stack adds containers: Mosquitto (TLS, user/pass),
`ModbusTestServer` (as a small container image built from the test project), net-snmp `snmpd`
with trap sender, a second "remote" machine container with the integration host installed, and
Toxiproxy between them and the node.

### E-030 — Network checks with Ping connector

| Step | Action | Expected |
|---|---|---|
| 1 | Admin adds Ping connection with 20 targets (CSV import in wizard): 18 reachable containers, 1 unroutable, 1 HTTP endpoint with a certificate expiring in 10 days | Test passes with 1 unreachable warning; import 20 devices |
| 2 | Wait 2 rounds | 18 up, 1 down → `std.net.unreachable` alarm after `alarmAfter`; certificate warning event and alarm |
| 3 | Stop a container that is parent of 2 targets | Parent down alarm; children `unknown` (parentDown), no alarms for them |
| 4 | Restart container | Alarm resolves; children back up |

### E-031 — Modbus I/O and analog thresholds

| Step | Action | Expected |
|---|---|---|
| 1 | Add Modbus connection to the test server with template `generic-io-8di-8do` and `generic-analog-4ai` (unit 1, 2) | 20 devices imported |
| 2 | Test server toggles DI 3; sets AI 1 above `alarmAbove` | Binary input state change within 2 s; analog alarm `std.analog.threshold` raised |
| 3 | Operator switches DO 5 on | Command succeeded and verified (read-back) |
| 4 | Pulse DO 6 for 2 s | Coil on then off; command succeeded after second write |
| 5 | Test server returns exception 02 for AI 4 | AI 4 quality Bad; other points fine; warning in connection details |

### E-032 — MQTT sensors discovered dynamically

| Step | Action | Expected |
|---|---|---|
| 1 | Add MQTT connection with rule `site/{building}/{deviceId}/temperature`, auto-import off | Test shows messages received on the rule |
| 2 | Publisher starts 50 sensors in 2 buildings | Discovery review lists 50 devices with suggested locations; import all |
| 3 | One sensor publishes 45 °C | Alarm raised |
| 4 | Sensor stops publishing for `staleAfter` | Quality Uncertain shown (stale marker) |
| 5 | Broker restarted while a sensor publishes QoS 1 | No message lost after reconnect (persistent session) |

### E-033 — Isolated host crash and recovery

| Step | Action | Expected |
|---|---|---|
| 1 | Set the simulator connection to host mode Isolated (draft, publish) | Connection restarts in an isolated host; Hosts page shows it Ready |
| 2 | Kill the host process (test hook) | Within 5 s the host restarts, connection reconnects; event `hostRestarted` in timeline; no acked events lost (simulator event counter = journal count) |
| 3 | Enable test failure "crash on start" 5 times | Crash loop → host Faulted, connections Faulted, system alarm `integrations.hostCrashLoop` |
| 4 | Disable failure, "restart host" | Recovered |

### E-034 — Remote host pairing, WAN cut and buffering

| Step | Action | Expected |
|---|---|---|
| 1 | Admin creates pairing code; on the remote container run `pair <code> <address>` with CA fingerprint | Host appears Ready in Hosts page |
| 2 | Create SNMP connection on the remote host (agent = `snmpd` reachable only from the remote container) | Package pushed; ports devices imported; traps received from the remote network |
| 3 | Toxiproxy cuts node↔remote link for 10 min; `snmptrap` sends 20 linkDown/linkUp traps | Devices shown with host unavailable (unknown); nothing lost on the remote side |
| 4 | Link restored | 20 events replayed with original times in the journal; no alarms raised for replayed events older than the cutoff; current states correct |
| 5 | Delete remote host while connection assigned | Refused; after reassigning the connection, delete works and host cannot reconnect |

## 6. M5 scenarios — "History, reports, notifications"

Seed `m5-site.json`: simulator (office), users admin, `mgr` (manager: `reports.run`,
`reports.schedule`, `admin.audit.view`), operators `op1`, `op2` with verified e-mail and phone;
external contact "Security company"; Mailpit container (SMTP + API), fake SMS provider (generic
HTTP provider pointing at a test receiver container that records requests and posts delivery
callbacks); history pre-filled with 30 days of simulator data (`tools/wpseed history`); fake
clock control on the node.

### E-040 — Alarm e-mail notification with timeline status

| Step | Action | Expected |
|---|---|---|
| 1 | Admin configures SMTP channel (Mailpit) and adds a Notify action (e-mail to group Operators, Critical alarms) on the forced-door rule; test message | Test e-mail arrives |
| 2 | Door forced (Critical) | Within 10 s, e-mails to op1/op2 in their languages (op2 Arabic RTL), link to the web client alarm page |
| 3 | Open alarm details | Timeline shows "e-mail sent to op1", "…op2" |
| 4 | Stop Mailpit, raise another alarm | Messages retried; after the last retry dead-lettered; visible in System → Notifications; retry after Mailpit restarts delivers |

### E-041 — SMS via provider with flood summary and quota

| Step | Action | Expected |
|---|---|---|
| 1 | Configure fake SMS provider; roster "Night duty" with op1 22:00–06:00, fallback op2; escalation step at 2 min → roster | — |
| 2 | Fake clock 23:00; raise an alarm, do not acknowledge | After 2 min SMS to op1 (provider received request, callback → delivered in timeline) |
| 3 | Raise 15 alarms in 1 min | op1 gets 5 individual SMS then one summary SMS ("10 new alarms…") |
| 4 | SMS quota set to 8/h | Further SMS suppressed; system event "quota reached" once |

### E-042 — Scheduled PDF shift report

| Step | Action | Expected |
|---|---|---|
| 1 | `mgr` schedules `std.shiftReport` at the end of calendar "Day shift", range previousCalendarPeriod, PDF + XLSX, to `mgr` and external contact | Schedule saved; warning that the external contact will not receive personal data templates (shift report includes holder names → refused for the contact, kept for `mgr`) |
| 2 | Fake clock to the end of the shift | Report run; e-mail to `mgr` with PDF attachment; PDF text contains the shift's alarms and events; XLSX opens with typed cells |
| 3 | `mgr` loses `operate.events.view` | Next run fails with owner-rights error; `mgr` and configurators notified |

### E-043 — Retention purge and aggregates

| Step | Action | Expected |
|---|---|---|
| 1 | Set events retention to 7 days (minimum allowed for the test profile) | Setting accepted with warning |
| 2 | Run the retention job (fake clock + job trigger) | Events older than 7 days gone from the journal; `alarms_daily` and `events_hourly` aggregates still give 30-day statistics in `std.alarmStatistics` |
| 3 | Audit trail unaffected (separate retention) | Audit entries of day 1 still present |

### E-044 — Privacy: subject search, export, pseudonymisation

| Step | Action | Expected |
|---|---|---|
| 1 | Admin searches person reference "Holder 0042" | Events across 30 days listed |
| 2 | Export (JSON) | File in FIL `export.events`, download audited |
| 3 | Erase with reason | Events now show a pseudonym; counts per holder still group; audit `privacy.subjectErased`; audit chain still verifies |
| 4 | Operator without personal data permission views events | Names masked ("Cardholder #…") |

## 7. M6 scenarios — "Synoptics, video, web"

Seed `m6-site.json`: simulator (campus) with locations Building A (levels 1–2) and site geo
coordinates; `OnvifTestDevice` cameras (2 fixed, 1 PTZ) streaming from `mediamtx` test
patterns, linked to doors of Building A Level 1 (`camera-for` links); offline PMTiles extract of
a small test area; users admin, `config1`, operators `op1` (desktop) and `op2` (web, Chromium and
Firefox).

### E-050 — Plan import and device placement

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` creates synoptic "Building A – L1" (plan), imports a 2-page PDF, picks page 2 at 150 DPI, sets scale by two points (10 m) | Background job completes; tiles displayed |
| 2 | Devices panel "unplaced devices of this location" → drag 20 doors and 2 cameras; draw zones for 3 rooms bound to child locations | Symbols bound with type defaults; camera cones from geometry |
| 3 | Add a command button "Lock down" (doorMode.setMode lockedDown on a group) and an appearance rule with an invalid CEL expression | Validation panel shows plain-language error; fix; publish |
| 4 | Delete one placed door in the registry (draft, publish) | Synoptic shows a grey "no longer exists" symbol; warning in the synoptic's validation |

### E-051 — Alarm focus on plan

| Step | Action | Expected |
|---|---|---|
| 1 | `op1` has the plan open zoomed out (zones only) | Zone colours reflect aggregated states |
| 2 | Door forced in room 2 | Room 2 zone turns alarm colour; console shows alarm |
| 3 | "Show on plan" from the alarm | Plan centres on the door, zooms to labels level, pulses 5 s |
| 4 | Zoom/pan around 1,000 simulated symbols (campus plan fixture) | Viewport subscription ≤ visible devices; frame time within budget |

### E-052 — Linked camera in alarm details (desktop)

| Step | Action | Expected |
|---|---|---|
| 1 | Door with linked camera forced | Alarm details show live video within 2 s (WebRTC) |
| 2 | "Play from alarm time − 10 s" | Timeline missing (camera has no recordings) → button disabled with explanation ("This camera has no recordings") |
| 3 | Snapshot | Snapshot stored as alarm attachment, visible in the timeline |

### E-053 — PTZ with arbitration and joystick

| Step | Action | Expected |
|---|---|---|
| 1 | `op1` moves the PTZ camera with on-screen pad and goes to preset 2 | Test device receives ContinuousMove/Stop/GotoPreset |
| 2 | `op2` (lower PTZ priority) tries to move within hold time | Refused: "controlled by op1, 25 s" |
| 3 | Virtual gamepad (Playwright gamepad emulation) on `op1` | Moves throttled ≤ 10/s; release stops within 1 s |

### E-054 — Map synoptic offline

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` creates a map synoptic, places devices from geo-coordinates, building zone linked to the L1 plan | Offline basemap tiles loaded from the node (no internet in the test network) |
| 2 | `op1` opens the map; clusters at low zoom show aggregated state; clicks the building | L1 plan opens |

### E-055 — Web client operation only

| Step | Action | Expected |
|---|---|---|
| 1 | `op2` signs in on the web (Chromium and Firefox) | Operation UI; enables sounds and browser notifications |
| 2 | Alarm raised while tab hidden | Browser notification; click returns to the alarm; acknowledge works |
| 3 | Opens synoptic and a camera tile | Plan renders; video plays (WebRTC or MSE fallback) |
| 4 | `config1` signs in on the web | No configuration UI; direct config API calls → 403 |
| 5 | Admin sets `Web:AllowedNetworks` excluding the test client network | Next web request → 403; sign-in refused |

### E-056 — Video under load and gateway restart

| Step | Action | Expected |
|---|---|---|
| 1 | 16 tiles (desktop) + 10 web viewers of the same camera | One source connection per camera/profile at the gateway |
| 2 | Kill the go2rtc process | Gateway restarts; players reconnect within 10 s; streams re-registered |

## 8. M7 scenarios — "Licensing"

Compose stack adds the LPT portal (API, signer with SoftHSM2, PostgreSQL, Mailpit) on a
network reachable from the "online" node; a second "offline" node has no route to the portal.
Portal seeded with partner "Integrator SA", customer "Example Bank", an order (Professional, 100
units, 3 desktop seats, 2 web seats, `int.simulator` included, `int.modbus` included) and its
activation code. FakeTimeProvider control on nodes; portal time real.

### E-060 — Online activation with review

| Step | Action | Expected |
|---|---|---|
| 1 | Fresh online node starts | Trial state, watermark banner, 30 days |
| 2 | Admin requests activation with the code (online) | Request "pending review" in the UI; portal review queue shows it with no anomaly |
| 3 | Licensing staff approves | Within one poll interval (test: 30 s) the node imports the licence; banner gone; audit `licence.activated`; configurators notified |
| 4 | Licence panel | Edition Professional, 5/5 components match, usage vs quotas |

### E-061 — Offline activation by files

| Step | Action | Expected |
|---|---|---|
| 1 | Offline node: save request file | `.wprq` downloaded |
| 2 | Partner uploads it in the portal; staff approves; partner downloads `.wplic` | Licence issued |
| 3 | Admin imports `.wplic` on the offline node | Licensed |
| 4 | Import the same licence on the online node | Refused: footprint 0/5 match / installation mismatch with plain message |

### E-062 — Quota enforcement

| Step | Action | Expected |
|---|---|---|
| 1 | Simulator connection importing 120 full-class devices in a draft | Publish refused: "120 units requested, 100 licensed" with numbers; forecast line in licence panel |
| 2 | Import only 90 | Publish succeeds |
| 3 | Fourth desktop session | Refused with seats message; existing sessions unaffected; admin emergency seat works |

### E-063 — Expiry, grace and restricted mode keep alarms working

| Step | Action | Expected |
|---|---|---|
| 1 | Subscription licence ending tomorrow; fake clock +2 days | Grace: daily banners and notifications |
| 2 | Fake clock +31 days | Restricted mode banner |
| 3 | Door forced; operator acknowledges | Alarm raised, notified and acknowledged normally |
| 4 | Configurator publishes a change | Refused (restricted); licence import still possible |
| 5 | Journal query 10 days back | Refused with restricted-history message; last 7 days OK |
| 6 | Import renewed licence | Back to Licensed immediately |

### E-064 — Hardware change (re-host) and clock rollback

| Step | Action | Expected |
|---|---|---|
| 1 | Change 3 footprint components (test hook replacing collector values) | FootprintMismatch: 14-day grace in full mode with banner |
| 2 | Re-host request online; portal shows "2/5 components match previous licence" anomaly; staff approves | New licence imported; banner gone |
| 3 | Move node clock back 3 days | Clock rollback banner; after 3 days grace (fake clock forward from the rolled-back time) restricted |

## 9. M8 scenarios — "Automate"

Seed `m8-site.json`: simulator (campus) with fire category devices in Building A, linked cameras
(C-RTSP test devices), a "video-wall" workstation group with one desktop client, Script-kind
identities "Fire robot" (grants: doorMode on Building A, notify, alarms raise) and "Counter
script" (read Building A, virtual devices), a test HTTP endpoint (allow-listed) recording requests.

### E-070 — Fire evacuation automation

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` instantiates the "Fire alarm: unlock doors and show cameras" template for Building A with identity "Fire robot"; dry-run with a sample fire alarm | Trace lists 20 door commands (not sent), wall notice, notification; publish |
| 2 | Simulator fire alarm in Building A | Doors set to emergencyOpen and verified; wall workstation shows exit cameras; security team e-mail |
| 3 | One door refuses (simulator failure mode) | "Exit door not open" Critical alarm raised by the automation |
| 4 | Runs panel | Run timeline with steps and outcomes |

### E-071 — Run continues after node restart

| Step | Action | Expected |
|---|---|---|
| 1 | Automation: trigger manual → command → wait 60 s → command | Started manually |
| 2 | Kill the node during the wait; restart | Run resumes at the wait; second command sent once; first not repeated (CMD journal shows one each) |

### E-072 — Operator task

| Step | Action | Expected |
|---|---|---|
| 1 | "Night arming check" at calendar edge (fake clock) with Building B disarmed | Task "Arm Building B?" shown to operators (desktop and web) |
| 2 | Operator answers Yes on the web | Area armed by the automation identity; task closed for others |
| 3 | Next day, nobody answers within 10 min | Default (No) path taken; run succeeded with note |

### E-073 — Script reacting to events with test mode

| Step | Action | Expected |
|---|---|---|
| 1 | `config1` writes a script in the IDE (forced door → PTZ preset + custom alarm); a typo in a device name | Red underline with "did you mean 'Main entrance'?" quick fix |
| 2 | Test mode: pick a recent forced-door event from history | Console: "would send ptz.gotoPreset to Camera 3", alarm described; nothing sent |
| 3 | Set a breakpoint in the handler and fire again | Debugger pauses; variables visible; continue |
| 4 | Publish; real forced door | PTZ preset command sent with issuer Script; custom alarm raised |

### E-074 — Virtual device and HTTP

| Step | Action | Expected |
|---|---|---|
| 1 | "Counter script" defines virtual device "Building A occupancy" (analog input) and updates it from access granted events; posts the count to the allow-listed endpoint every minute | Virtual device appears in lists, synoptic value element and alarm rule (threshold) work |
| 2 | Script tries `fetch` to a non-listed host | Error `scr.http.endpoint_not_allowed` in console; script keeps running |

### E-075 — Script limits isolation

| Step | Action | Expected |
|---|---|---|
| 1 | Publish a script with an infinite loop in a handler and one allocating 1 GB | Invocations aborted (CPU/heap limits); after 10 failures the script is suspended with system alarm |
| 2 | Other scripts and automations meanwhile | Unaffected (E-074 counter keeps updating) |
| 3 | Kill the script host process | Restarted; scripts redeployed; buffered events delivered |

## 10. Later milestones (to be detailed with their specs)

| Id | Milestone | Scenario |
|---|---|---|
| E-080…E-088 | M9 | Failover of a redundant pair under load (< 30 s, no lost events); split-brain prevention; rolling upgrade; federation link with WAN cut and resume; handover schedule |
| E-090…E-093 | M10 | Mobile push and acknowledgement; on-site in-app alerts without internet |
| E-100…E-105 | M11 | Fresh install on Windows and Linux with setup wizard; SQL Server provider end-to-end; store migration; signed update and rollback |
