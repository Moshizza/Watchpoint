# MNT — Maintenance Mode (operation suspended)

| | |
|---|---|
| Spec id | MNT |
| Projects | `src/service/Watchpoint.Maintenance` |
| Milestone | M3 |
| Tasks | T3.09 |
| Design docs | [Desktop client §6](../../docs/clients/desktop-client.md#6-suspending-operation-maintenance-mode), [Users and rights §6](../../docs/modules/users-and-rights.md#6-maintenance-mode-gate-operation-suspended) |

## 1. Purpose and scope

**System maintenance mode** suspends *operation* (desktop operation side, web client, mobile
app) for a whole site or for selected locations, while event processing, history and
configuration continue. It has a reason, an initiator, an expected end, optional auto-end,
reminders, a banner for configurators, system events and audit.

Not in scope: **device maintenance** (the `maintenance` flag on devices, REG/MOD/ALM) — it
does not affect users.

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, IDN (`ISessionAdmission`, `ISessionRegistry.RevokeWhereAsync`), RGT (`IRightsEvaluator`, permissions `config.maintenance`, `operate.duringMaintenance`; location scope filter), REG (location subtree), PIP (`WP_SYS` `maintenance.changed`), EVT (system events `maintenance.started/ended`), AUD, SCH (timers via `TimeProvider`), CFG (policy settings document `system.maintenancePolicy`), STO | API (live `system` topic, `Closing("maintenance")`), ALM/NOT (notification policy), AUT/SCR (pause policy, M8), UIF (dialog and banner), UIO (maintenance screen), RPT (maintenance periods, M5) |

## 3. Layout

```
Watchpoint.Maintenance/
├── Contracts/
│   ├── MaintenancePeriod.cs, MaintenanceScope.cs, MaintenancePolicy.cs
│   ├── IMaintenanceService.cs, IMaintenanceStatus.cs
│   └── MaintenanceErrors.cs
├── Internal/
│   ├── MaintenanceService.cs          start/extend/end, timers, reminders
│   ├── MaintenanceAdmission.cs        ISessionAdmission implementation
│   ├── MaintenanceScopeFilter.cs      contributes to RGT visible scope (location mode)
│   ├── ImpactCalculator.cs            "who will be locked out, what still runs"
│   └── Storage/MaintenanceDbContext.cs
├── MaintenanceModule.cs
└── MaintenanceLog.cs                  event ids 3750–3799
```

## 4. Contracts

```csharp
public sealed record MaintenanceScope(bool WholeSite, IReadOnlyList<LocationId>? Locations /* subtrees; null when WholeSite */);

public sealed record MaintenancePeriod(Guid Id, MaintenanceScope Scope, string Reason, UserId StartedBy, DateTimeOffset StartedAt,
    DateTimeOffset ExpectedEnd, bool AutoEnd, MaintenanceNotificationMode Notifications, bool PauseNonSafetyAutomations,
    UserGroupId? NotifyGroup, DateTimeOffset? EndedAt, UserId? EndedBy, MaintenanceEndReason? EndReason, long Version);

public enum MaintenanceNotificationMode { Continue /* default */, Suppress, CriticalOnly }
public enum MaintenanceEndReason { Manual, AutoEnded, Superseded }

public sealed record StartMaintenanceRequest(MaintenanceScope Scope, string Reason, DateTimeOffset ExpectedEnd, bool AutoEnd = true,
    MaintenanceNotificationMode Notifications = MaintenanceNotificationMode.Continue, bool PauseNonSafetyAutomations = false,
    UserGroupId? NotifyGroup = null, string ImpactConfirmationToken = "" /* from PreviewImpact */);

public interface IMaintenanceService
{
    Task<Result<MaintenanceImpact>> PreviewImpactAsync(MaintenanceScope scope, UserId actor, CancellationToken ct);
    Task<Result<MaintenancePeriod>> StartAsync(StartMaintenanceRequest request, UserId actor, CancellationToken ct);
    Task<Result<MaintenancePeriod>> ExtendAsync(Guid id, DateTimeOffset newExpectedEnd, string? reason, UserId actor, long expectedVersion, CancellationToken ct);
    Task<Result<MaintenancePeriod>> EndAsync(Guid id, UserId actor, CancellationToken ct);
    Task<Page<MaintenancePeriod>> HistoryAsync(DateTimeOffset from, DateTimeOffset to, PageRequest page, CancellationToken ct);
}

public interface IMaintenanceStatus      // hot path, in-memory, updated from WP_SYS on every node
{
    IReadOnlyList<MaintenancePeriod> Active { get; }
    bool IsSiteWide { get; }
    bool IsLocationSuspended(LocationId location);     // subtree check via REG location index
    bool IsDeviceSuspended(DeviceId device);           // device's location suspended
    event Action<MaintenanceChange>? Changed;
}

public sealed record MaintenanceImpact(int OperationSessionsToClose, IReadOnlyList<ImpactedUser> UsersLockedOut,
    IReadOnlyList<ImpactedUser> UsersKeepingAccess /* configurators, duringMaintenance holders */,
    int DevicesAffected, int OpenAlarmsAffected, IReadOnlyList<string> StillRunning /* localisation keys */, string ConfirmationToken /* valid 10 min */);
```

## 5. Behaviour

### 5.1 Start

1. Permission `config.maintenance` (site-wide). Reason required (≥ 5 characters),
   `ExpectedEnd` in (now + 5 min, now + 30 days], `maintenance.expected_end_invalid` otherwise.
2. `ImpactConfirmationToken` must come from a `PreviewImpactAsync` call by the same actor for
   the same scope within 10 min (`maintenance.confirmation_required`) — the UI confirmation
   dialog states who will be locked out and what still runs.
3. Overlap rules: at most one **site-wide** period; location periods may coexist; starting a
   site-wide period while location periods are active ends them with reason `Superseded`.
   A location period covering a location already covered → `maintenance.already_active`.
4. Persist, publish `maintenance.changed { period }` on `WP_SYS`, raise system event
   `maintenance.started` (EVT, severity warning, includes scope, reason, initiator, expected end),
   audit `maintenance.started` (Sensitive), notify `NotifyGroup` (NOT hook; M5 — before M5 the
   system event is the notification).
5. Every node's `IMaintenanceStatus` applies the change, then:
   - **Site-wide**: `RevokeWhereAsync(s => s.Side == Operation && !Exempt(s.User))` with reason
     `maintenance`; API sends `Closing("maintenance")` to those live connections.
   - **Location**: no session closed; RGT visible scopes of non-exempt users exclude the
     suspended subtrees (`MaintenanceScopeFilter` registered as an RGT scope post-filter), which
     triggers `rights.changed` → live resync; commands on suspended devices are rejected by CMD
     (`maintenance.active`).

`Exempt(user)` = administrator, or holds `operate.duringMaintenance`. Configuration-side
sessions are never closed.

### 5.2 Admission

`MaintenanceAdmission : ISessionAdmission` (IDN §sign-in step 6): when a site-wide period is
active and the requested session is operation side (desktop operation, web, mobile) and the
user is not exempt → `maintenance.active` with args `{ startedBy, reason, expectedEnd }`
(rendered by UIO's full-page message). Location periods never refuse sign-in.

### 5.3 What continues

Connections, event/state processing and history are unaffected. Alarms are still raised and
recorded; `IMaintenanceStatus` exposes `NotificationMode` for NOT (M5) and ALM sound/toast
policy: `Suppress` → no external notifications, `CriticalOnly` → only priority ≥ critical
threshold. `PauseNonSafetyAutomations` read by AUT (M8): automations flagged `safety` keep
running.

### 5.4 End, extend, auto-end, reminders

- `EndAsync` by any `config.maintenance` holder; `ExtendAsync` changes `ExpectedEnd` (same
  bounds), audit `maintenance.extended`.
- Timers (per active period, rescheduled on change and at start-up from the database; only the
  **leader node** — M9 lease `maintenance` — fires them; single node: always):
  reminders to the initiator (and NotifyGroup) at 15 min and 5 min before `ExpectedEnd`
  (live `Notice` + notification), then at `ExpectedEnd`: if `AutoEnd` → end with `AutoEnded`,
  else system event `maintenance.overdue` every 30 min until ended.
- End → `maintenance.changed`, system event `maintenance.ended`, audit, sessions may sign in
  again (clients showing the maintenance screen retry automatically on the `system` topic
  change — they keep an unauthenticated status poll `GET /api/v1/system/status` every 30 s).

### 5.5 Banner

Live `system` topic carries `maintenance { active: [ { id, scope, reason, startedBy, expectedEnd } ] }`;
UIF shows a banner to configurators and exempt users; UIO shows the full-page message for
others (site-wide) or badges suspended locations (location mode).

## 6. Data (`wp_maintenance`)

| Table | Columns |
|---|---|
| `periods` | id, scope jsonb, reason, started_by, started_at, expected_end, auto_end, notifications smallint, pause_automations, notify_group, ended_at, ended_by, end_reason, row_version — index (ended_at) where ended_at is null |
| `impact_tokens` | token, actor, scope_hash, expires_at |

## 7. API

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/system/status` | anonymous | `{ maintenance: { active, startedBy, reason, expectedEnd } | null, version }` — no personal data beyond the initiator display name; rate-limited |
| `GET /api/v1/maintenance` | `config.maintenance` or `admin.health.view` | Active periods |
| `POST /api/v1/maintenance/impact` | `config.maintenance` | `MaintenanceScope` → impact |
| `POST /api/v1/maintenance` | `config.maintenance` | Start |
| `PATCH /api/v1/maintenance/{id}` | `config.maintenance` | Extend (If-Match) |
| `POST /api/v1/maintenance/{id}/end` | `config.maintenance` | End |
| `GET /api/v1/maintenance/history?from=&to=` | `admin.audit.view` or `config.maintenance` | Periods (reports) |

## 8. Settings (`system.maintenancePolicy` CFG document)

| Key | Default |
|---|---|
| `defaultNotificationMode` | `Continue` |
| `defaultAutoEnd` | true |
| `reminderOffsets` | [15 min, 5 min] |
| `maxDuration` | 30 days |
| `defaultNotifyGroup` | null |

## 9. Errors

`maintenance.active` (args startedBy, reason, expectedEnd), `maintenance.already_active`,
`maintenance.expected_end_invalid`, `maintenance.reason_required`,
`maintenance.confirmation_required`, `maintenance.not_found`, `maintenance.already_ended`.

## 10. Observability

| EventId | Level | Message |
|---|---|---|
| 3750 | Warning | Maintenance {Id} started by {User} scope {Scope} until {ExpectedEnd}: {Reason} |
| 3751 | Information | Maintenance {Id} extended to {ExpectedEnd} |
| 3752 | Information | Maintenance {Id} ended ({EndReason}) |
| 3753 | Information | Closed {Count} operation sessions for maintenance {Id} |
| 3760 | Warning | Maintenance {Id} overdue since {ExpectedEnd} |

Metric: `watchpoint.maintenance.active` (gauge, tag `scope` = site|location).

## 11. Tests

| Id | Layer | Test |
|---|---|---|
| MNT-U-001 | U | Start validation: permission, reason, expected end bounds |
| MNT-U-002 | U | Confirmation token required, bound to actor and scope, expires |
| MNT-U-003 | U | Impact calculation: sessions to close, users locked out vs keeping access, devices and alarms affected |
| MNT-U-004 | U | Overlap: single site-wide; site-wide supersedes location periods; duplicate location rejected |
| MNT-U-005 | U | Site-wide start revokes non-exempt operation sessions only (configuration sessions kept) |
| MNT-U-006 | U | Exempt: administrators and `operate.duringMaintenance` holders |
| MNT-U-007 | U | Admission refuses operation sign-in with args; allows configuration side and exempt users |
| MNT-U-008 | U | Location mode: no sign-in refusal; visible scope excludes suspended subtrees; rights.changed raised |
| MNT-U-009 | U | Location mode: commands on suspended devices rejected with maintenance.active |
| MNT-U-010 | U | IMaintenanceStatus subtree checks and device lookup |
| MNT-U-011 | U | Notification mode exposed (Continue/Suppress/CriticalOnly) |
| MNT-U-012 | U | Extend changes timers and audits |
| MNT-U-013 | U | Reminders at configured offsets (FakeTimeProvider) |
| MNT-U-014 | U | Auto-end at expected end; overdue events when auto-end off |
| MNT-U-015 | U | Timers restored from database after restart |
| MNT-U-016 | U | Only lease holder fires timers (single node always) |
| MNT-U-017 | U | Start/end publish maintenance.changed, system events and audit records |
| MNT-U-018 | U | Anonymous status endpoint content and rate limit |
| MNT-U-019 | U | Live system topic payload for banner |
| MNT-U-020 | U | History query for reports |
| MNT-I-001 | I | Operator connected on web and desktop operation: site-wide start closes both with Closing("maintenance"); sign-in refused; end → sign-in works |
| MNT-I-002 | I | Configurator keeps configuration side and can publish during maintenance |
| MNT-I-003 | I | Events and alarms keep being processed and recorded during maintenance |
| MNT-I-004 | I | Location maintenance on Building C: operator loses Building C devices/alarms live, rest unaffected |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T3.09 | all | MNT-U-001…MNT-U-020, MNT-I-001…MNT-I-004 |
