# EVT — Events and Event Journal

| | |
|---|---|
| Spec id | EVT |
| Projects | `src/service/Watchpoint.Events` |
| Milestone | M2 |
| Tasks | T2.05 |
| Design docs | [Events and alarms §1](../../docs/modules/events-and-alarms.md#1-events) |

## 1. Purpose and scope

- One **event model** for the journal: device events (from connectors), health events (derived
  from health changes), system events (from modules), operator notes, and optionally alarm and
  command activity.
- A **system event catalogue** modules register into, and a publisher.
- **Journal** queries (history through HIS) and the live `events` topic, with personal-data
  masking.

Not in scope: storage tables (HIS), alarm rules (ALM).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, MOD (event definitions, translations), REG (source snapshot), PIP (subscriber, records, consumers), HIS (`IHistoryStore` event queries), API | ALM (triggers on events), AUT, SCR, UIO, RPT, NOT, all modules publishing system events |

## 3. Project layout

```
Watchpoint.Events/
├── Contracts/
│   ├── JournalEvent.cs, EventSource.cs, EventSeverity.cs
│   ├── ISystemEventCatalogue.cs, SystemEventDefinition.cs
│   ├── ISystemEventPublisher.cs
│   ├── IEventJournal.cs, EventQuery.cs
│   └── EventErrors.cs
├── Internal/
│   ├── HealthEventDeriver.cs        IPartitionSubscriber (Order 150)
│   ├── JournalProjector.cs          records → JournalEvent DTOs (enrichment snapshot, title message)
│   ├── PersonalDataMasker.cs
│   ├── OperatorNotes.cs
│   ├── Live/EventsTopic.cs
│   └── Export/EventExportJob.cs
├── EventsModule.cs
└── EventsLog.cs                     event ids 2500–2549
```

## 4. Model

```csharp
public enum EventCategory { Device, Health, System, Operator, Alarm, Command }

public sealed record JournalEvent(
    Guid Id,                          // envelope id
    long Sequence,                    // log sequence (WP_LOG) or 0 + stream marker for WP_SYS
    string Kind,                      // "doorPosition.forced", "health.wentOffline", "identity.accountLocked", "operator.note", "alarm.raised"…
    EventCategory Category,
    Severity Severity,
    DateTimeOffset? SourceTime,
    DateTimeOffset ReceivedTime,
    EventSource? Source,              // device/server snapshot at the time
    RecordOrigin Origin,
    IReadOnlyDictionary<string, WpValue> Payload,
    Message Title,                    // translatable title with args
    RecordCorrelation Correlation,
    IReadOnlyList<string> PersonalDataFields);

public sealed record EventSource(Guid EntityId, EntityKind Kind, string DisplayName, TypeRef Type,
    LocationId LocationId, IReadOnlyList<string> LocationNames, ServerId? ServerId, IReadOnlyList<string> TagKeys);
```

Title rules: device events use the capability event title key
`capabilities.<cap>.events.<evt>.title` with payload fields and `device` (display name) as
arguments; system events use their registered key; operator notes use `events.operator.note`
with `text`.

## 5. Health events (derived)

`HealthEventDeriver` (partition subscriber, Order 150) watches `health` records and emits
follow-up `event` records:

| Transition | Event |
|---|---|
| connectivity → `offline` (from anything else) | `health.wentOffline` (severity offline; payload `reason`) |
| `offline`/`unknown` → `online`/`degraded` | `health.cameOnline` (info; payload `downtime` duration if known) |
| fault code added | `health.faultRaised` (fault; payload `fault`) |
| fault code removed | `health.faultCleared` (info; payload `fault`) |

Transitions caused by `ServerUnavailable` (server offline) are **not** duplicated per device: one
`health.wentOffline` for the server only (devices get no event — avoids floods of thousands of
events when a server drops).

## 6. System events

```csharp
public sealed record SystemEventDefinition(string Kind /* "<module>.<name>" lowercase camel */, Severity Severity,
    ParameterSchema Payload, string TitleKey, IReadOnlyList<string> PersonalDataFields, bool DeviceBound);

public interface ISystemEventCatalogue
{
    void Register(SystemEventDefinition definition);           // at module start-up (phase Catalogue)
    IReadOnlyList<SystemEventDefinition> All { get; }
}

public interface ISystemEventPublisher
{
    // Device-bound events go to the device's partition (WP_LOG); others to WP_SYS.
    ValueTask PublishAsync(string kind, IReadOnlyDictionary<string, WpValue> payload, Guid? relatedEntity,
        RecordOrigin origin, CancellationToken ct);
}
```

Record kind for system events: `sysevent` with body `{ kind, payload, severity?, personalFields? }` (the catalogue's severity and `PersonalDataFields`, so that HIS can split personal data without the catalogue; body type `SysEventBody`, HIS §5). Initial catalogue
(registered by the owning modules; translations in their bundles):

| Kind | Module | Severity |
|---|---|---|
| `identity.signedIn`, `identity.signInFailed`, `identity.accountLocked`, `identity.breakGlassUsed` | IDN | info / warning / warning / alarm |
| `integrations.connectionStateChanged`, `integrations.connectionFaulted`, `integrations.packageInstalled` | IRT | info / fault / info |
| `pipeline.flooding` | PIP | warning |
| `host.childProcessFailed` | HST | fault |
| `configuration.published`, `configuration.rolledBack` | CFG (M3) | info |
| `maintenance.started`, `maintenance.ended` | MNT (M3) | warning / info |
| `licence.*` | LIC (M7) | various |
| `cluster.*` | CLU (M9) | various |

## 7. Journal

```csharp
public sealed record EventQuery(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<EventCategory>? Categories,
    IReadOnlyList<string>? Kinds /* exact or prefix "doorPosition.*" */, Severity? MinSeverity,
    IReadOnlyList<LocationId>? LocationsSubtree, IReadOnlyList<ServerId>? Servers, IReadOnlyList<Guid>? Entities,
    IReadOnlyList<TagId>? Tags, string? PersonRef /* holderRef / credentialRef */, UserId? Operator,
    string? Text /* payload text search, notes */, bool IncludeAlarmActivity, bool IncludeCommandActivity,
    IVisibleScope Scope, bool Descending = true);

public interface IEventJournal
{
    Task<Page<JournalEvent>> QueryAsync(EventQuery query, PageRequest page, CancellationToken ct);   // via HIS
    Task<Result<JournalEvent>> GetAsync(Guid eventId, CancellationToken ct);
    Task<Result<Guid>> AddOperatorNoteAsync(OperatorNoteRequest request, CancellationToken ct);
}
public sealed record OperatorNoteRequest(UserId Author, string Text /* 1–2,000 */, DeviceId? Device, LocationId? Location,
    IReadOnlyList<FileId>? Attachments /* M5 */);
```

- Query range limit: 31 days per request (`events.query.range_too_large`), cursor paging,
  `heavy` rate limit; results ordered by received time.
- Scope: the caller's visible scope restricts sources (events without source — system events —
  require `admin.systemEvents.view` or are filtered).
- Operator note → record `operator.note` (body `{ text, location?, attachments? }`) appended to the
  device partition if a device is given, else WP_SYS.

### 7.1 Personal data masking

`PersonalDataMasker` replaces fields listed in `PersonalDataFields` with
`{ "k":"str","v":"•••" }` and adds `"masked": true`, unless the caller holds
`operate.personalData.view` (scope-aware). Masking happens in the API layer (journal DTO, live
topic, exports). Person reference filters (`PersonRef`) require the permission too.

## 8. Live topic `events`

- Filter: same fields as `EventQuery` without time range, plus `"includeHealth": bool`.
- Source: in-process subscriber on partition records of kinds `event`, `sysevent` (device-bound),
  `operator.note`, and (when included) `alarm.*`, `command.*`; plus a WP_SYS ordered consumer for
  global records. Each record projected to `JournalEvent` and matched against subscriptions
  (index by kind and location).
- Snapshot on subscribe: the last 200 matching events from HIS (most recent first), then live
  deltas; resume supported within the log retention (sequence = WP_LOG sequence; WP_SYS events
  carry a separate sequence namespace `s:<n>` and are not resumable — the snapshot covers them).
- Not coalescable.

## 9. API

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/events?from=&to=&category=&kind=&minSeverity=&location=&server=&entity=&tag=&person=&operator=&text=&alarms=&commands=&limit=&cursor=` | `operate.events.view` + scope | Journal query |
| `GET /api/v1/events/{id}` | same | One event |
| `POST /api/v1/events/notes` | `operate.notes.create` | Operator note |
| `POST /api/v1/events/export` | `operate.events.export` | `{ query, format: "csv" | "xlsx" }` → job (API §5.6) producing a file (FIL in M5; M2: temporary file served by the job endpoint for 1 h) |
| `GET /api/v1/event-types` | authenticated | Device event definitions + system event catalogue with title keys and severities |

## 10. Errors, observability

Errors: `events.query.range_too_large`, `events.not_found`, `events.note.too_long`,
`events.export.too_many_rows` (limit 1,000,000 rows).

| EventId | Level | Message |
|---|---|---|
| 2501 | Information | System event catalogue: {Count} definitions |
| 2510 | Debug | Derived {Kind} for {EntityId} |
| 2520 | Information | Export {JobId} completed: {Rows} rows |

Metrics: `watchpoint.events.recorded` (counter by `category`, `severity`),
`watchpoint.events.live.subscriptions` (gauge).

## 11. Tests

| Id | Layer | Test |
|---|---|---|
| EVT-U-001 | U | Device event record projected with source snapshot and title message |
| EVT-U-002 | U | Health offline transition derives `health.wentOffline` once |
| EVT-U-003 | U | Coming back online derives `cameOnline` with downtime |
| EVT-U-004 | U | Fault added/removed derive raised/cleared per fault code |
| EVT-U-005 | U | Server offline: single server event, no per-device events |
| EVT-U-006 | U | System event registration and publishing to WP_SYS vs device partition |
| EVT-U-007 | U | Unknown system event kind publish → argument exception |
| EVT-U-008 | U | Payload validated against the definition |
| EVT-U-009 | U | Operator note to device partition / to WP_SYS |
| EVT-U-010 | U | Note length limit |
| EVT-U-011 | U | Query range limit 31 days |
| EVT-U-012 | U | Query filters map to HIS query |
| EVT-U-013 | U | Scope filtering; system events require permission |
| EVT-U-014 | U | Personal data masked without permission; visible with permission |
| EVT-U-015 | U | Person filter requires permission |
| EVT-U-016 | U | Live topic matching by kind prefix and location subtree |
| EVT-U-017 | U | Live snapshot = last 200 from HIS then deltas |
| EVT-U-018 | U | Alarm/command activity included only when requested |
| EVT-U-019 | U | Title for Arabic culture with bidi-isolated device name (formatting in UI; server provides key+args) |
| EVT-U-020 | U | Event types endpoint lists device and system definitions |
| EVT-I-001 | I | Simulator events appear in live topic and in history |
| EVT-I-002 | I | Health events derived end-to-end (simulator device offline) |
| EVT-I-003 | I | Journal query with cursor over 10,000 events |
| EVT-I-004 | I | Export CSV job content and masking |
| EVT-I-005 | I | Export XLSX opens (ClosedXML read-back) |
| EVT-I-006 | I | Resume of live topic after reconnect |
| EVT-I-007 | I | IDN sign-in failures appear as system events |
| EVT-I-008 | I | Operator note via API visible in journal |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T2.05 | All | EVT-U-001…EVT-U-020, EVT-I-001…EVT-I-008 |
