# Performance and Load Tests (P-001…P-012)

Task T9.08. Design targets come from
[Performance and capacity](../../docs/operations/performance-and-capacity.md). These tests
produce the **capacity sheet** shipped with each release (`docs/operations/capacity-sheet.md`,
generated) and the measured limits used by the HLT capacity advisor.

## 1. Tooling

| Tool | Use |
|---|---|
| **C-SIM in remote mode** (`tools/wpsim`) | Device load: N devices with configurable change/event rates, bursts, command latency; several simulator processes for > 25,000 devices |
| **k6** (internal use only, not shipped) | API and live hub load: virtual users signing in, subscribing (SignalR via k6 WebSocket + MessagePack helper `tests/perf/k6/lib/signalr.js`), querying history, acknowledging alarms |
| `tests/perf/Watchpoint.Perf` (.NET console) | Orchestrates a run: starts the topology, seeds configuration, drives phases, collects metrics (OpenTelemetry → Prometheus → CSV), evaluates pass/fail, writes the report |
| Prometheus + Grafana (containers, internal) | Metrics during runs; dashboards committed in `tests/perf/grafana/` |
| Reference hardware | **Profile S**: 8 vCPU, 32 GB RAM, NVMe SSD (one node, DB on same machine). **Profile M**: 2 nodes × S + witness + dedicated DB 8 vCPU/32 GB. Runs on dedicated runners (not shared CI), weekly and before releases |

Each run records: versions, hardware profile, settings deviating from defaults, raw metrics,
pass/fail per criterion, and diffs against the previous release (regression > 10 % fails the
release gate).

## 2. Tests

| Id | Scenario | Load | Pass criteria |
|---|---|---|---|
| P-001 | Sustained ingest | Profile S, 25,000 devices, 5,000 observations/s for 60 min | No backlog growth; ingest latency p95 < 100 ms; CPU < 70 %; no dropped records |
| P-002 | Burst ingest | P-001 + burst 20,000 observations/s for 60 s | No loss (all records in log and history); backlog drained < 2 min after burst; quotas/coalescing counters reported |
| P-003 | End-to-end latency | 10,000 devices, 1,000 obs/s, 20 desktop clients (k6 VUs) subscribed to 500 devices each | Field observation → client delta p95 < 300 ms, p99 < 800 ms |
| P-004 | Alarm flood | 1,000 alarms/min for 10 min, 20 operators consoles | Raise → console p95 < 1 s; acknowledge round trip p95 < 300 ms; notifications flood summaries respected |
| P-005 | Group command | Lockdown of 1,000 doors (200 commands/s) | Accepted → dispatched p95 < 100 ms; all verified within simulator latency + 5 s |
| P-006 | Client scale | Profile M with front role: 200 concurrent sessions (150 web, 50 desktop) with typical workload mix | API p95 < 200 ms for lists; hub delivery p95 < 500 ms; no node > 80 % CPU |
| P-007 | History queries | 1 year of history for 5,000 devices (generated with `wpseed history`), 20 users querying journals and reports | Journal page p95 < 1 s; alarm statistics report (1 year) < 30 s; ingest unaffected (P-001 latency within +20 %) |
| P-008 | Crash recovery | Kill node (Profile S, 25,000 devices) | Ready and live within 60 s (snapshot + replay); no lost records |
| P-009 | Failover | Profile M, 2,000 devices, 20 connections, 200 events/s; kill owner | Failover < 30 s p95 over 10 repetitions; zero lost events from catch-up connectors; zero duplicate dispatch |
| P-010 | Long soak | Profile S, P-001 load at 50 % for 72 h with daily config publishes and client churn | Memory stable (no growth trend > 5 %/day); no handle leaks; GC pauses p99 < 50 ms |
| P-011 | Integration hosts | 20 isolated hosts with simulator connections, 50,000 obs/s total | Host overhead per §6.4 IHO targets; restarts recover < 5 s |
| P-012 | Federation | Central + 10 child sites (containers, WAN emulation 50 ms RTT, 10 Mbit/s), each 2,000 devices | Child → central alarm latency p95 < 2 s; resume after 1 h outage completes < 10 min; central CPU < 50 % |

## 3. Workload mix (P-006 and P-010)

| Action | Share of VU time |
|---|---|
| Alarm console open (subscribed) | 100 % of operator VUs |
| Device list browsing with live rows | 40 % |
| Synoptic viewport subscriptions (pan/zoom every 10 s) | 30 % |
| Event journal queries (last 24 h, filters) | 15 % |
| Commands (1 per 2 min per VU) | — |
| Acknowledge/comment alarms | as alarms arrive |
| Reports (one 1-month report per hour per 10 VUs) | — |

## 4. Report format

`tests/perf/reports/<version>/<profile>/<test>.md` with tables of criteria (target, measured,
pass), charts (PNG from Grafana render API), environment, and the generated capacity sheet
rows: devices per node, observations/s, sessions per front node, alarms/min, history size per
device per year.
