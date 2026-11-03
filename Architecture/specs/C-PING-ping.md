# C-PING — Ping / TCP / HTTP Check Connector

| | |
|---|---|
| Spec id | C-PING |
| Projects | `src/integrations/Watchpoint.Integration.Ping` (package), tests `tests/integrations/Watchpoint.Integration.Ping.Tests` |
| Package id | `watchpoint.integration.ping` (builtin, licence class `included`) |
| Milestone | M4 |
| Tasks | T4.04 |
| Event id range | 5100–5199 |
| Design docs | [Integrations](../../docs/modules/integrations.md) |

## 1. Purpose and scope

Supervise the reachability of network equipment that has no richer integration (switches,
recorders, cameras, controllers, servers, any IP device) and of HTTP(S) endpoints:

- **ICMP echo** (ping): round-trip time, packet loss.
- **TCP connect**: port open, connect time.
- **HTTP(S) check**: status code, response time, optional body match, TLS certificate expiry.

Each check target becomes a device whose health and properties feed the usual state, alarm
and history machinery. No commands (except `health.refresh`). First real-protocol connector:
small, and it exercises polling, scheduling, the request gate, `ISecureHttp` and the
conformance suite.

## 2. Dependencies

`Watchpoint.Integration.Abstractions`, `Watchpoint.Integration.Sdk` (`ConnectorBase`,
`PollingLoop`), `System.Net.NetworkInformation.Ping` (BCL). No third-party
dependencies.

## 3. Layout

```
Watchpoint.Integration.Ping/
├── manifest.json
├── settings/ping.settings.json
├── types/ping.server.json, net.host.json, net.tcp-service.json, net.http-endpoint.json
├── capabilities/net.reachability.json, net.tcpCheck.json, net.httpCheck.json
├── rules/std.net.unreachable.json, std.net.degraded.json, std.net.certificateExpiring.json
├── i18n/en.json, fr.json, ar.json
├── docs/README.md (en), README.fr.md, README.ar.md
├── src/PingConnector.cs, Targets.cs, Checks/IcmpCheck.cs, Checks/TcpCheck.cs, Checks/HttpCheck.cs,
│       Evaluation/TargetEvaluator.cs (window, thresholds, hysteresis), PingDiagnostics.cs, PingLog.cs
└── Watchpoint.Integration.Ping.csproj
```

## 4. Types and capabilities (package-provided)

| Capability | Properties | Events |
|---|---|---|
| `net.reachability` | `status` (enum: up normal, degraded warning, down fault, unknown unknown), `rtt` (decimal ms), `packetLoss` (decimal %, 0–100) | `wentDown` (fault), `cameUp` (info), `degraded` (warning) |
| `net.tcpCheck` | `status` (same enum), `connectTime` (decimal ms) | same events |
| `net.httpCheck` | `status` (same enum), `statusCode` (int), `responseTime` (decimal ms), `contentMatched` (boolean: false warning), `certificateExpiresInDays` (int; ≤ 30 warning via rule) , `lastError` (string) | same + `certificateExpiring` {days} (warning) |

| Type | Role | Capabilities | Licence |
|---|---|---|---|
| `ping.server` | server (the connection's root: "Network checks") | health | free |
| `net.host` | device | health, net.reachability | free |
| `net.tcp-service` | device | health, net.tcpCheck | free |
| `net.http-endpoint` | device | health, net.httpCheck | free |

Device **health connectivity** follows `status`: up → online, degraded → degraded, down →
offline, unknown → unknown. Rule templates (`ruleTemplates`) shipped: `std.net.unreachable`
(OnState status == down for ≥ `alarmAfter`, default priority 300), `std.net.degraded` (disabled
by default), `std.net.certificateExpiring` (OnEvent certificateExpiring, priority 200).

## 5. Settings (`settings/ping.settings.json`)

| Field | Type | Default | Rules |
|---|---|---|---|
| `defaults.interval` | duration PT5S–PT1H | PT30S | |
| `defaults.timeout` | duration PT0.2S–PT30S | PT2S | < interval |
| `defaults.samples` | integer 1–10 | 3 | ICMP echos per round (spaced 200 ms) |
| `defaults.downAfter` | integer 1–20 | 3 | consecutive failed rounds before `down` |
| `defaults.upAfter` | integer 1–20 | 2 | consecutive good rounds before `up` |
| `defaults.degradedRtt` | duration | PT0.2S | RTT above → degraded |
| `defaults.degradedLoss` | decimal % 0–100 | 20 | loss above → degraded |
| `maxConcurrency` | integer 1–512 | 64 | request gate size |
| `targets` | array (≤ 10,000) of target objects | [] | `x-wp-ui: table` with CSV import/export in the wizard |
| `targets[].id` | string `^[A-Za-z0-9._-]{1,64}$` | required, unique | becomes the device native id (stable) |
| `targets[].name` | string ≤ 200 | id | device vendor name |
| `targets[].kind` | enum `icmp`, `tcp`, `http` | `icmp` | |
| `targets[].address` | string | required | host name, IPv4 or IPv6 (icmp/tcp); absolute `http(s)` URL (http) |
| `targets[].port` | integer 1–65535 | required for tcp | |
| `targets[].interval`, `timeout`, `downAfter`, `upAfter`, `degradedRtt`, `degradedLoss`, `samples` | as defaults | null = default | per-target override |
| `targets[].http.method` | enum GET, HEAD | GET | |
| `targets[].http.expectedStatus` | string, e.g. `200-399` or `200,204` | `200-399` | |
| `targets[].http.contentMatch` | string (regex, ≤ 200, 100 ms timeout) | null | evaluated on first 64 KB |
| `targets[].http.headers` | object string→string (≤ 10) | {} | values may reference secret `httpHeaderSecret` via `{{secret}}` |
| `targets[].http.tls` | `{ mode: system|customCa|pin, customCaPem?, pinSha256? }` | system | passed to `ISecureHttp`; no "ignore errors" |
| `targets[].http.allowHttp` | boolean | false | must be true for `http://` URLs |
| `targets[].http.certificateWarningDays` | integer 0–365 | 30 | 0 disables |
| `targets[].parent` | string (target id) | null | **dependency**: when the parent is down, this target reports `unknown` with reason `parentDown` instead of down (avoids alarm storms behind a failed switch) |

Secrets: `httpHeaderSecret` (kind `token`, optional).

Validation in `InitializeAsync` (field errors with paths): duplicate ids, unknown parent,
parent cycles, timeout ≥ interval, invalid regex, tcp without port, `http://` without
`allowHttp`.

## 6. Behaviour

### 6.1 Inventory

`DescribeAsync` returns: one server (`ping.server`, native id `checks`) and one device per
target (type by kind, native id = `id`, vendor name = `name`, attributes `address`, `port`,
`kind`, `dependsOn` (parent target id). The dependency is an attribute, not a REG parent: REG
parents mean containment, and a camera behind a switch is not "inside" the switch).
Settings changes (targets added/removed) trigger `ReportInventoryChanged` with the complete
snapshot.

### 6.2 Scheduling

- One polling job per distinct interval (`SchedulePolling("interval-PT30S", …)`), each round
  checks all targets with that interval, **spread** evenly across the interval (target `i` of
  `n` starts at offset `i × interval / n`) to avoid bursts.
- Each check runs through `IRequestGate.RunAsync(Background)` (concurrency `maxConcurrency`).
- A check never overlaps itself: if the previous round for a target is still running, the round
  is skipped and counted (`ping.round.skipped`).
- `health.refresh` command on a device → immediate check of that target (priority Normal).

### 6.3 Checks

| Kind | Implementation | Success | Measures |
|---|---|---|---|
| icmp | `Ping.SendPingAsync(address, timeout, 32-byte payload)` × samples, 200 ms apart; DNS resolved once per round (failure = all samples lost, `lastError = dns`) | ≥ 1 reply | rtt = median of replies; loss = lost/samples × 100 |
| tcp | `Socket.ConnectAsync` with timeout; closed immediately (RST avoided: graceful close) | connected | connectTime |
| http | `ISecureHttp.CreateClient` (one client per target, reused); request with timeout; body read up to 64 KB when contentMatch set; `HEAD` ignores contentMatch | status in expected set and (no contentMatch or matched) | responseTime, statusCode, certificate NotAfter → days |

Linux ICMP: .NET uses unprivileged ICMP sockets when `net.ipv4.ping_group_range` includes the
service group, else falls back to the `ping` utility. The installer (DEP) sets the sysctl; the
connector's `TestAsync` reports a clear message if neither works (`ping.icmp.not_permitted`).

### 6.4 Evaluation (`TargetEvaluator`, pure, unit-tested)

- Round result → `good` (success and not degraded), `degraded` (success but rtt/connect/response
  time > degradedRtt or loss > degradedLoss, or HTTP content not matched), `bad` (failure).
- State machine per target: `unknown` initially; `downAfter` consecutive bad → `down`;
  `upAfter` consecutive good → `up`; degraded rounds → `degraded` immediately when currently up
  (back to up after `upAfter` good rounds); degraded rounds while down count as good for
  recovery.
- Parent dependency: if parent state is `down`, child state reported as `unknown` with reason
  `parentDown`, its own counters keep running; when the parent recovers, the child's actual
  state is reported.
- Reports: properties every round (rtt, loss, times) — the sink coalesces; `status` only on
  change; events on transitions; device health on transitions; certificate warning event once
  per day while ≤ warning days.

## 7. Connection test (`TestAsync`)

Checks up to the first 5 targets once (no state machine) and returns steps: "Name
resolution", "ICMP permitted" (if any icmp target), "Target <id>: reachable in 3 ms" …; overall
success if the platform allows the check kinds used (unreachable targets are warnings, not
failures — a wizard should not block on an offline device).

## 8. Diagnostics (`IDiagnostics`)

| Action | Arguments | Result |
|---|---|---|
| `checkNow` | `{ targetId }` | Raw result of one round with timings |
| `traceroute` | `{ targetId, maxHops? = 20 }` | Hops with RTT (ICMP TTL-limited pings), ≤ 60 s |
| `resolve` | `{ address }` | DNS addresses |
| `summary` | — | Counts by status, skipped rounds, average check duration |

## 9. Translations

All capability/type/property/event labels, settings labels and descriptions, error messages
and docs in en/fr/ar (key coverage test PING-U-020).

## 10. Observability

| EventId | Level | Message |
|---|---|---|
| 5100 | Information | Ping connector started with {Targets} targets ({Icmp} icmp, {Tcp} tcp, {Http} http) |
| 5101 | Warning | ICMP not permitted on this host; falling back to ping utility / failing |
| 5110 | Debug | Target {TargetId} {Kind} round: {Result} in {ElapsedMs} ms |
| 5111 | Information | Target {TargetId} state {From} → {To} |
| 5120 | Warning | Round for {TargetId} skipped (previous still running) |

Connector metrics: `checks` (counter by kind, success), `check_duration` (via `CountRequest`),
`targets_down` (gauge), `rounds_skipped` (gauge).

## 11. Tests

| Id | Layer | Test |
|---|---|---|
| PING-U-001 | U | Settings validation errors with paths (duplicates, parent unknown/cycle, timeout ≥ interval, regex, tcp port, http without allowHttp) |
| PING-U-002 | U | Inventory from targets: types by kind, attributes, `dependsOn` |
| PING-U-003 | U | Inventory change reported on settings change |
| PING-U-004 | U | Scheduling: one job per interval; spread offsets |
| PING-U-005 | U | No overlap: skipped round counted |
| PING-U-006 | U | Request gate concurrency respected (fake checks blocking) |
| PING-U-007 | U | ICMP result aggregation: median RTT, loss %, DNS failure |
| PING-U-008 | U | TCP check success/timeout/refused |
| PING-U-009 | U | HTTP expected status parsing (`200-399`, lists) |
| PING-U-010 | U | HTTP content match on first 64 KB with regex timeout |
| PING-U-011 | U | HTTP certificate expiry days and daily warning event |
| PING-U-012 | U | HTTP TLS modes passed to ISecureHttp; header secret substitution not logged |
| PING-U-013 | U | Evaluator: downAfter/upAfter counters |
| PING-U-014 | U | Evaluator: degraded thresholds and recovery |
| PING-U-015 | U | Evaluator: parent down → child unknown with reason; recovery |
| PING-U-016 | U | Reports: status/events/health only on transitions; measures every round |
| PING-U-017 | U | `health.refresh` command triggers immediate check |
| PING-U-018 | U | TestAsync steps; unreachable targets as warnings; ICMP not permitted message |
| PING-U-019 | U | Diagnostics actions results |
| PING-U-020 | U | Translations key coverage en/fr/ar; manifest and types validate with `wptypes` |
| PING-I-001 | I | ICMP against localhost and an unroutable address (TEST-NET-1 192.0.2.1) |
| PING-I-002 | I | TCP against a listener started by the test and a closed port |
| PING-I-003 | I | HTTP against Kestrel test server: status ranges, content match, self-signed with pin and custom CA |
| PING-I-004 | I | 1,000 targets at 30 s interval with 64 concurrency: no skipped rounds, CPU < 5 % of a core (benchmark) |
| PING-C | C | Conformance suite INT-C-001…040 (applicable cases; no credentials, no commands except refresh) passes in-process and isolated. `IVendorSimulator` = `PingTargetSimulator`: TCP and HTTP listeners on loopback that can be stopped, slowed or made to fail; ICMP cases use 127.0.0.1 (up) and 192.0.2.1 (down) |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T4.04 | all | PING-U-001…PING-U-020, PING-I-001…PING-I-004, conformance |
