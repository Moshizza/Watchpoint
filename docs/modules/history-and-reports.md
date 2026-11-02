# History and Reports

> Tables below are described with the default provider (TimescaleDB). Other databases use
> the equivalent mechanisms listed in [Data storage §3.1](data-storage.md#31-how-each-database-covers-watchpoints-needs).

## 1. What is stored

| Hypertable (TimescaleDB) | Content | Default retention |
|---|---|---|
| `history.events` | All events (device, system, operator) | 1 year |
| `history.alarms` + `history.alarm_timeline` | Alarm records and every transition, comment, procedure answer | 3 years |
| `history.commands` | Every command with lifecycle and result | 1 year |
| `history.property_changes` | Value changes per property history policy | 90 days raw, then aggregates |
| `history.audit` | Audit trail (hash-chained) | 5 years |
| `history.sessions` | Sign-ins and sessions | 1 year |

Retentions are configurable per category and per site, subject to legal constraints of each
country (e.g. CCTV-related or access logs limits). Personal data fields follow privacy
retention (see [Users and rights §8](users-and-rights.md#8-privacy-gdpr-and-similar-laws)).

## 2. Storage design

- Written by the **history writer**, which consumes the event log in batches (COPY into
  PostgreSQL) — fast, and decoupled from live processing.
- Hypertables partitioned by time (daily or weekly chunks), **compressed** after a few days
  (typically 90 %+ reduction), dropped by **retention policies** without heavy deletes.
- **Continuous aggregates** for charts and reports: hourly/daily counts of events and alarms
  per location/type, min/avg/max of measurements.
- Source snapshots (names, location path) are stored with each row, so history remains
  readable after configuration changes.
- Archive option: export old chunks to compressed files (Parquet) on a file share or S3
  compatible storage before dropping.

## 3. History views in clients

- **Event journal**: virtualised table with filters (time, type, severity, location,
  server, device, tag, person, operator), full-text search in payload/comments, saved
  filters, export CSV/XLSX/PDF.
- **Alarm history**: same with alarm-specific columns (times to acknowledge/close, owner,
  outcome), open the full timeline.
- **Device history**: property charts (ECharts) with zoom, overlays of events, comparisons.
- **Command history**, **audit viewer**, **session history** (permissions apply).

## 4. Reports

| Report | Content |
|---|---|
| Alarm statistics | Counts by priority, location, type; trends; top sources; false-alarm ratio |
| Operator performance | Time to acknowledge/close per operator/shift; workload |
| Device availability | Offline time per device/server/connection; fault occurrences |
| Access summary | Granted/denied per door/period (from integrated access control events) |
| Intrusion summary | Arm/disarm times per area, by whom, late/missed arming |
| Command report | Commands per user, failures, unverified commands |
| Shift report | Events, alarms and operator notes of a shift, handover text |
| Audit report | Configuration and rights changes in a period |

- Report templates are HTML + parameters (time range, scope, language), rendered on screen
  and to PDF by headless Chromium; tables to XLSX with ClosedXML; CSV everywhere.
- **Scheduled reports** sent by e-mail or saved to a folder (daily shift report, monthly
  statistics).
- Custom report templates editable by configurators (layout + query blocks chosen from a
  catalogue — no SQL), and a **read-only SQL view** for analysts using external BI tools
  (with a dedicated, read-only database role on curated views).
