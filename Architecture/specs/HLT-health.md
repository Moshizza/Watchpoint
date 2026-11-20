# HLT — System Health

| | |
|---|---|
| Spec id | HLT |
| Projects | `src/service/Watchpoint.Health`, UI `src/ui/packages/configuration/src/system/health/` (dashboard; also visible read-only on the operation side for `admin.health.view`) |
| Milestone | M9 |
| Tasks | T9.05 |
| Event id range | 3700–3749 (Health/Maintenance range; MNT uses 3750–3799) |
| Design docs | [Core runtime §6](../../docs/modules/core-runtime.md#6-system-health), [Deployment and operations](../../docs/operations/deployment-and-operations.md), [Performance and capacity §6](../../docs/operations/performance-and-capacity.md#6-capacity-monitoring) |

## 1. Purpose and scope

One **System → Health** view and API for administrators: every node, process and dependency
(database, event log, media gateway, script host, integration hosts, connections), resource
usage, backlogs and latencies, with **thresholds** that raise system events (alarm-able), a
**capacity advisor** that recommends actions (add a node, move history, raise limits) from
measured load, and a **diagnostic bundle** export for support.

Not in scope: the metrics/telemetry pipeline itself (HST OpenTelemetry), node liveness endpoints
(HST `/health/*`).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST (health checks registry, OpenTelemetry `MeterListener`), CLU (members, quorum, leases), STO (DB stats), PIP (stream/consumer info, backlogs), IRT/IHO (connections, hosts), VID (gateways), SCR (script host), FIL (disk usage), LIC (usage), AUD (chain status), EVT (system events), NOT (system notifications), FIL (bundle file) | UIF/UIO health pages, MNT/ALM (system alarms on health events), FED (child health on links), DEP (post-upgrade health check) |

## 3. Layout

Log event ids: 3700–3749 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Health/
├── Contracts/HealthSnapshot.cs, HealthItem.cs, HealthStatus.cs, ThresholdRule.cs, IHealthService.cs, Recommendation.cs
├── Internal/
│   ├── Collectors/ (NodeResources, Database, EventLog, Pipeline, Connections, Hosts, MediaGateway, ScriptHost, Files, Clock, Certificates, Licence, Audit, Backups)
│   ├── Aggregation/ClusterHealthAggregator.cs (each node publishes its snapshot on WP_SYS every 10 s)
│   ├── Thresholds/ThresholdEvaluator.cs (hysteresis, durations) → system events
│   ├── Capacity/CapacityAdvisor.cs (rules over 7-day metrics history)
│   ├── Bundle/DiagnosticBundleBuilder.cs (redaction, manifest, zip)
│   └── History/HealthHistory.cs (5-min aggregates, 30 days, wp_health)
├── HealthModule.cs, HealthLog.cs
```

## 4. Model

```csharp
public enum HealthStatus { Ok, Warning, Error, Unknown }
public sealed record HealthItem(string Id /* "node.<id>.cpu", "db.replication.lag", "connection.<id>" */, string Category /* node | database | eventLog | pipeline | integrations | video | scripts | files | security | licence */,
    LocalizedText Label, HealthStatus Status, IReadOnlyList<HealthMetric> Metrics, LocalizedText? Message, string? HelpKey /* docs link */, DateTimeOffset At);
public sealed record HealthMetric(string Name, decimal Value, string? Unit, decimal? Warning, decimal? Error);
public sealed record HealthSnapshot(DateTimeOffset At, HealthStatus Overall, IReadOnlyList<NodeHealth> Nodes, IReadOnlyList<HealthItem> Items, IReadOnlyList<Recommendation> Recommendations);
```

## 5. Collectors and default thresholds

| Collector | Items and metrics | Warning / Error (defaults, sustained 2 min unless noted) |
|---|---|---|
| Node resources | CPU %, memory working set, GC pause p99, thread pool queue, disk free per data volume, open handles | CPU 80/95 %; memory 80/90 % of limit; disk free 15/5 % (or 20/5 GB) immediate |
| Clock | NTP offset vs peers (cluster) and vs `GET /api/v1/time` peers | 1 s / 2 s (CLU skew limit) |
| Database | Reachable, latency p95, connections used/max, replication lag (HA), database size, longest transaction, table bloat estimate (weekly) | latency 50/200 ms; connections 80/95 %; lag 5/30 s |
| Event log | NATS cluster members up, JetStream storage used, stream lag per consumer, R3 health | storage 80/90 %; consumer lag 10,000/100,000 records |
| Pipeline | Ingest rate, latency p95 (received → subscribers done), partition backlog, coalesced and quota-dropped counts, side-effect queues depth | latency 300 ms / 1 s; backlog 5,000/50,000 |
| Integrations | Per connection state, reasons, flooding, silent connector; per host state, restarts | state Faulted → Error; Degraded/Reconnecting > 5 min → Warning |
| Video | Gateways up, streams, WebRTC failures rate | gateway down → Error |
| Scripts | Script host up, suspended scripts, limit breaches/h | suspended → Warning |
| Files | Disk usage per category, quota usage, integrity issues, replication backlog | quota 80/95 %; integrity → Error |
| Security | Certificates expiry (node, CA, SMTP/LDAP pins), audit chain status, failed sign-ins rate | cert 30/7 days; chain failure → Error |
| Licence | State, grace, usage vs quotas | Grace → Warning; Restricted → Error |
| Backups | Last successful backup age, last backup size | > 26 h / > 72 h |

Thresholds editable per site (`system.healthThresholds` CFG document); status changes produce
system events `health.warning` / `health.error` / `health.recovered { itemId, metric, value }`
(severity warning/fault) with hysteresis (recover below 90 % of the threshold for 2 min),
so default alarm rule templates (`std.system.health.error`) can alarm on them and NOT can notify
the IT group.

## 6. Aggregation and history

- Each node computes its snapshot every 10 s and publishes it (compressed) on `WP_SYS`
  `health.snapshot.<nodeId>`; `ClusterHealthAggregator` on every front node merges the latest
  snapshots (stale node snapshot > 30 s → node Unknown).
- History: 5-minute aggregates (min/avg/max per metric) stored in `wp_health.metrics_5m` for 30
  days (sparkline charts and the capacity advisor).

## 7. Capacity advisor

Rules evaluated hourly over 7 days of history (and the load model of the performance doc):

| Rule | Recommendation |
|---|---|
| Pipeline p95 latency > 200 ms for > 10 % of busy hours, CPU > 70 % on pipeline nodes | Add a pipeline node or move roles |
| Devices per integration node > 80 % of measured capacity (P-tests results table shipped with the release) | Add an integration node / rebalance |
| DB write latency rising and history tables > 70 % of disk | Move history to a dedicated server; adjust retention |
| Web sessions near front node capacity | Add a front node |
| Media gateway CPU > 70 % | Use sub-streams by default / add a media node |
| Disk free trend reaching 10 % within 30 days | Extend storage or reduce retention |

Each recommendation: severity, evidence (metrics, charts link), action text with docs link.

## 8. Diagnostic bundle

`POST /api/v1/health/bundles { include: [logs, metrics, config, health, versions, connections, cluster], from, to }`
(`admin.diagnostics.export`) → job:

1. Preview list of contents (files and sizes) returned first; the admin confirms (UI shows the
   list, as the design requires).
2. Builds a zip: manifest (site id hash, versions, nodes), logs (filtered by time, all nodes via
   cluster request), health history, metrics snapshot, configuration **export without secrets**
   (CFG bundle with secret refs removed), connection diagnostics, cluster state.
3. **Redaction** pass: HST redaction policy names, `rtsp://user:pass@`, `Authorization:` headers,
   e-mail addresses and phone numbers replaced by hashes, IP addresses kept (needed for support)
   unless `redactIps`.
4. Stored in FIL `diagnostics.bundle` (7 days), download link; audit `health.bundleExported`.

## 9. API and UI

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/health` | `admin.health.view` | Current cluster snapshot |
| `GET /api/v1/health/items/{id}/history?from=&to=` | `admin.health.view` | 5-min series |
| `GET /api/v1/health/recommendations` | `admin.health.view` | Capacity advisor |
| `GET/PUT /api/v1/health/thresholds` | `config.system.health` | Thresholds document |
| `POST /api/v1/health/bundles` / `GET …/{id}` | `admin.diagnostics.export` | Bundle |

Live topic `health` (snapshot deltas every 10 s).

Dashboard: overall status banner; nodes cards (roles, CPU/memory/disk sparklines, owned
partitions/connections); categories grid with items (status icon + colour + text), click → detail
drawer (metrics, thresholds, history chart, help link, related system events); recommendations
panel; bundle wizard. RTL, accessibility as UIC.

`config.system.health` added to RGT catalogue.

## 10. Errors, observability

Errors: `health.item.not_found`, `health.bundle.too_large` (> 2 GB → narrow the time range),
`health.thresholds.invalid`.

| EventId | Level | Message |
|---|---|---|
| 3700 | Warning | Health {Item} {Status}: {Metric}={Value}{Unit} (threshold {Threshold}) |
| 3701 | Information | Health {Item} recovered |
| 3710 | Information | Capacity recommendation: {Rule} |
| 3720 | Information | Diagnostic bundle {BundleId} created ({Bytes} bytes) by {User} |

## 11. Tests

| Id | Test |
|---|---|
| HLT-U-001…012 | Each collector with fakes produces items/metrics and statuses per default thresholds |
| HLT-U-013 | Threshold evaluator: sustained duration, immediate thresholds, hysteresis recovery |
| HLT-U-014 | System events emitted on transitions only |
| HLT-U-015 | Thresholds document validation and per-site override |
| HLT-U-016 | Node snapshot publication and aggregation; stale node → Unknown |
| HLT-U-017 | 5-min history aggregation and 30-day retention |
| HLT-U-018…023 | Capacity advisor rules with synthetic 7-day histories |
| HLT-U-024 | Bundle preview lists contents before build |
| HLT-U-025 | Bundle redaction (secrets names, RTSP credentials, auth headers, e-mails, phones; IPs option) — fixture logs |
| HLT-U-026 | Bundle config export contains no secrets or secret refs |
| HLT-U-027 | Bundle stored in FIL with retention and audit |
| HLT-U-028 | API permissions and live topic |
| HLT-U-029 | Dashboard rendering: statuses with colour + icon + text, drawer history chart |
| HLT-U-030 | Translations coverage of labels, messages and recommendations |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T9.05 | all | HLT-U-001…HLT-U-030 |
