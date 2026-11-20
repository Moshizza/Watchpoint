# RPT — Reports

| | |
|---|---|
| Spec id | RPT |
| Projects | `src/service/Watchpoint.Reports`, UI in `src/ui/packages/operation/src/reports/` (viewer, run, schedules) and `src/ui/packages/configuration/src/reports/` (template editor) |
| Milestone | M5 |
| Tasks | T5.06 (§4–§7), T5.07 (§8–§9) |
| Event id range | 3250–3299 (History/Reports range; HIS uses 3200–3249) |
| Design docs | [History and reports §4](../../docs/modules/history-and-reports.md#4-reports) |

## 1. Purpose and scope

- **Report templates** built from blocks (title, text, KPI, table, chart, page break) bound to
  **data sources** from a catalogue — no SQL for users.
- Rendering to **HTML** (on screen), **PDF** (headless Chromium), **XLSX** (ClosedXML) and **CSV**.
- Eight **standard templates**; site templates editable by configurators.
- **Scheduled reports** with relative time ranges, delivered by e-mail (NOT) and stored in FIL;
  optional export folder.
- **BI views**: curated read-only SQL views and a read-only database role for external tools.

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HIS (`IHistoryQueries`, aggregates), ALM/CMD/EVT/AUD/MNT/REG (data providers), RGT (rights of the requesting identity), FIL (outputs), NOT (delivery), SCH (calendars for shift ranges, Cronos for cron), CFG (template and schedule object types), API (jobs), PuppeteerSharp (MIT) + bundled `chrome-headless-shell` (BSD-3), ClosedXML (MIT), NodaTime (Apache-2.0) | UIO/UIF (viewer, editor), AUT (M8 "run report" action), operators and managers |

## 3. Layout

Log event ids: 3250–3299 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Reports/
├── Contracts/
│   ├── ReportTemplate.cs, ReportBlock.cs (Title, Text, Kpi, Table, Chart, PageBreak, Group), ReportParameter.cs
│   ├── IReportDataSource.cs, DataSourceDescriptor.cs, DataSet.cs
│   ├── IReportService.cs, ReportRun.cs, ReportSchedule.cs, RelativeRange.cs
│   └── ReportErrors.cs
├── Internal/
│   ├── Catalogue/DataSourceCatalogue.cs, Providers/ (Alarms*, Commands*, Devices*, Access*, Intrusion*, Events*, Audit*, Shift*, Maintenance*, Series*)
│   ├── Execution/ReportExecutor.cs (parameters → data sets, limits), ParameterResolver.cs (relative ranges)
│   ├── Rendering/HtmlRenderer.cs, SvgChartRenderer.cs, PdfRenderer.cs (browser pool), XlsxRenderer.cs, CsvRenderer.cs, Styles/report.css
│   ├── Scheduling/ScheduleRunner.cs, DeliveryService.cs, FolderExporter.cs
│   ├── Standard/ (8 standard templates JSON, en/fr/ar labels)
│   ├── Bi/BiViewsMigration.cs
│   └── Storage/ReportsDbContext.cs
├── ReportsModule.cs
└── ReportsLog.cs
```

## 4. Template model (T5.06)

```csharp
public sealed record ReportTemplate(ReportTemplateId Id, string Key, LocalizedName Name, LocalizedName? Description,
    IReadOnlyList<ReportParameter> Parameters, IReadOnlyList<ReportBlock> Blocks, PageSetup Page, bool Standard, long Version);

public sealed record ReportParameter(string Name /* ^[a-z][a-zA-Z0-9]{0,31}$ */, ParameterKind Kind, LocalizedName Label,
    JsonElement? Default, bool Required);
public enum ParameterKind { TimeRange, Scope /* locations/devices/categories/tags */, Users, UserGroups, Priority, Text, Number, Boolean, Enum }

public abstract record ReportBlock(string Id)
{
    public sealed record Title(string Id, LocalizedText Text, int Level) : ReportBlock(Id);
    public sealed record Text(string Id, LocalizedText Markdown /* placeholders {{param.x}}, {{site.name}}, {{range.from|date}} */) : ReportBlock(Id);
    public sealed record Kpi(string Id, IReadOnlyList<KpiItem> Items) : ReportBlock(Id);   // label, data source + measure, format, comparison to previous period
    public sealed record Table(string Id, DataBinding Data, IReadOnlyList<TableColumn> Columns, int? MaxRows, TableTotals? Totals) : ReportBlock(Id);
    public sealed record Chart(string Id, DataBinding Data, ChartKind Kind /* Bar | StackedBar | Line | Area | Pie | Heatmap */,
        string X, IReadOnlyList<string> Series, LocalizedText? Title) : ReportBlock(Id);
    public sealed record Group(string Id, DataBinding Data, string By, IReadOnlyList<ReportBlock> Blocks) : ReportBlock(Id);  // repeat per group value (e.g. per location)
    public sealed record PageBreak(string Id) : ReportBlock(Id);
}
public sealed record DataBinding(string Source /* "alarms.stats" */, JsonElement Arguments /* may reference {{param.*}} */);
public sealed record PageSetup(PaperSize Size /* A4 | Letter */, Orientation Orientation, bool Logo, LocalizedText? Footer);
```

Templates are CFG object types `reports.template` (configurators: `config.reports.edit`);
standard templates are read-only (duplicate to customise). Validation: data sources exist,
arguments valid against their schemas, column names exist in the source's output, chart fields
numeric where required, parameters referenced exist, nesting depth ≤ 3.

## 5. Data sources (T5.06)

```csharp
public interface IReportDataSource
{
    DataSourceDescriptor Descriptor { get; }   // key, label, argument schema (SCM parameter schema), output columns (name, type, label, unit)
    Task<DataSet> ExecuteAsync(JsonElement arguments, ReportContext context, CancellationToken ct);
}
public sealed record ReportContext(ReportIdentity Identity /* user or schedule owner */, string Culture, string TimeZone,
    DateTimeOffset Now, IVisibleScope Scope /* RGT, for the identity */, int RowLimit);
public sealed record DataSet(IReadOnlyList<DataColumn> Columns, IReadOnlyList<object?[]> Rows, bool Truncated, IReadOnlyList<string> Warnings);
```

Catalogue v1 (providers live in RPT and call module query interfaces; each declares the
permission needed — the identity must hold it):

| Key | Output | Backed by | Permission |
|---|---|---|---|
| `alarms.stats` | bucket, location, rule, priority band, raised, acknowledged, mean/max time to ack, mean time to close, false alarms | HIS `alarms_daily` | `alarms.view` |
| `alarms.list` | alarm rows with times, operator, outcome | HIS alarms | `alarms.view` |
| `alarms.topSources` | source, count | HIS alarms counts | `alarms.view` |
| `operators.performance` | user, shift, alarms handled, mean/p90 time to ack/close | HIS alarms + timeline | `alarms.view` + `config.users.view` |
| `devices.availability` | device/server/connection, offline duration, availability %, fault occurrences | HIS health events | `operate.devices.view` |
| `access.summary` | door, granted, denied by reason, top holders (personal data masked without `operate.personalData.view`) | HIS events (`accessEvents.*`) | `operate.events.view` |
| `intrusion.summary` | area, arm/disarm times, by whom, late/missed arming vs calendar | HIS events (`armable.*`) + SCH | `operate.events.view` |
| `commands.list` / `commands.stats` | commands per user/device/result, unverified | HIS commands | `commands.viewAll` or own |
| `events.counts` / `events.list` | counts by kind/severity/location; rows | HIS events | `operate.events.view` |
| `shift.journal` | events, alarms and operator notes of the range, handover text | HIS + notes | `operate.events.view` |
| `audit.list` | audit entries (configuration and rights changes) | AUD | `admin.audit.view` |
| `maintenance.periods` | maintenance periods with initiator, reason, duration | MNT | `config.maintenance` or `admin.audit.view` |
| `property.series` | time, min/avg/max of a property (≤ 20 devices) | HIS series | `operate.history.view` |

Limits: `RowLimit` per data set (default 50,000 for XLSX/CSV, 5,000 for PDF tables — truncation
noted in the output), query timeout 60 s per data source, whole report 10 min.

## 6. Execution and rendering (T5.06)

### 6.1 Run

```csharp
public interface IReportService
{
    Task<Result<JobRef>> RunAsync(ReportRunRequest request, ReportIdentity identity, CancellationToken ct);   // API job
    Task<Result<ReportPreview>> PreviewAsync(ReportRunRequest request, ReportIdentity identity, CancellationToken ct); // HTML, row limit 200
}
public sealed record ReportRunRequest(ReportTemplateId Template, IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlySet<ReportFormat> Formats /* Html | Pdf | Xlsx | Csv */, string? Culture, string? TimeZone);
```

1. Resolve parameters (relative ranges §8.1, defaults, validation).
2. Execute data bindings (deduplicated identical bindings; parallel up to 4).
3. Render requested formats; store each output in FIL category `report.output` (retention 90
   days) with metadata (template key/version, parameters, identity, row counts).
4. Record `report_runs` row (status, durations, outputs, warnings); audit `reports.run` for
   templates containing `audit.list` or personal data.

### 6.2 HTML

`HtmlRenderer` builds a self-contained HTML document (inline CSS `report.css` with design
tokens, `dir="rtl"` for Arabic, logical CSS properties), all values HTML-encoded, Markdown text
blocks rendered with Markdig (BSD-2) in safe mode (no raw HTML). Numbers, dates and durations
formatted with the culture and time zone (NodaTime). Charts as inline **SVG** from
`SvgChartRenderer` (deterministic, no JavaScript): axes, ticks, legends, colours from the
categorical palette, accessible `<title>`/`<desc>`, mirrored axes in RTL.

### 6.3 PDF

- `PdfRenderer` keeps a pool of 2 headless Chromium processes (`chrome-headless-shell` shipped in
  `<install>/chromium/`, version pinned per release), launched with `--headless`, `--no-sandbox`
  only on Linux containers where the sandbox is unavailable (otherwise sandbox on), no network:
  request interception aborts every request that is not `data:` or the in-memory document.
- `page.SetContentAsync(html)` then `PdfAsync` with page size/orientation, margins, header/footer
  templates (site name, page x/y, generation time); timeout 120 s; process recycled after 50
  renders or on crash.
- Fonts: bundled Noto Sans, Noto Sans Arabic, Noto Kufi (OFL) — no system font dependency.

### 6.4 XLSX and CSV

- XLSX: one sheet per table/chart data set (sheet names from block titles, ≤ 31 chars,
  deduplicated), header styles, typed cells (numbers, dates in the time zone with format),
  auto-filter, frozen header, column widths; a "Parameters" sheet; RTL sheets for Arabic.
- CSV: one file per data set (zip if several), RFC 4180, UTF-8 with BOM (option), culture-
  independent invariant numbers and ISO 8601 times with offset.

## 7. Standard templates and BI views (T5.06)

Standard templates (keys `std.*`): `std.alarmStatistics`, `std.operatorPerformance`,
`std.deviceAvailability`, `std.accessSummary`, `std.intrusionSummary`, `std.commandReport`,
`std.shiftReport`, `std.auditReport` — each with parameters (time range, scope) and layout
documented in `Standard/README.md`; labels in en/fr/ar; golden HTML snapshots in tests.

BI: migration creates schema `wp_bi` with views over HIS/AUD tables (`alarms`, `alarm_timeline`,
`events` without personal-data columns unless `Reports:Bi:IncludePersonalData`, `commands`,
`devices`, `locations`, `audit` summary), and role `watchpoint_bi` (`USAGE` on `wp_bi`, `SELECT` on
views only, `statement_timeout` 60 s, `CONNECTION LIMIT 10`). The administrator sets its password
in *System → Reports → BI access* (stored by PostgreSQL only; shown once). Views are a documented,
versioned contract (`v1_` prefix; breaking changes create `v2_` views).

## 8. Schedules and delivery (T5.07)

```csharp
public sealed record ReportSchedule(ReportScheduleId Id, LocalizedName Name, ReportTemplateId Template,
    IReadOnlyDictionary<string, JsonElement> Parameters, ScheduleTrigger Trigger, IReadOnlySet<ReportFormat> Formats,
    UserId Owner /* identity for rights */, string Culture, string TimeZone,
    IReadOnlyList<RecipientRef> Recipients /* NOT */, string? ExportFolder, bool Enabled, long Version);

public abstract record ScheduleTrigger
{
    public sealed record Cron(string Expression /* Cronos, 5 fields */) : ScheduleTrigger;
    public sealed record CalendarEdge(string CalendarKey, EdgeKind Edge) : ScheduleTrigger;   // end of shift
}
```

### 8.1 Relative ranges

Time range parameters accept relative values resolved at run time in the schedule's time zone:
`today`, `yesterday`, `previousHours:N`, `previousDays:N`, `previousWeek` (ISO), `previousMonth`,
`previousQuarter`, `previousYear`, `monthToDate`, `previousCalendarPeriod:<calendarKey>` (last
completed active period of a calendar — shift reports). DST-safe (NodaTime local dates).

### 8.2 Running and delivery

- `ScheduleRunner` uses SCH triggers (leader-only in M9). Missed runs (node down) are run once at
  start-up if missed by < 24 h, with the range of the missed time.
- Identity: the **owner's** current rights; if the owner is disabled or lacks a data source
  permission, the run fails with `reports.schedule.owner_rights` and a notification to the owner
  and to users with `config.reports.edit`.
- Delivery: NOT request kind `report.ready` to recipients: attachments if total ≤ 10 MB, else a
  link to the web client report-run page (recipients must sign in; external contacts only get
  attachments and are refused for templates with personal data —
  `reports.delivery.personal_data_external`).
- Export folder (optional, admin-configured allow-list of base paths `Reports:ExportFolders`):
  write `<template>/<yyyy>/<MM>/<name>_<from>_<to>.<ext>`; failures retried 3× then reported.

### 8.3 UI (T5.07)

- Operation side: **Reports** page — template list (by permission), run dialog (parameters
  form generated from parameter kinds), HTML preview (sandboxed iframe, `srcdoc`, CSP no
  scripts), downloads, my runs history, schedules list (own; all with `reports.schedule.manage`).
- Configuration side: template editor — block list with drag-and-drop, block property forms,
  data source picker with argument forms (schemas) and column pickers, live preview with sample
  parameters, duplicate standard template.

Permissions added to RGT catalogue: `reports.run` (None), `reports.schedule` (None — own
schedules), `reports.schedule.manage` (None), `config.reports.view/edit` (None),
`admin.bi.manage` (None).

## 9. Data and API

Tables `wp_reports`: `templates`, `schedules`, `runs` (id, template_id, version, parameters,
identity, formats, status, started_at, finished_at, outputs jsonb (file ids), warnings,
error), `schedule_state` (schedule_id, last_run_at, next_run_at).

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/report-templates`, `{id}` | `reports.run` | Templates the user can run (all data sources permitted) |
| `POST/PATCH/DELETE /api/v1/report-templates…` | `config.reports.edit` | CFG-backed |
| `GET /api/v1/report-data-sources` | `config.reports.edit` | Catalogue with schemas |
| `POST /api/v1/reports/preview` | `reports.run` | HTML preview (200 rows) |
| `POST /api/v1/reports/runs` | `reports.run` | → job; result = run with outputs |
| `GET /api/v1/reports/runs?mine=` / `{id}` | `reports.run` (own) / `admin.audit.view` (all) | |
| `GET/POST/PATCH/DELETE /api/v1/report-schedules…` | `reports.schedule` (own) / `reports.schedule.manage` | |
| `POST /api/v1/report-schedules/{id}/run-now` | same | |
| `POST /api/v1/reports/bi/password` | `admin.bi.manage` | Set BI role password |

## 10. Errors, observability

Errors: `reports.template.invalid`, `reports.template.not_found`, `reports.datasource.unknown`,
`reports.datasource.forbidden` (args source), `reports.parameter.invalid`, `reports.timeout`,
`reports.render.failed`, `reports.schedule.owner_rights`, `reports.delivery.personal_data_external`,
`reports.export.folder_not_allowed`.

| EventId | Level | Message |
|---|---|---|
| 3250 | Information | Report {Template} run {RunId} by {Identity}: {Formats} in {ElapsedMs} ms ({Rows} rows) |
| 3251 | Warning | Report run {RunId} failed: {Error} |
| 3260 | Warning | Chromium process recycled ({Reason}) |
| 3270 | Information | Scheduled report {Schedule} delivered to {Recipients} recipients |

Metrics: `watchpoint.reports.runs` (counter: template, outcome), `watchpoint.reports.duration`
(histogram), `watchpoint.reports.pdf.pool.busy` (gauge).

## 11. Tests

| Id | Test |
|---|---|
| RPT-U-001 | Template validation: sources, arguments, columns, chart field types, parameters, nesting |
| RPT-U-002 | Parameter resolution and validation incl. defaults |
| RPT-U-003 | Data binding deduplication and parallel execution limit |
| RPT-U-004 | Each data source: argument schema, output columns, permission declared (catalogue test) |
| RPT-U-005 | alarms.stats from aggregates matches raw computation fixture |
| RPT-U-006 | operators.performance computations (mean, p90) |
| RPT-U-007 | devices.availability offline durations across range boundaries |
| RPT-U-008 | access.summary masking without personal data permission |
| RPT-U-009 | intrusion.summary late/missed arming vs calendar |
| RPT-U-010 | Rights: identity scope filters every data source |
| RPT-U-011 | Data source forbidden → error naming the source |
| RPT-U-012 | Row limits and truncation warnings |
| RPT-U-013 | Timeouts per source and per report |
| RPT-U-014 | HTML renderer encodes values; Markdown safe mode |
| RPT-U-015 | Culture/time-zone formatting en/fr/ar; RTL document |
| RPT-U-016 | SVG charts: bar, stacked, line, area, pie, heatmap golden SVGs; RTL mirroring; accessibility titles |
| RPT-U-017 | KPI comparison to previous period |
| RPT-U-018 | Group blocks repeat per value |
| RPT-U-019 | PDF renderer: network requests blocked (test page referencing external URL) |
| RPT-U-020 | PDF pool recycling after N renders and on crash |
| RPT-U-021 | PDF page setup, header/footer, fonts embedded (Arabic shaping) |
| RPT-U-022 | XLSX: sheets naming, typed cells, filters, frozen header, parameters sheet, RTL |
| RPT-U-023 | CSV: RFC 4180 quoting, BOM option, zip for several data sets |
| RPT-U-024 | Outputs stored in FIL with metadata and retention |
| RPT-U-025 | Run records and audit for sensitive templates |
| RPT-U-026 | Standard templates validate and render golden HTML snapshots |
| RPT-U-027 | Standard template labels coverage en/fr/ar |
| RPT-U-028 | Preview limited to 200 rows |
| RPT-U-029 | BI views migration and role grants (SQL text tests) |
| RPT-U-030 | Template CFG object type (validate/prepare/complete) |
| RPT-U-031 | Relative ranges for each kind incl. DST transitions |
| RPT-U-032 | previousCalendarPeriod with SCH fake calendar |
| RPT-U-033 | Cron and calendar edge triggers |
| RPT-U-034 | Missed runs caught up once (< 24 h) |
| RPT-U-035 | Owner rights check and failure notification |
| RPT-U-036 | Delivery: attachments ≤ 10 MB else link |
| RPT-U-037 | External contacts refused for personal data templates |
| RPT-U-038 | Export folder allow-list, path pattern, retries |
| RPT-U-039 | Schedules API own vs manage permission |
| RPT-U-040 | Run-now |
| RPT-U-041 | UI: run dialog parameters form per kind |
| RPT-U-042 | UI: preview iframe sandboxed (no scripts) |
| RPT-U-043 | UI: template editor block operations and validation display |
| RPT-U-044 | UI: data source picker with argument forms |
| RPT-U-045 | UI: schedules list and editor |
| RPT-I-001 | Seeded history (1 month simulator data) → each standard template in all formats; values match SQL reference queries |
| RPT-I-002 | PDF generation with real Chromium in CI (Linux) for Arabic and French reports; text extractable |
| RPT-I-003 | BI role can read views, cannot read base tables or write |
| RPT-I-004 | Scheduled daily shift report delivered by e-mail (Mailpit) with PDF attachment |
| RPT-I-005 | Performance: alarm statistics for 1 year, 5,000 devices in < 30 s |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T5.06 | §4–§7, §9–§10 | RPT-U-001…RPT-U-030, RPT-I-001…RPT-I-003 |
| T5.07 | §8 | RPT-U-031…RPT-U-045, RPT-I-004, RPT-I-005 |
