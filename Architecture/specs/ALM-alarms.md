# ALM — Alarms

| | |
|---|---|
| Spec id | ALM |
| Projects | `src/service/Watchpoint.Alarms` |
| Milestone | M2 |
| Tasks | T2.09 (§4–§6), T2.10 (§7–§9), T2.11 (§10–§13), T2.12 (§14–§15) |
| Design docs | [Events and alarms](../../docs/modules/events-and-alarms.md) |

## 1. Purpose and scope

Alarm **rules** (separate from devices), the **evaluation engine** on the pipeline, the alarm
**lifecycle** with operator actions, start/end **delays**, **composite** rules, **escalation**,
**procedures**, **audience**, **default rules** from device types, and alarm **actions**.

Not in scope: notifications delivery (NOT, M5 — ALM calls a hook), alarm history storage (HIS),
UI (UIO), incidents grouping UI (later; the model field exists), handover between sites (FED).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, MOD, REG, STA, PIP (subscriber Order 300, dispatcher, side effects), EXP, SCH, CMD (actions), EVT (system events), STO (snapshots), RGT (M3: visibility, permissions) | API/UIO, NOT, AUT (alarm triggers), HIS, RPT, FED, MOB |

## 3. Project layout

```
Watchpoint.Alarms/
├── Contracts/
│   ├── AlarmRule.cs, RuleScope.cs, RuleTrigger.cs, LifecyclePolicy.cs, EscalationStep.cs, AlarmActionSpec.cs, RuleOverride.cs
│   ├── Alarm.cs, AlarmState.cs, AlarmOwner.cs, AlarmRecords.cs
│   ├── Procedure.cs, ProcedureStep.cs
│   ├── IAlarmRuleStore.cs, IAlarmService.cs, IAlarmQuery.cs, IAlarmNotificationHook.cs
│   ├── PriorityBands.cs
│   └── AlarmErrors.cs
├── Internal/
│   ├── Rules/RuleCompiler.cs, RuleIndex.cs, ScopeResolver.cs, TemplateInstaller.cs, OverrideResolver.cs
│   ├── Engine/AlarmSubscriber.cs (IPartitionSubscriber, Order 300), EvaluationState.cs, CompositeWindows.cs, DelayTimers.cs
│   ├── Lifecycle/AlarmStateMachine.cs, AlarmStore.cs (per partition), OperatorActions.cs
│   ├── Escalation/EscalationScheduler.cs
│   ├── Actions/AlarmActionRunner.cs
│   ├── Audience/AudienceResolver.cs
│   ├── Live/AlarmsTopic.cs
│   └── Storage/AlarmsDbContext.cs (rules, overrides, procedures, settings)
├── AlarmsModule.cs
└── AlarmsLog.cs                      event ids 2550–2699 (2550 shared with EXP errors)
```

## 4. Rules (T2.09)

### 4.1 Document

```csharp
public sealed record AlarmRule
{
    public required AlarmRuleId Id { get; init; }
    public required string Key { get; init; }                 // ^[a-z][a-z0-9.-]{1,99}$ unique; templates: "std.ac.door.forced"
    public required LocalizedName Name { get; init; }
    public string? DescriptionKeyOrText { get; init; }
    public bool Enabled { get; init; } = true;
    public string? TemplateKey { get; init; }                 // set when created from a type template
    public required RuleScope Scope { get; init; }
    public required RuleTrigger Trigger { get; init; }
    public string? Condition { get; init; }                   // extra CEL condition (bool)
    public string? ActiveCalendar { get; init; }              // rule active only when calendar active
    public bool ActiveCalendarInverted { get; init; }
    public required int Priority { get; init; }               // 1 (highest) … 1000
    public IReadOnlyList<CalendarPriority> PriorityByCalendar { get; init; } = [];   // first active calendar wins
    public TimeSpan StartDelay { get; init; }                 // 0 … PT24H
    public TimeSpan EndDelay { get; init; }
    public required LifecyclePolicy Lifecycle { get; init; }
    public ProcedureId? Procedure { get; init; }
    public IReadOnlyList<AlarmActionSpec> Actions { get; init; } = [];
    public IReadOnlyList<EscalationStep> Escalation { get; init; } = [];
    public AlarmAudience Audience { get; init; } = AlarmAudience.Everyone;
    public AlarmDisplay Display { get; init; } = new();
    public long Version { get; init; }
}

public sealed record RuleScope(
    IReadOnlyList<DeviceId>? Devices, IReadOnlyList<TypeId>? Types /* IsA */, IReadOnlyList<Category>? Categories,
    IReadOnlyList<LocationId>? LocationsSubtree, IReadOnlyList<ServerId>? Servers,
    IReadOnlyList<TagId>? TagsAll, IReadOnlyList<TagId>? TagsAny, IReadOnlyList<TagId>? TagsExclude,
    bool System /* rule on system events (no devices) */);
// Non-null lists are ANDed; within a list ORed. Empty scope (all null, System=false) = invalid.

public abstract record RuleTrigger
{
    public sealed record OnEvent(IReadOnlyList<string> Kinds /* "doorPosition.forced", "accessEvents.denied", "health.wentOffline", "sysevent:identity.accountLocked" */,
        string? PayloadCondition /* CEL with `event` */, EndMode End) : RuleTrigger;
    public sealed record OnState(string Condition /* CEL bool with `device`; alarm while true */) : RuleTrigger;
    public sealed record OnHealth(IReadOnlyList<Connectivity> Connectivity, IReadOnlyList<string> AnyFaults) : RuleTrigger;  // alarm while matching
    public sealed record OnCount(IReadOnlyList<string> Kinds, string? PayloadCondition, int Count /* 2–1000 */, TimeSpan Within, bool PerDevice, EndMode End) : RuleTrigger;
    public sealed record OnSequence(IReadOnlyList<SequenceStep> Steps /* 2–5 */, TimeSpan Within, bool SameDevice, EndMode End) : RuleTrigger;
}
public sealed record SequenceStep(IReadOnlyList<string> Kinds, string? PayloadCondition, bool Absent /* "and no X" step */);

public abstract record EndMode
{
    public sealed record Immediate : EndMode;                                       // resolved right after raising (event alarms)
    public sealed record OnEvent(IReadOnlyList<string> Kinds) : EndMode;            // e.g. forced → closedAgain
    public sealed record OnCondition(string Condition) : EndMode;                   // CEL on device: resolved when true
    public sealed record Manual : EndMode;                                          // only operator close
}

public sealed record LifecyclePolicy(bool AcknowledgeRequired = true, bool ReportRequired = false,
    bool AutoCloseWhenResolvedAndAcknowledged = true, bool AllowCloseWhileActive = false,
    RepeatBehaviour Repeat = RepeatBehaviour.Counter, TimeSpan? MaxShelve = null);
public enum RepeatBehaviour { Counter, NewAlarm }

public sealed record EscalationStep(TimeSpan After, EscalationWhen When, int? RaisePriorityTo,
    IReadOnlyList<UserGroupId> NotifyGroups, bool LouderSound, int? RepeatCountThreshold);
public enum EscalationWhen { NotAcknowledged, NotResolved, NotClosed, RepeatedCount }

public sealed record AlarmActionSpec(AlarmActionOn On, AlarmActionKind Kind, JsonElement Parameters);
public enum AlarmActionOn { Raised, Acknowledged, Resolved, Closed, Escalated }
public enum AlarmActionKind { Command, Automation, Notify, Snapshot, Popup, Bookmark, Script }

public sealed record AlarmAudience(IReadOnlyList<UserGroupId> UserGroups, IReadOnlyList<WorkstationId> Workstations)
{ public static readonly AlarmAudience Everyone = new([], []); }
public sealed record AlarmDisplay(string? Sound = null, bool? Popup = null, string? Color = null);

public sealed record RuleOverride(Guid Id, AlarmRuleId Rule, RuleScope AppliesTo /* devices/locations/tags */,
    bool? Enabled, int? Priority, TimeSpan? StartDelay, TimeSpan? EndDelay, ProcedureId? Procedure, long Version);
```

### 4.2 Validation (compile)

`RuleCompiler` compiles every CEL expression with the right environment (EXP §4.5) for the set
of device types the scope can match (types listed, or all types present in REG within the scope);
plain-language diagnostics are returned with paths (`/trigger/condition`). Other checks: priority
range, delays range, event kinds exist (MOD event paths or registered system kinds), calendars
exist, procedure exists, actions parameters valid (per kind schema), escalation steps ordered by
`After`, `Count` 2–1000, sequence steps 2–5 with at most one `Absent` step not first.

### 4.3 Rule index

Built per rule-set version (atomic swap):

| Index | Key → rules |
|---|---|
| Event | event kind → rules with `OnEvent`/`OnCount`/`OnSequence`/`EndMode.OnEvent` using it |
| Property | property path → rules whose condition/end condition references it (`ReferencedProperties`) |
| Health | → `OnHealth` rules and any rule referencing `health.*` |
| Time | rules whose expressions use `now`/calendars or have `ActiveCalendar` (re-evaluated on ticks/calendar edges) |
| Scope membership | rule → `HashSet<DeviceId>` resolved from REG (recomputed on registry or rule changes; for large scopes stored as predicate over enrichment to avoid huge sets: types/categories/locations/tags checks are O(1) per device using enrichment) |

Overrides resolved per (rule, device): most specific wins — device > tag > location (deepest) —
and the effective rule (enabled, priority, delays, procedure) is computed lazily and cached per
device until rules/overrides/registry change.

### 4.4 Default rules from type templates

- Rule **templates** are documents shipped with the standard library (`types/rules/*.json`) and
  packages (`rules/`), format = `AlarmRule` without ids, with `scope.types` set to the template's
  types.
- `TemplateInstaller` runs at start and whenever MOD registers types: for every template
  referenced by a type's `defaultAlarmRules` whose type has at least one device in REG (or always
  for system templates), ensure **one site rule** exists with `TemplateKey` = template key (created
  enabled). Existing rules are never modified by templates (users may have edited them); template
  updates are shown as "template changed" in the configuration UI (UIF).

Standard templates v1:

| Key | Trigger | Priority | Notes |
|---|---|---|---|
| `std.ac.door.forced` | OnEvent `doorPosition.forced`, end OnEvent `doorPosition.closedAgain` | 200 (High) | |
| `std.ac.door.heldOpen` | OnState `device.doorPosition.position == "heldOpen"` | 400 (Medium) | |
| `std.ac.access.denied.repeated` | OnCount `accessEvents.denied` ×5 within PT1M per device, end Immediate | 400 | |
| `std.intrusion.zone.alarm` | OnEvent `zoneInput.activated` with condition `"full" in linkedState("memberOf", "armable.armState") \|\| "partial" in linkedState("memberOf", "armable.armState")` (zone's area armed, §6) | 50 (Critical) | |
| `std.intrusion.tamper` | OnState `device.tamper.tampered` | 200 | |
| `std.health.offline` | OnHealth connectivity [offline], StartDelay PT2M | 400 | all device types |
| `std.health.fault` | OnHealth any fault | 600 (Low) | |
| `std.analog.threshold` | OnState `device.overall == "alarm"` for `generic.analog-input` | 300 | |
| `std.system.accountLocked` | OnEvent `sysevent:identity.accountLocked`, Immediate | 600 | System scope |
| `std.system.breakGlass` | OnEvent `sysevent:identity.breakGlassUsed` | 10 | System scope |
| `std.system.connectionFaulted` | OnEvent `sysevent:integrations.connectionFaulted`, end on `connectionStateChanged` to connected | 300 | System scope |
| `std.command.unverified` | OnEvent `command.unverified` (command records exposed as events kinds `command.*`) | 500 | |

### 4.5 Priority bands

Site setting (`wp_alarms.settings`): bands with thresholds — default Critical 1–99, High 100–299,
Medium 300–599, Low 600–899, Info 900–1000. Used for colours, sounds, filters, permissions
(`alarms.view` grants can be limited to a priority range).

## 5. Evaluation engine (T2.09)

`AlarmSubscriber.OnRecords` for each record:

1. Determine candidates from the index (event kind, properties changed, health).
2. For each candidate rule: skip if disabled (effective, with overrides), device not in scope,
   or `ActiveCalendar` inactive.
3. Evaluate per trigger type:
   - **OnEvent**: payload condition + rule condition (with `event` and `device`) → if true, a
     *raise request* for (rule, source).
   - **OnState / OnHealth**: compute condition for the device → feeds the **evaluation state**
     machine of (rule, device): `Normal → PendingStart (StartDelay) → Active → PendingEnd
     (EndDelay) → Normal`. Transition to Active → raise request; to Normal → resolve request.
   - **OnCount**: append timestamp to the window (per device or per rule); count ≥ N within
     `Within` → raise request, window cleared.
   - **OnSequence**: advance the sequence matcher (per device if `SameDevice`); completion within
     `Within` → raise request; `Absent` steps check that no matching event occurred between
     neighbours (evaluated when the next step arrives or when the window expires).
   - **End modes** for event-type triggers: `OnEvent` end kinds → resolve request for active
     alarms of (rule, source); `OnCondition` → evaluated on property records of the source;
     `Immediate` → resolve right after raise; `Manual` → none.
4. Raise and resolve requests go to the lifecycle (§7), producing follow-up records via
   `PartitionContext.Emit`.

`OnTick`: start/end delays expiry, time-dependent conditions re-evaluation (for (rule, device)
pairs currently PendingStart/Active/PendingEnd and for rules flagged Time — bounded work per tick:
at most `Alarms:TickBudget` evaluations, default 5,000, continuing next tick), composite window
expiry (`Absent` steps), escalation timers (§11), shelving expiry.

System-scope rules (no device) are evaluated by a small dedicated worker consuming `WP_SYS`
`sysevent` records; their alarms live in partition `Partitioning.Of(rule.Id)` (records posted
through `IPartitionDispatcher`).

Determinism: evaluation only uses the record, current state (STA) and time from the context; the
same log replay yields the same alarms (recovery replays without re-running actions — §15).

## 6. Expressions environment notes

- `device` resolves from STA state of the source device.
- Rules with `OnEvent` have `event` too.
- `refs` are not available in rules (cross-device conditions are composite rules or automations).
- **Linked device state**: ALM adds one function to its expression environment (EXP supports
  environment-specific functions through `ExpressionEnvironment.Functions`):
  `linkedState("<linkKind>", "<cap>.<prop>")` returns the list of current values of that property
  on the devices linked to the source device by that link kind (REG links, both directions for
  `memberOf`). Example: a zone alarm only when its area is armed:
  `"full" in linkedState("memberOf", "armable.armState")`.

## 7. Alarm lifecycle (T2.10)

### 7.1 Model

```csharp
public enum AlarmState { Active, Acknowledged, ActiveResolved, Resolved, Shelved, Closed }

public sealed record Alarm
{
    public required AlarmId Id { get; init; }
    public required AlarmRuleId RuleId { get; init; }
    public required long RuleVersion { get; init; }
    public required string RuleKey { get; init; }
    public required AlarmSource Source { get; init; }        // device/server snapshot or system
    public required int Partition { get; init; }
    public required AlarmState State { get; init; }
    public AlarmState? StateBeforeShelve { get; init; }
    public required int Priority { get; init; }
    public required int InitialPriority { get; init; }
    public required Message Title { get; init; }
    public required DateTimeOffset RaisedAt { get; init; }
    public required DateTimeOffset LastOccurrenceAt { get; init; }
    public int Count { get; init; } = 1;
    public bool ConditionActive { get; init; }               // source still abnormal
    public DateTimeOffset? AcknowledgedAt { get; init; }  public UserId? AcknowledgedBy { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }        public UserId? ClosedBy { get; init; }
    public string? Report { get; init; }
    public AlarmOwner? Owner { get; init; }                  // User or UserGroup
    public DateTimeOffset? ShelvedUntil { get; init; }    public UserId? ShelvedBy { get; init; }  public string? ShelveReason { get; init; }
    public int EscalationLevel { get; init; }
    public ProcedureId? Procedure { get; init; }  public long? ProcedureVersion { get; init; }
    public IReadOnlyDictionary<string, ProcedureAnswer> ProcedureAnswers { get; init; } = Empty;
    public IncidentId? Incident { get; init; }
    public Guid TriggerRecordId { get; init; }               // envelope id of the record that raised it
    public IReadOnlyList<DeviceId> LinkedCameras { get; init; } = [];
    public AlarmAudience Audience { get; init; } = AlarmAudience.Everyone;
}
```

`Title`: `alarms.title` default = rule name; templates may provide a title key with arguments
(event title).

### 7.2 Transitions

| From | Trigger | To | Record |
|---|---|---|---|
| — | raise request, no open alarm for (rule, source) | Active | `alarm.raised` |
| Active / Acknowledged / ActiveResolved / Resolved | raise request with `Repeat = Counter` | same state, but Resolved → Acknowledged and ActiveResolved → Active (condition back) | `alarm.occurred` (count+1, last occurrence) |
| any open | raise request with `Repeat = NewAlarm` | new alarm Active | `alarm.raised` |
| Active | acknowledge (operator) | Acknowledged | `alarm.acknowledged` |
| ActiveResolved | acknowledge | Resolved (then auto-close if policy) | `alarm.acknowledged` |
| Active | resolve request | ActiveResolved | `alarm.resolved` |
| Acknowledged | resolve request | Resolved | `alarm.resolved` |
| Resolved | auto-close (policy, report not required) | Closed | `alarm.closed` (by system) |
| Resolved | close (operator, report if required) | Closed | `alarm.closed` |
| Acknowledged | close while condition active | Closed only if `AllowCloseWhileActive` | `alarm.closed` |
| Active/Acknowledged/ActiveResolved/Resolved | shelve (duration ≤ MaxShelve and permission max) | Shelved | `alarm.shelved` |
| Shelved | expiry or unshelve | if condition active → previous state (or Active if it was ActiveResolved…); else Closed when ack not required, else Resolved | `alarm.unshelved` (+ `alarm.closed`) |
| any open | comment, transfer, take ownership, procedure answer, priority change (escalation) | same | `alarm.commented`, `alarm.transferred`, `alarm.procedureAnswered`, `alarm.priorityChanged`, `alarm.escalated` |

Rules:

- **At most one open alarm per (rule, source)** in `Counter` mode.
- `AcknowledgeRequired = false`: raise produces Active; resolve then closes automatically (no
  operator action), useful for informational alarms.
- Operator actions are executed **on the alarm's partition** (`IPartitionDispatcher`): the
  current state is checked there, so two concurrent acknowledgements produce one success and one
  `alarms.already_acknowledged` (args `by`, `at`).
- Every transition record carries actor (user/session/client kind or system), time, and the new
  state; HIS stores them as the alarm timeline.

### 7.3 Operator service

```csharp
public interface IAlarmService
{
    Task<Result> AcknowledgeAsync(AlarmId id, AlarmActor actor, CancellationToken ct);
    Task<Result<BatchResult>> AcknowledgeManyAsync(IReadOnlyList<AlarmId> ids, AlarmActor actor, CancellationToken ct); // ≤ 500
    Task<Result> CommentAsync(AlarmId id, AlarmActor actor, string text /* 1–2,000 */, IReadOnlyList<FileId>? attachments, CancellationToken ct);
    Task<Result> TransferAsync(AlarmId id, AlarmActor actor, AlarmOwner to, string? message, CancellationToken ct);
    Task<Result> TakeOwnershipAsync(AlarmId id, AlarmActor actor, CancellationToken ct);
    Task<Result> ShelveAsync(AlarmId id, AlarmActor actor, TimeSpan duration, string reason, CancellationToken ct);
    Task<Result> UnshelveAsync(AlarmId id, AlarmActor actor, CancellationToken ct);
    Task<Result> CloseAsync(AlarmId id, AlarmActor actor, string? report, CancellationToken ct);
    Task<Result> AnswerProcedureStepAsync(AlarmId id, AlarmActor actor, string stepId, ProcedureAnswer answer, CancellationToken ct);
}
public sealed record AlarmActor(UserId User, SessionId Session, ClientKind ClientKind, string? WorkstationId);
```

Permission checks (M3 via RGT; M2 fallback: authenticated users may do everything except shelve
beyond 1 h and close without report): `alarms.acknowledge`, `alarms.comment`, `alarms.transfer`,
`alarms.shelve` (constraint max duration), `alarms.close`, `alarms.closeWithoutReport`, each with
scope (source location/device) and priority range.

## 8. Queries and live topic (T2.10)

```csharp
public interface IAlarmQuery
{
    IReadOnlyList<Alarm> GetActive(AlarmFilter filter, IVisibleScope scope);   // in-memory, all partitions
    Result<Alarm> Get(AlarmId id);                                               // active or recently closed (24 h cache)
    AlarmCounters GetCounters(AlarmFilter filter, IVisibleScope scope);
}
public sealed record AlarmFilter(int? MinPriority, int? MaxPriority, IReadOnlyList<AlarmState>? States,
    IReadOnlyList<LocationId>? LocationsSubtree, IReadOnlyList<Category>? Categories, bool? Mine, bool? Unassigned,
    UserId? ForUser /* for Mine */, IReadOnlyList<AlarmRuleId>? Rules);
public sealed record AlarmCounters(int Active, int Unacknowledged, int Mine, IReadOnlyDictionary<string /* band */, int> ByBand);
```

API:

| Method and path | Permission | |
|---|---|---|
| `GET /api/v1/alarms?…filter…` | `alarms.view` + scope + priority range | Active alarms |
| `GET /api/v1/alarms/counters?…filter…` | same | |
| `GET /api/v1/alarms/{id}` | same | Alarm + timeline (HIS) + procedure definition snapshot |
| `POST /api/v1/alarms/{id}/acknowledge` / `comment` / `transfer` / `take` / `shelve` / `unshelve` / `close` / `procedure/{stepId}` | per action | |
| `POST /api/v1/alarms/acknowledge` | `alarms.acknowledge` | `{ ids: [...] }` → per-id results |
| `GET/POST/PATCH/DELETE /api/v1/alarm-rules…` | `config.alarms.view/edit` (M3 drafts) | rule CRUD with compile diagnostics |
| `POST /api/v1/alarm-rules/validate` | `config.alarms.edit` | Compile without saving → diagnostics |
| `POST /api/v1/alarm-rules/scope-preview` | `config.alarms.view` | `RuleScope` → `{ count, sample: DeviceSummary[≤50] }` (M3, used by UIF) |
| `POST /api/v1/alarm-rules/test-condition` | `config.alarms.edit` | `{ expression, deviceId }` → `{ value, type, diagnostics }` evaluated with the device's current values (M3, used by UIF) |
| `GET/POST/PATCH/DELETE /api/v1/alarm-rule-overrides…`, `/procedures…` | `config.alarms.edit` | |
| `GET /api/v1/alarm-rule-templates` | `config.alarms.view` | Templates with "changed since rule creation" flags |
| `GET/PUT /api/v1/alarm-settings` | `config.alarms.edit` | Priority bands, sounds per band |

Live topic `alarms`: filter = `AlarmFilter`; snapshot = active alarms matching (visible scope +
audience: user's groups/workstation); deltas = alarm DTO after every transition (`Coalescable =
false`), plus `removed` marker when an alarm no longer matches (closed or out of filter). Popup
actions are delivered as `Notice` messages (`alarm.popup`) to targeted sessions.

## 9. Persistence of active state

`ISnapshotParticipant` (STA §8.1): consumer `alarms`, kinds `alarm` (open alarms) and
`alarm-eval` (evaluation states with pending timers, composite windows, escalation schedule).
Closed alarms are removed from snapshots. Recovery restores them before replay; replaying records
re-applies transitions idempotently (alarm ids in records).

## 10. Delays and composite details (T2.11)

- StartDelay: condition must stay true continuously for the delay; a false evaluation during
  PendingStart cancels it (no alarm, no record). EndDelay symmetrical for resolution.
- For event triggers, StartDelay means "raise only if not ended within the delay" (end event or
  condition within delay cancels).
- Composite windows memory bounded: per rule max 10,000 tracked keys; oldest evicted with log 2620.

## 11. Escalation (T2.11)

- On raise, schedule escalation steps (relative to raisedAt). On each due step: check `When`
  (NotAcknowledged: state Active/ActiveResolved; NotResolved: ConditionActive; NotClosed: not
  Closed; RepeatedCount: Count ≥ threshold) → apply: priority raised (`alarm.priorityChanged`),
  `alarm.escalated` record with step index, notify groups (NOT hook), louder sound flag in DTO.
- Steps fire once each; cancelled when the alarm closes.

## 12. Procedures (T2.11)

```csharp
public sealed record Procedure(ProcedureId Id, LocalizedName Name, IReadOnlyList<ProcedureStep> Steps, long Version);
public sealed record ProcedureStep(string Id /* ^[a-z0-9-]{1,32}$ */, ProcedureStepKind Kind, LocalizedName Label,
    bool Mandatory, JsonElement Parameters, IReadOnlyList<Branch>? Branches);
public enum ProcedureStepKind { Instruction, Checklist, Question, Input, Call, Action, Wait }
public sealed record Branch(string Answer, string GotoStepId);
public sealed record ProcedureAnswer(JsonElement Value, UserId By, DateTimeOffset At);
```

Parameters per kind: Question `{ choices: [{ value, label }] }`; Input `{ type: "text" | "number" |
"choice" | "photo", choices? }`; Call `{ contactSource: "location" | "fixed", phone?, intercomDevice? }`;
Action `{ command: { path, arguments } | automation: id }`; Wait `{ duration }`. Answers validated
per kind; a step skipped by branching is not mandatory. Close is refused while mandatory reachable
steps are unanswered (`alarms.procedure.incomplete` with step ids). The alarm stores the procedure
version used.

## 13. Audience (T2.11)

Visible to a user iff: RGT scope includes the source (or system alarms permission), priority within
the user's `alarms.view` priority range, and (audience empty, or user in one of the audience
groups, or session workstation in audience workstations). `AudienceResolver` caches per user
(invalidated on rights/groups changes).

## 14. Actions (T2.12)

| Kind | Parameters | Behaviour |
|---|---|---|
| Command | `{ target: "source" \| { deviceId } \| { selector }, command, arguments }` | CMD request with issuer `AlarmAction` (identity = site setting `Alarms:ActionIdentity`, a Script user; M2 fallback: system identity with full rights); correlation alarm id |
| Automation | `{ automationId, parameters }` | AUT (M8); before M8 ignored with warning |
| Notify | `{ channels, recipients, template }` | `IAlarmNotificationHook` (NOT implements in M5; M2: logs) |
| Snapshot | `{ cameras: "linked" \| [ids] }` | VID (M6); before: ignored |
| Popup | `{ userGroups?, workstations? }` | Live `Notice` `alarm.popup` |
| Bookmark | `{ cameras, text }` | VID (M6) |
| Script | `{ scriptId, function }` | SCR (M8) |

- Executed through the side-effect queue class `AlarmAction` with timeout 30 s; results recorded
  as `alarm.actionExecuted { action index, outcome, error? }` (timeline).
- **Never re-executed on replay**: the subscriber knows it is in recovery (PIP handshake) and skips
  action execution; actions whose `alarm.actionExecuted` record is missing after recovery are
  **not** retried (at-most-once, documented).

## 15. Default rules installation (T2.12)

See §4.4. Also creates the `Alarms:ActionIdentity` script user at first start (IDN user kind
Script, name "Alarm actions").

## 16. Errors

`alarms.rule.not_found`, `alarms.rule.key_taken`, `alarms.rule.invalid` (with diagnostics),
`alarms.rule.scope_empty`, `alarms.not_found`, `alarms.already_acknowledged`, `alarms.not_open`,
`alarms.close.report_required`, `alarms.close.condition_active`, `alarms.procedure.incomplete`,
`alarms.procedure.step_unknown`, `alarms.procedure.answer_invalid`, `alarms.shelve.too_long`,
`alarms.transfer.target_invalid`, `alarms.batch.too_large`.

## 17. Observability

| EventId | Level | Message |
|---|---|---|
| 2600 | Information | Alarm {AlarmId} raised by rule {RuleKey} on {Source} priority {Priority} |
| 2601 | Information | Alarm {AlarmId} {Transition} by {Actor} |
| 2610 | Warning | Rule {RuleKey} evaluation errors: {Count} in the last minute ({Sample}) |
| 2611 | Warning | Tick budget exhausted; {Remaining} evaluations deferred |
| 2620 | Warning | Composite window for rule {RuleKey} evicted keys |
| 2630 | Warning | Alarm action {Kind} for {AlarmId} failed: {Error} |

Metrics: `watchpoint.alarms.active` (gauge by band, state), `watchpoint.alarms.raised` (counter by
rule), `watchpoint.alarms.ack.latency` (histogram s, raised→ack), `watchpoint.alarms.eval.duration`
(histogram µs), `watchpoint.alarms.actions` (counter by kind, outcome).

## 18. Tests

Unit: `tests/service/Watchpoint.Alarms.Tests` (fake STA/REG, real EXP, TestClock). Integration:
`Watchpoint.IntegrationTests/Alarms` with simulator.

| Id | Test |
|---|---|
| ALM-U-001 | Rule validation: empty scope rejected |
| ALM-U-002 | Rule validation: CEL diagnostics with paths |
| ALM-U-003 | Rule validation: unknown event kind / calendar / procedure |
| ALM-U-004 | Rule validation: count/sequence bounds; Absent step position |
| ALM-U-005 | Rule key uniqueness and pattern |
| ALM-U-006 | Scope: types include descendants (IsA) |
| ALM-U-007 | Scope: location subtree, servers, tags all/any/exclude combination |
| ALM-U-008 | Scope membership recomputed on registry change |
| ALM-U-009 | Index by event kind returns only relevant rules |
| ALM-U-010 | Index by referenced properties |
| ALM-U-011 | Health index |
| ALM-U-012 | Time-dependent rules flagged |
| ALM-U-013 | Override precedence device > tag > deepest location |
| ALM-U-014 | Override disables rule for a location |
| ALM-U-015 | ActiveCalendar gating (and inverted) |
| ALM-U-016 | PriorityByCalendar picks first active calendar |
| ALM-U-017 | OnEvent raises with payload condition |
| ALM-U-018 | OnEvent with rule condition using device state |
| ALM-U-019 | OnState raises while true, resolves when false |
| ALM-U-020 | OnHealth connectivity and faults |
| ALM-U-021 | OnCount per device within window |
| ALM-U-022 | OnCount global (not per device) |
| ALM-U-023 | OnCount window expiry |
| ALM-U-024 | OnSequence same device within window |
| ALM-U-025 | OnSequence with Absent step ("forced and no granted in previous 10 s") |
| ALM-U-026 | OnSequence timeout resets |
| ALM-U-027 | EndMode Immediate |
| ALM-U-028 | EndMode OnEvent |
| ALM-U-029 | EndMode OnCondition |
| ALM-U-030 | EndMode Manual |
| ALM-U-031 | Evaluation runtime error counts as false and is counted/logged |
| ALM-U-032 | `linkedState` function for intrusion zone rule |
| ALM-U-033 | System-scope rules from WP_SYS records, partition by rule id |
| ALM-U-034 | Determinism: replay produces same alarms without actions |
| ALM-U-035 | Disabled device not evaluated |
| ALM-U-036 | Raise creates Active with title, priority, snapshot, linked cameras |
| ALM-U-037 | Counter mode: repeat increments count |
| ALM-U-038 | NewAlarm mode creates separate alarms |
| ALM-U-039 | One open alarm per (rule, source) |
| ALM-U-040 | Acknowledge Active → Acknowledged |
| ALM-U-041 | Acknowledge ActiveResolved → Resolved → auto-close |
| ALM-U-042 | Resolve Active → ActiveResolved |
| ALM-U-043 | Resolve Acknowledged → Resolved → auto-close (report not required) |
| ALM-U-044 | Report required: stays Resolved until close with report |
| ALM-U-045 | Close refused without report → `alarms.close.report_required` |
| ALM-U-046 | Close while condition active refused unless allowed |
| ALM-U-047 | Repeat after resolve returns to Acknowledged/Active appropriately |
| ALM-U-048 | AcknowledgeRequired=false: auto-close on resolve |
| ALM-U-049 | Concurrent acknowledgements: one success, one already_acknowledged with by/at |
| ALM-U-050 | Acknowledge many: per-id results, ≤ 500 |
| ALM-U-051 | Comment length and attachments recorded |
| ALM-U-052 | Transfer to user/group; invalid target |
| ALM-U-053 | Take ownership |
| ALM-U-054 | Shelve within max; too long rejected |
| ALM-U-055 | Shelve expiry restores state when condition active |
| ALM-U-056 | Shelve expiry closes/resolves when condition normal |
| ALM-U-057 | Unshelve manual |
| ALM-U-058 | Records emitted for every transition with actor |
| ALM-U-059 | Operator actions executed on the alarm's partition |
| ALM-U-060 | Active alarms query filters (priority, states, location, mine, unassigned) |
| ALM-U-061 | Counters by band |
| ALM-U-062 | Recently closed in 24 h cache |
| ALM-U-063 | Priority bands default and custom settings |
| ALM-U-064 | Live topic snapshot and transition deltas |
| ALM-U-065 | Live topic `removed` marker when out of filter |
| ALM-U-066 | Popup notice routing to targeted sessions |
| ALM-U-067 | Snapshot participant serialises open alarms and evaluation state |
| ALM-U-068 | Recovery restores alarms before replay; idempotent replay |
| ALM-U-069 | Closed alarms removed from snapshots |
| ALM-U-070 | M2 fallback permissions |
| ALM-U-071 | StartDelay: continuous true raises after delay |
| ALM-U-072 | StartDelay: interruption cancels without record |
| ALM-U-073 | EndDelay symmetrical |
| ALM-U-074 | Event trigger with StartDelay cancelled by end event |
| ALM-U-075 | Time-dependent condition re-evaluated on tick |
| ALM-U-076 | Calendar edge re-evaluation |
| ALM-U-077 | Tick budget and continuation |
| ALM-U-078 | Composite window eviction bound |
| ALM-U-079 | Escalation NotAcknowledged raises priority |
| ALM-U-080 | Escalation NotResolved |
| ALM-U-081 | Escalation NotClosed |
| ALM-U-082 | Escalation RepeatedCount |
| ALM-U-083 | Escalation steps fire once, cancelled on close |
| ALM-U-084 | Escalation notifies groups via hook |
| ALM-U-085 | Escalation louder sound flag in DTO |
| ALM-U-086 | Procedure step validation by kind |
| ALM-U-087 | Procedure branching |
| ALM-U-088 | Mandatory steps block close; branched-out steps not required |
| ALM-U-089 | Procedure version stored on alarm |
| ALM-U-090 | Procedure answers recorded in timeline |
| ALM-U-091 | Action step executes command with rights |
| ALM-U-092 | Wait step metadata |
| ALM-U-093 | Call step contact resolution from location |
| ALM-U-094 | Audience by user group |
| ALM-U-095 | Audience by workstation |
| ALM-U-096 | Audience + scope + priority range combined |
| ALM-U-097 | Audience cache invalidation |
| ALM-U-098 | Procedure CRUD validation (ids unique, branches reference existing steps, no cycles) |
| ALM-U-099 | Override CRUD validation |
| ALM-U-100 | Rule deletion closes its open alarms with system reason |
| ALM-U-101 | Rule edit applies to new evaluations; open alarms keep their rule version |
| ALM-U-102 | Rule disable resolves its state-based alarms |
| ALM-U-103 | Device deleted: open alarms resolved/closed with reason |
| ALM-U-104 | Device in maintenance: rules skipped (configurable per rule: default skip) |
| ALM-U-105 | Server unavailable: health rules handle server only (no device flood) |
| ALM-U-106 | Title from template key with event arguments |
| ALM-U-107 | Alarm DTO JSON shape |
| ALM-U-108 | Timeline ordering |
| ALM-U-109 | Batch acknowledge permission per alarm scope |
| ALM-U-110 | Alarm query performance: 10,000 active alarms filtered < 20 ms |
| ALM-U-111 | Templates installed when first device of a type exists |
| ALM-U-112 | A template rule deleted by a user is recorded as dismissed and never re-created |
| ALM-U-113 | Template changes flagged, rule not modified |
| ALM-U-114 | System templates always installed |
| ALM-U-115 | Action Command via CMD with AlarmAction issuer and correlation |
| ALM-U-116 | Action Notify calls hook |
| ALM-U-117 | Action Popup |
| ALM-U-118 | Actions skipped during recovery |
| ALM-U-119 | Action failure recorded in timeline |
| ALM-U-120 | Action timeout |
| ALM-U-121 | Action identity created at first start |
| ALM-U-122 | Unavailable action kinds (before their modules) ignored with warning |
| ALM-U-123 | `std.*` templates validate against the standard library |
| ALM-U-124 | Unverified command alarm template |
| ALM-U-125 | Break-glass system alarm template |
| ALM-I-001 | Simulator forced door → alarm raised, ack, resolved, auto-closed — records in log and history |
| ALM-I-002 | Restart with open alarms → identical after recovery |
| ALM-I-003 | Crash during alarm actions → no duplicate actions |
| ALM-I-004 | API acknowledge concurrency (two clients) |
| ALM-I-005 | Live topic over SignalR with filters |
| ALM-I-006 | Rule CRUD via API with diagnostics |
| ALM-I-007 | Analog threshold alarm via simulator heat spike |
| ALM-I-008 | Count rule on denied access (simulator scenario) |
| ALM-I-009 | Sequence rule forced-without-grant |
| ALM-I-010 | Load: 1,000 alarms/min flood → console latency < 1 s p95 |
| ALM-I-011 | Default templates present after simulator import |
| ALM-I-012 | Alarm action command opens a door (simulator) |
| ALM-I-013 | Escalation end-to-end with TestClock-controlled node (fake time provider in test host) |
| ALM-I-014 | Alarms consumed into HIS history and timeline |

## 19. Tasks

| Task | Sections | Tests |
|---|---|---|
| T2.09 | §4–§6 | ALM-U-001…ALM-U-035 |
| T2.10 | §7–§9 | ALM-U-036…ALM-U-070, ALM-I-001…ALM-I-010 |
| T2.11 | §10–§13 | ALM-U-071…ALM-U-110 |
| T2.12 | §14–§15 | ALM-U-111…ALM-U-125, ALM-I-011…ALM-I-014 |
