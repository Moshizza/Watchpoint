# RGT — Rights, User Groups and Rights Copy

| | |
|---|---|
| Spec id | RGT |
| Projects | `src/service/Watchpoint.Rights` |
| Milestone | M3 |
| Tasks | T3.04 (§4–§7), T3.05 (§8–§10) |
| Design docs | [Users and rights](../../docs/modules/users-and-rights.md) |

## 1. Purpose and scope

- The **permission catalogue** and **right grants** held by each user (no profiles).
- The **rights evaluator** used by every API call, live subscription, command and alarm view:
  permission × scope × schedule × constraints.
- **User groups** (organise users; no rights), **template users**, the **rights-copy tool**
  (copy / compare / preview / apply / undo / re-apply), **drift indicator**, and protection
  against privilege escalation.

Not in scope: accounts and authentication (IDN), applying changes atomically (CFG publishes
`rights.grants` documents), audit storage (AUD).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, IDN (users, kinds, administrator flag), REG (scopes resolution), MOD (command permissions), SCH (grant schedules), CFG (object types `rights.grants`, `rights.userGroup`), PIP (`rights.changed` on WP_SYS), STO | API (`ICurrentUser`), CMD (`ICommandAuthorizer`), ALM (visibility, priority ranges), EVT/HIS (scope filters), IDN (directory mappings), UIF, every module checking permissions |

## 3. Layout

```
Watchpoint.Rights/
├── Contracts/
│   ├── PermissionDefinition.cs, IPermissionCatalogue.cs
│   ├── RightGrant.cs, GrantScope.cs, GrantConstraints.cs, GrantSource.cs
│   ├── IRightsEvaluator.cs, AuthorizationDecision.cs, IVisibleScope.cs
│   ├── UserGroup.cs, IUserGroupService.cs
│   ├── RightsCopy/CopyRequest.cs, CopyPreview.cs, CopyOperation.cs, IRightsCopyService.cs
│   ├── IDriftService.cs
│   └── RightsErrors.cs
├── Internal/
│   ├── Catalogue/PermissionCatalogue.cs, CommandPermissionGenerator.cs
│   ├── Evaluation/RightsEvaluator.cs, ScopeIndex.cs (device bitsets), GrantMatcher.cs, ScheduleEpochs.cs
│   ├── Groups/UserGroupService.cs
│   ├── Copy/RightsCopyService.cs, ScopeTranslator.cs, GrantIdentity.cs, PreviewBuilder.cs
│   ├── Drift/DriftService.cs
│   ├── Escalation/EscalationGuard.cs
│   ├── Adapters/CommandAuthorizer.cs, CurrentUserPermissions.cs
│   └── Storage/RightsDbContext.cs (+ Postgres subclass)
├── RightsModule.cs
└── RightsLog.cs                       event ids 2800–2849
```

## 4. Permission catalogue (T3.04)

```csharp
public enum PermissionScopeKind { None /* site-wide only */, Devices, Locations, System }

public sealed record PermissionDefinition(PermissionCode Code, string Family /* "operation.alarms" */, string Module,
    string LabelKey, string DescriptionKey, PermissionScopeKind ScopeKind, IReadOnlySet<ConstraintKind> AllowedConstraints,
    bool Sensitive /* shown with a warning when granted */);

public enum ConstraintKind { RequireReason, RequireFourEyes, RequireStepUp, MaxDuration, PriorityRange, ClientKinds, MaxLookback }

public interface IPermissionCatalogue
{
    IReadOnlyList<PermissionDefinition> All { get; }
    Result<PermissionDefinition> Get(PermissionCode code);
    bool Matches(PermissionCode grantPattern, PermissionCode requested);   // "commands.openable.*" matches "commands.openable.open"
    void Register(PermissionDefinition definition);                        // modules at phase Catalogue
}
```

Grant patterns: a grant's permission may end with `.*` (all permissions under that prefix) or be
`*` (only for administrators' convenience in templates — it still obeys escalation rules).

### 4.1 Catalogue v1

| Family | Codes (scope kind) |
|---|---|
| operation.devices | `operate.devices.view` (Devices), `operate.personalData.view` (Devices), `operate.history.view` (Devices), `operate.events.view` (Devices), `operate.events.export` (Devices), `operate.notes.create` (Locations) |
| operation.alarms | `alarms.view` (Devices; constraint PriorityRange), `alarms.acknowledge`, `alarms.comment`, `alarms.transfer`, `alarms.shelve` (MaxDuration), `alarms.close`, `alarms.closeWithoutReport`, `alarms.mute` (None), `admin.systemEvents.view` (System), `operate.notifications.subscribe` (None, M5) |
| operation.commands | Generated: `commands.<capability>.<command>` for every capability command in MOD (Devices; constraints RequireReason, RequireFourEyes, RequireStepUp, ClientKinds), plus `commands.approve` (Devices), `commands.viewAll` (Devices), `commands.cancelAny` (Devices), `commands.priority.critical` (None) |
| operation.video (M6) | `video.live`, `video.playback` (MaxLookback), `video.export`, `video.ptz`, `video.unmask` |
| operation.workspace | `operate.workspaces.save` (None), `operate.workspaces.publish` (None), `operate.duringMaintenance` (None; Sensitive — operation side stays available during maintenance mode, MNT) |
| configuration | `config.registry.view/edit`, `config.integrations.view/edit/install/operate`, `config.alarms.view/edit`, `config.calendars.edit`, `config.users.view/edit`, `config.rights.view/edit`, `config.synoptics.*` (M6), `config.automations.*` (M8), `config.scripts.*` (M8), `config.notifications.*` (M5), `config.system.*`, `config.publish` (publish drafts), `config.approve`, `config.locks.break`, `config.maintenance` (start/stop maintenance mode), `config.licence` (licence panel, requests, import — M7) — scope Locations for registry/alarms/synoptics, None otherwise |
| reports (M5) | `reports.run`, `reports.schedule`, `reports.schedule.manage`, `config.reports.view/edit`, `admin.bi.manage` (all None) |
| administration | `admin.audit.view`, `admin.sessions.view`, `admin.sessions.manage`, `admin.health.view`, `admin.diagnostics.export`, `admin.privacy` (None) |

`CommandPermissionGenerator` regenerates command permissions when MOD's catalogue changes
(new package types) — labels come from capability command translations.

## 5. Grants (T3.04)

```csharp
public sealed record RightGrant(RightGrantId Id, UserId User, PermissionCode Permission, GrantScope Scope,
    string? ScheduleCalendar /* SCH key; null = always */, GrantConstraints Constraints, GrantSource Source,
    DateTimeOffset CreatedAt, UserId CreatedBy);

public sealed record GrantScope(bool Everything,
    IReadOnlyList<LocationId>? LocationsSubtree, IReadOnlyList<ServerId>? Servers, IReadOnlyList<Category>? Categories,
    IReadOnlyList<TypeId>? Types, IReadOnlyList<TagId>? TagsAny, IReadOnlyList<TagId>? TagsExclude,
    IReadOnlyList<DeviceId>? Devices, IReadOnlyList<SiteId>? Sites /* M9 */);
// Everything=true ⇒ all lists null. Otherwise non-null lists ANDed (Devices ORs with the rest: explicit devices always included).

public sealed record GrantConstraints(bool RequireReason = false, bool RequireFourEyes = false, bool RequireStepUp = false,
    TimeSpan? MaxDuration = null, PriorityRange? Priorities = null, IReadOnlySet<ClientKind>? ClientKinds = null,
    TimeSpan? MaxLookback = null);
public sealed record PriorityRange(int Min, int Max);

public abstract record GrantSource
{
    public sealed record Manual : GrantSource;
    public sealed record CopiedFrom(UserId SourceUser, Guid CopyOperationId, DateTimeOffset At, bool SourceWasTemplate) : GrantSource;
    public sealed record DirectoryMapping(Guid ProviderId, string GroupDn, UserId TemplateUser) : GrantSource;
}
```

- Grants of a user are edited as one configuration document `rights.grants` (CFG object type, id =
  user id): `{ "grants": [ … ] }`. Validation: permission exists (or valid pattern), constraints
  allowed by the definition, scope kind compatible (a `None` permission must have
  `Everything=true`), calendar exists, referenced locations/devices exist; escalation guard (§10).
- `GrantIdentity`: canonical form `(permission, normalised scope, schedule, normalised constraints)`
  — two grants with the same identity are duplicates (validation error).

## 6. Evaluation (T3.04)

```csharp
public interface IRightsEvaluator
{
    bool HasPermission(UserId user, PermissionCode permission, DateTimeOffset now);   // any matching active grant anywhere
    AuthorizationDecision Authorize(UserId user, PermissionCode permission, AuthorizationTarget target, DateTimeOffset now);
    IVisibleScope GetVisibleScope(UserId user, PermissionCode permission, DateTimeOffset now);
    IReadOnlyList<GrantExplanation> Explain(UserId user, PermissionCode permission, AuthorizationTarget target, DateTimeOffset now); // "why"
}

public abstract record AuthorizationTarget
{
    public sealed record Device(DeviceId Id) : AuthorizationTarget;
    public sealed record Location(LocationId Id) : AuthorizationTarget;
    public sealed record System : AuthorizationTarget;
}

public sealed record AuthorizationDecision(bool Allowed, GrantConstraints EffectiveConstraints, string? DenialCode /* common.forbidden | rights.outside_schedule | rights.client_kind */);

public interface IVisibleScope
{
    bool Everything { get; }
    bool ContainsDevice(DeviceId id);
    bool ContainsLocation(LocationId id);           // location visible if any grant scope covers it or a device inside it
    IReadOnlySet<DeviceId>? Devices { get; }        // null when Everything
    IReadOnlySet<LocationId>? Locations { get; }
    DateTimeOffset ValidUntil { get; }              // next schedule transition affecting this scope
}
```

Algorithm:

1. **Administrators** (IDN flag): allowed for everything; `EffectiveConstraints` = none (site
   policies such as step-up for critical commands are enforced by CMD independently).
2. Template, disabled, expired users: nothing allowed.
3. Matching grants = grants whose permission pattern matches, whose schedule calendar is active
   at `now` (or none), and whose scope contains the target (System target: `Everything` grants
   only, or permission scope kind System).
4. None → denied (`rights.outside_schedule` if some grant would match outside its schedule,
   else `common.forbidden`).
5. Effective constraints = **least restrictive** combination over matching grants
   (`RequireX` = AND of all, `MaxDuration` = max, `PriorityRange` = union hull, `ClientKinds` =
   union, `MaxLookback` = max). Client kind constraint then checked against the request's client
   kind → `rights.client_kind` if not allowed.

Performance:

- `ScopeIndex` assigns each device an integer index (rebuilt incrementally on registry changes)
  and caches, per (user, permission family or code, schedule epoch), a **bitset** of visible devices
  plus visible locations. 50,000 devices → 6.25 KB per bitset.
- `Authorize` on a device is O(number of grants of the user matching the code) with O(1) scope
  checks after the first computation; target p99 < 5 µs warm.
- Caches invalidated on: grants change (user), registry change affecting scope membership
  (location moves, tags, server, type), calendar change, schedule edge (`ValidUntil`).
- **Scope post-filters** (`IVisibleScopePostFilter`, registered by other modules — MNT location
  maintenance in M3): applied after grant evaluation for non-administrators; a filter may remove
  devices/locations and declares its own invalidation (`Changed` event).
- Each invalidation that changes a user's effective rights publishes `rights.changed { userIds }`
  on `WP_SYS` (API re-filters live subscriptions, API §8.6).

### 6.1 Adapters

- `ICurrentUser` (API §5.2) implemented with this evaluator (replaces the M1–M2 fallback).
- `ICommandAuthorizer` (CMD §5 step 6): `Authorize(user, "commands.<cap>.<cmd>", Device)` →
  allowed + constraints (reason, four-eyes, step-up, client kinds). Script/automation identities are
  evaluated like users.
- ALM visibility: `alarms.view` decision with `PriorityRange` constraint.

## 7. User groups (T3.04)

```csharp
public sealed record UserGroup(UserGroupId Id, LocalizedName Name, string? Description,
    IReadOnlyList<UserId> Members, IReadOnlyList<UserId> Managers /* may edit membership */, long Version);
```

- Groups hold **no rights**. Used for: organisation and filtering of users, bulk actions (disable,
  extend validity, force password change, sign out, MFA reset), rights-copy targets, alarm
  audiences, notification recipients, escalation targets, handover.
- Members can be added by `config.users.edit` holders or the group's managers.
- CFG object type `rights.userGroup`. API: `GET/POST/PATCH/DELETE /api/v1/user-groups…`,
  `POST /api/v1/user-groups/{id}/bulk { action, parameters }` (job).

## 8. Rights copy (T3.05)

```csharp
public sealed record CopyRequest(UserId Source, IReadOnlyList<UserId> TargetUsers, IReadOnlyList<UserGroupId> TargetGroups,
    CopyWhat What, CopyMode Mode, IReadOnlyList<ScopeMapping> ScopeTranslation, CopyExclusions Exclusions, string? Reason);

[Flags] public enum CopyWhat { Grants = 1, ClientAccess = 2, Workstations = 4, SessionPolicy = 8, GroupMemberships = 16,
                               NotificationSubscriptions = 32 /* M5 */, Language = 64, MfaPolicy = 128 }
public enum CopyMode { Replace, Merge, Remove }
public sealed record ScopeMapping(ScopeEntityKind Kind /* Location | Server | Device | Tag */, Guid From, Guid To);
public sealed record CopyExclusions(bool SkipAdministrators = true, bool SkipDisabled = true,
    IReadOnlyList<PermissionCode>? KeepManualGrantsFor = null /* in Replace mode, manual grants matching these patterns are kept */);

public interface IRightsCopyService
{
    Task<Result<CopyPreview>> PreviewAsync(CopyRequest request, UserId actor, CancellationToken ct);
    Task<Result<CopyOperation>> ApplyAsync(Guid previewId, UserId actor, string? reason, CancellationToken ct);  // via CFG
    Task<Result<CopyOperation>> UndoAsync(Guid operationId, UserId actor, CancellationToken ct);
    Task<Result<CopyPreview>> PreviewReapplyAsync(Guid operationId, UserId actor, CancellationToken ct);
    Task<Page<CopyOperation>> HistoryAsync(CopyHistoryQuery query, PageRequest page, CancellationToken ct);
    Task<Result<UserComparison>> CompareAsync(UserId a, UserId b, CancellationToken ct);
}

public sealed record CopyPreview(Guid PreviewId /* valid 15 min */, CopyRequest Request, IReadOnlyList<TargetPreview> Targets,
    int TotalAdded, int TotalRemoved, int TotalChanged, IReadOnlyList<Problem> Warnings);
public sealed record TargetPreview(UserId User, string DisplayName, TargetStatus Status /* Included | Skipped */, string? SkipReason,
    IReadOnlyList<GrantDiff> Grants, IReadOnlyList<FieldDiff> Fields, IReadOnlyList<SkippedGrant> SkippedGrants);
public sealed record GrantDiff(DiffKind Kind /* Added | Removed | Changed */, RightGrant? Before, RightGrant? After);
public sealed record SkippedGrant(RightGrant Grant, string ReasonCode /* rights.copy.actor_lacks_permission, rights.copy.scope_not_translatable */);
public sealed record CopyOperation(Guid Id, CopyRequest Request, UserId Actor, DateTimeOffset At, long ConfigVersion,
    IReadOnlyList<TargetPreview> Applied, Guid? UndoneBy, Guid? UndoOf);
```

### 8.1 Algorithm

1. Resolve targets: users + current members of target groups (deduplicated), minus the source,
   minus exclusions (administrators, disabled), minus the actor (users cannot change their own
   rights — `rights.self_change_forbidden` as a skip reason).
2. Source grants → **translate scopes** with `ScopeTranslation` (replace ids; a scope referencing
   an id without mapping is kept as is; mapping a location to a location of a different kind is
   allowed with a warning). Translated grants get source `CopiedFrom(source, operationId, now,
   sourceIsTemplate)`.
3. Escalation guard (§10) on each translated grant for the **actor**: grants the actor could not
   create are **skipped** with reason (never silently granted).
4. Per target, compute the new grant list:
   - **Replace**: translated grants + manual grants kept per `KeepManualGrantsFor`.
   - **Merge**: existing ∪ translated (by `GrantIdentity`; existing identical grants untouched).
   - **Remove**: existing minus grants whose identity equals a translated grant.
5. Other `What` fields (client access, workstations, session policy, group memberships, language,
   MFA policy): Replace/Merge/Remove semantics per field type (sets: union/difference; scalars:
   copy on Replace and Merge; untouched on Remove).
6. Diff per target → preview; warnings: sensitive permissions gained (by target), users gaining
   configuration permissions, constraints relaxed (e.g. four-eyes removed), targets with no change.
7. **Apply**: build a CFG draft with one `rights.grants` entry per changed target plus
   `identity.user` / `rights.userGroup` entries for other fields → publish (one configuration
   version; approval rules apply) → store `CopyOperation` with the applied diff and config version.
   Preview must be fresh: if any target's rights changed since the preview → `rights.copy.stale_preview`.
8. **Undo**: CFG rollback of that configuration version **restricted to the operation's entries**,
   validated against current state (conflicts reported if targets changed meanwhile).
9. **Re-apply**: new preview from the stored request (same source, current source rights).

Limits: 2,000 targets per operation (`rights.copy.too_many_targets`).

### 8.2 Compare

`CompareAsync(a, b)`: grants grouped by permission family: only in A, only in B, in both with
different scopes/constraints/schedules, identical; plus field differences.

## 9. Drift indicator (T3.05)

```csharp
public enum DriftStatus { NotFromTemplate, InSync, TemplateChanged, ModifiedManually, TemplateChangedAndModified }
public interface IDriftService
{
    DriftInfo Get(UserId user);                         // cached; recomputed on rights changes of the user or the template
    IReadOnlyDictionary<UserId, DriftInfo> GetMany(IReadOnlyCollection<UserId> users);
}
public sealed record DriftInfo(DriftStatus Status, UserId? Template, Guid? LastOperation, int ManualAdditions, int MissingFromTemplate, int ChangedFromTemplate);
```

For a user whose grants include `CopiedFrom` with `SourceWasTemplate` (latest operation per
template): **TemplateChanged** if the template's current grants (translated with that operation's
mapping) differ from what the operation applied; **ModifiedManually** if grants from that operation
were removed/changed or manual grants were added afterwards. Shown in user lists with a "re-apply"
action.

## 10. Escalation protection (T3.05)

`EscalationGuard.CanGrant(actor, grant)`:

- Administrators: yes.
- Actor needs `config.rights.edit` with a scope containing the **target user's** management scope
  (v1: `config.rights.edit` is site-wide (None) — scoped user management is a later extension).
- Actor must hold the permission (pattern ⊇ granted pattern) with a scope that **contains** the
  granted scope (containment check: each component of the granted scope is within the union of
  the actor's matching grant scopes; `Everything` only if the actor has `Everything`), a schedule
  that is the same or always, and constraints **not weaker** than the actor's own effective
  constraints (cannot grant without four-eyes if the actor needs four-eyes).
- Nobody can edit their own grants (`rights.self_change_forbidden`), except the bootstrap path:
  when no administrator exists, `wpctl reset-admin` (IDN) creates one.
- Removing grants from others is allowed with `config.rights.edit` (no containment needed).

## 11. Data (`wp_rights`)

| Table | Columns |
|---|---|
| `grants` | id, user_id, permission, scope jsonb, schedule_calendar, constraints jsonb, source jsonb, identity_hash char(64), created_at, created_by — unique (user_id, identity_hash); index user_id |
| `user_groups` | id, name jsonb, description, managers uuid[], row_version, timestamps |
| `user_group_members` | group_id, user_id — PK both |
| `copy_operations` | id, request jsonb, actor, at, config_version, applied jsonb, undone_by, undo_of |
| `copy_previews` | id, request jsonb, actor, created_at, expires_at, preview jsonb, target_versions jsonb (stale detection) |

## 12. API

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/permissions` | authenticated | Catalogue with labels (for editors) |
| `GET /api/v1/users/{id}/grants` | `config.rights.view` (or self) | Grants |
| `PUT /api/v1/users/{id}/grants` | `config.rights.edit` (quick edit via CFG) | Full replacement |
| `GET /api/v1/users/{id}/effective?permission=&device=` | `config.rights.view` (or self) | Decision + explanation (matching grants, constraints) |
| `GET /api/v1/me/permissions` | authenticated | Permission codes held (with "anywhere" semantics) for UI gating; also in `/auth/me` |
| `GET/POST/PATCH/DELETE /api/v1/user-groups…`, `POST …/bulk` | `config.users.view/edit` or group manager | |
| `POST /api/v1/rights-copy/preview` | `config.rights.edit` | `CopyRequest` → preview |
| `POST /api/v1/rights-copy/apply` | `config.rights.edit` | `{ previewId, reason }` |
| `GET /api/v1/rights-copy/operations` | `config.rights.view` | History |
| `POST /api/v1/rights-copy/operations/{id}/undo` / `reapply-preview` | `config.rights.edit` | |
| `GET /api/v1/rights-copy/compare?a=&b=` | `config.rights.view` | |
| `GET /api/v1/users/drift?ids=` | `config.rights.view` | Drift per user |

## 13. Errors

`rights.permission.unknown`, `rights.grant.invalid_scope`, `rights.grant.constraint_not_allowed`,
`rights.grant.duplicate`, `rights.outside_schedule`, `rights.client_kind`,
`rights.self_change_forbidden`, `rights.escalation.denied` (args `permission`, `reason`),
`rights.copy.too_many_targets`, `rights.copy.stale_preview`, `rights.copy.preview_expired`,
`rights.copy.undo_conflict`, `rights.group.not_found`.

## 14. Observability

| EventId | Level | Message |
|---|---|---|
| 2801 | Information | Rights changed for {Count} users (version {Version}) |
| 2810 | Information | Rights copy {OperationId} by {Actor}: {Targets} targets, +{Added} −{Removed} ~{Changed} |
| 2811 | Information | Rights copy {OperationId} undone by {Actor} |
| 2820 | Warning | Escalation denied for {Actor}: {Permission} ({Reason}) |

Metrics: `watchpoint.rights.authorize.duration` (histogram µs), `watchpoint.rights.cache.hits`
(counter), `watchpoint.rights.scope.recompute` (counter).

## 15. Tests

| Id | Test |
|---|---|
| RGT-U-001 | Catalogue registration; command permissions generated from MOD and regenerated on package types |
| RGT-U-002 | Pattern matching: `commands.openable.*`, `*`, exact |
| RGT-U-003 | Grant validation: unknown permission, constraint not allowed, scope kind mismatch, duplicate identity |
| RGT-U-004 | Scope: Everything |
| RGT-U-005 | Scope: location subtree incl. devices moved in later |
| RGT-U-006 | Scope: servers, categories, types (IsA), tags any/exclude combination |
| RGT-U-007 | Scope: explicit devices OR'ed with the rest |
| RGT-U-008 | Schedule active/inactive (calendar) → outside_schedule code |
| RGT-U-009 | Administrators allowed everything |
| RGT-U-010 | Template/disabled/expired users denied |
| RGT-U-011 | Least restrictive constraints across matching grants |
| RGT-U-012 | Client kind constraint |
| RGT-U-013 | PriorityRange hull for alarms.view |
| RGT-U-014 | System target only for Everything/System grants |
| RGT-U-015 | HasPermission "anywhere" semantics |
| RGT-U-016 | Visible scope bitsets for devices and locations |
| RGT-U-017 | Visible location includes locations containing visible devices |
| RGT-U-018 | ValidUntil = next schedule transition |
| RGT-U-019 | Cache invalidation on grants change |
| RGT-U-020 | Cache invalidation on registry change (location move, tag change) |
| RGT-U-021 | Cache invalidation on calendar change and schedule edge |
| RGT-U-022 | `rights.changed` published only when effective rights changed |
| RGT-U-023 | Explain returns matching grants and why others did not match |
| RGT-U-024 | Authorize performance warm p99 < 5 µs (benchmark) |
| RGT-U-025 | Bitset memory for 50,000 devices × 200 users within budget |
| RGT-U-026 | ICurrentUser adapter: permission filter and visible scope |
| RGT-U-027 | Command authorizer constraints mapping (reason, four-eyes, step-up, client kinds) |
| RGT-U-028 | Script identity evaluated like users |
| RGT-U-029 | ALM visibility with priority range |
| RGT-U-030 | User group CRUD, managers can edit membership only |
| RGT-U-031 | Group bulk actions call IDN (disable, extend validity, sign out, force password change, MFA reset) |
| RGT-U-032 | Groups hold no rights (grants API rejects group targets) |
| RGT-U-033 | `rights.grants` CFG object type validate/prepare/complete |
| RGT-U-034 | Me permissions endpoint content |
| RGT-U-035…050 | Scope containment checks (each scope component, unions of actor grants, Everything) used by escalation guard and copy |
| RGT-U-051 | Copy: target resolution from users and groups, deduplicated, exclusions applied |
| RGT-U-052 | Copy: actor and source excluded with skip reasons |
| RGT-U-053 | Copy: scope translation of locations/servers/devices/tags |
| RGT-U-054 | Copy: untranslatable references kept with warning |
| RGT-U-055 | Copy Replace mode |
| RGT-U-056 | Copy Replace keeps manual grants matching KeepManualGrantsFor |
| RGT-U-057 | Copy Merge mode deduplicates by identity |
| RGT-U-058 | Copy Remove mode removes identical grants only |
| RGT-U-059 | Copy other fields per What flags and mode |
| RGT-U-060 | Copy source tagging CopiedFrom with template flag |
| RGT-U-061 | Copy escalation: grants actor cannot give are skipped with reasons |
| RGT-U-062 | Copy warnings: sensitive permissions gained, configuration rights gained, constraints relaxed |
| RGT-U-063 | Preview counts and per-target diffs |
| RGT-U-064 | Preview expiry 15 min |
| RGT-U-065 | Stale preview detection |
| RGT-U-066 | Apply creates one CFG version with all entries |
| RGT-U-067 | Apply with approval-required types returns awaiting approval |
| RGT-U-068 | Copy operation stored with applied diff |
| RGT-U-069 | Undo restricted to operation entries |
| RGT-U-070 | Undo conflict when targets changed meanwhile |
| RGT-U-071 | Re-apply preview uses current source rights |
| RGT-U-072 | 2,000 target limit |
| RGT-U-073 | Compare two users grouped by family |
| RGT-U-074 | Drift InSync |
| RGT-U-075 | Drift TemplateChanged |
| RGT-U-076 | Drift ModifiedManually (removed, changed, manual additions) |
| RGT-U-077 | Drift both |
| RGT-U-078 | Drift NotFromTemplate |
| RGT-U-079 | Drift recomputed on template change |
| RGT-U-080 | Escalation: administrators unrestricted |
| RGT-U-081 | Escalation: permission not held → denied |
| RGT-U-082 | Escalation: wider scope → denied |
| RGT-U-083 | Escalation: weaker constraints → denied |
| RGT-U-084 | Escalation: schedule must be same or always |
| RGT-U-085 | Self change forbidden |
| RGT-U-086 | Removing others' grants allowed |
| RGT-U-087 | Directory mapping uses copy engine in Merge mode with DirectoryMapping source (IDN integration) |
| RGT-U-088 | Copy audit records (AUD sink) |
| RGT-U-089 | Copy preview performance: 500 targets × 50 grants < 2 s |
| RGT-U-090 | Translations for all permission labels in en/fr/ar |
| RGT-I-001 | Operator with Building A grants: device lists, states, alarms, events filtered end-to-end |
| RGT-I-002 | Rights reduced while connected → live resync and 403 on next REST call |
| RGT-I-003 | Command denied outside schedule end-to-end |
| RGT-I-004 | Four-eyes command via grant constraint end-to-end |
| RGT-I-005 | Copy template → group of 50 users, apply, verify grants, undo |
| RGT-I-006 | Copy with scope translation Building A → Building B |
| RGT-I-007 | Drift statuses after template change end-to-end |
| RGT-I-008 | Persistence of grants, groups, operations |

## 16. Tasks

| Task | Sections | Tests |
|---|---|---|
| T3.04 | §4–§7, §11–§14 | RGT-U-001…RGT-U-050, RGT-I-001…RGT-I-004, RGT-I-008 |
| T3.05 | §8–§10 | RGT-U-051…RGT-U-090, RGT-I-005…RGT-I-007 |
