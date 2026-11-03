# UIF — UI Configuration Side

| | |
|---|---|
| Spec id | UIF |
| Package | `src/ui/packages/configuration` (`@watchpoint/configuration`) |
| Milestone | M3 |
| Tasks | T3.10 (§4–§6), T3.11 (§7), T3.12 (§8), T3.13 (§9), T3.14 (§10–§11) |
| Design docs | [Desktop client §4](../../docs/clients/desktop-client.md), [Configuration management](../../docs/modules/configuration-management.md), [Users and rights](../../docs/modules/users-and-rights.md) |

## 1. Purpose and scope

The configuration side of the desktop client: shell and side switch, drafts (draft bar, diff,
publish, approval), schema-generated forms, validation panel, and the M3 editors —
integrations and connections, devices/locations/tags/links with bulk edit, alarm rules and
procedures, users, groups, rights editor, rights-copy wizard, sessions, maintenance mode and the
audit viewer.

Later editors plug in with the same framework: synoptics (SYN, M6), automations (AUT, M8),
scripts (SCR, M8), notifications (NOT, M5), licence (LPT, M7), cluster and federation (M9).

The package is loaded **only by the desktop app** (DSK). The web app never bundles it (build
check UIF-U-004). All calls use `ConfigurationEndpoint()` APIs, which refuse non-desktop
client kinds (API §5).

## 2. Dependencies

`@watchpoint/api-client`, `live`, `model`, `i18n`, `ui`, `operation` (shared shell parts,
`EntityPicker`), React Router 7, TanStack Query 5, TanStack Table/Virtual, react-hook-form 7 +
Ajv 8 (JSON Schema validation, MIT), Monaco editor (MIT, CEL editor via `@watchpoint/cel`
TS implementation of EXP for syntax/type checking), `jsondiffpatch` (MIT) for diff display,
SheetJS CE (`xlsx`, Apache-2.0) for import preview.

## 3. Package layout

```
packages/configuration/src/
├── app/ConfigurationApp.tsx          route tree under /config, permission gating
├── shell/                            ConfigNavigator (sections by permission), SideSwitch, ConfigBanner (maintenance, licence)
├── drafts/                           DraftProvider, DraftBar, DraftPicker, DiffView, PublishDialog, ApprovalPanel, ConflictResolver
├── forms/                            ObjectEditor, SchemaForm wrappers, useDraftObject, useQuickEdit, FieldProblems
├── validation/                       ValidationPanel, ProblemItem, FixButton
├── integrations/  (T3.11)            PackagesCatalogue, PackageDetails, InstallPackageDialog, ConnectionWizard, ConnectionDetails, DiscoveryReview
├── registry/      (T3.12)            DevicesGrid, DeviceEditor, LocationsEditor, TagsEditor, LinksEditor, BulkEditDialog, ImportExportDialog
├── alarms/        (T3.13)            AlarmRulesList, AlarmRuleEditor, ScopeBuilder, TriggerEditor, CelEditor, OverridesEditor, ProceduresList, ProcedureEditor
├── users/         (T3.14)            UsersGrid, UserEditor, GroupsEditor, RightsEditor, GrantDialog, EffectiveRightsInspector, RightsCopyWizard, CompareUsers, DriftBadge
├── system/        (T3.14)            SessionsGrid, MaintenanceDialog, MaintenanceBanner, AuditViewer
└── shared/                           ConfigPage, Toolbar, EmptyState, usePermission, routes.ts
```

## 4. Shell, side switch and gating (T3.10)

- Route root `/config/*`. The **side switch** in the top bar (Operation ⇄ Configuration) is
  visible only if the user holds any `config.*` permission (`/me/permissions`). Shortcut
  `Ctrl+Shift+C`. Switching keeps both sides' state (separate tab sets).
- `ConfigNavigator` sections (each shown only when its view permission is held):

| Section | Route | Permission |
|---|---|---|
| Integrations | `/config/integrations` | `config.integrations.view` |
| Devices | `/config/devices` | `config.registry.view` |
| Locations and tags | `/config/locations`, `/config/tags` | `config.registry.view` |
| Alarms | `/config/alarm-rules`, `/config/procedures` | `config.alarms.view` |
| Calendars | `/config/calendars` | `config.calendars.edit` |
| Users and rights | `/config/users`, `/config/groups`, `/config/rights-copy` | `config.users.view` / `config.rights.view` |
| Sessions | `/config/sessions` | `admin.sessions.view` |
| Maintenance | dialog from top bar | `config.maintenance` |
| Audit | `/config/audit` | `admin.audit.view` |
| Drafts and history | `/config/drafts`, `/config/versions` | any `config.*.edit` |

- `usePermission(code, target?)` hook: uses `/me/permissions` for "anywhere" checks; editors
  that depend on scope ask the API (`/users/{myId}/effective`, RGT §12) lazily, and server errors remain the
  authority (403 shows a translated message, never a crash).
- `ConfigBanner`: maintenance active (MNT §5.5), approval waiting for me, licence banners (M7).

## 5. Drafts and editing (T3.10)

### 5.1 Draft context

- `DraftProvider` holds the **active draft** (or none = quick-edit mode). The user picks/creates
  a draft in `DraftPicker`; the choice persists per user (platform storage).
- Editors read objects through `useDraftObject(type, id)`: with an active draft →
  `GET /drafts/{id}/objects/{type}/{objectId}`; without → module read endpoint.
- Saving in an editor: with a draft → `PUT /drafts/{id}/entries` (returns problems shown inline);
  without a draft → quick edit through the module endpoint (`If-Match` with the row version),
  `202` (approval required) shows "awaiting approval" toast with link.
- Objects changed in the active draft show a **draft dot** in grids and trees; deleted ones are
  struck through.

### 5.2 Draft bar

Bottom bar while a draft is active: name, entry count, problem counts (errors/warnings), buttons
**Diff**, **Validate**, **Publish…**, **Discard**. Live updates through topic `drafts` (CFG §6.1)
(changes by collaborators, validation finished, publish result).

### 5.3 Diff view

`DiffView` lists entries grouped by object type: operation icon, object label, base version vs
live version (if live changed since base → "changed meanwhile" marker linking to
`ConflictResolver`). Per entry: side-by-side field diff (`jsondiffpatch` HTML formatter
restyled with design tokens, RTL-aware), revert button.

### 5.4 Publish dialog

Comment (required if site setting says so), "publish now" / "schedule at" (date-time input),
shows approval requirement (types needing approval), blocking errors list (publish disabled
while errors exist), warnings (acknowledge checkbox). Result: version number with link to
version page, or "awaiting approval", or failure with problems.

### 5.5 Approval panel

For `config.approve` holders: drafts awaiting approval, diff, approve / reject with reason. A
user cannot approve their own draft (button hidden; server enforces).

### 5.6 Conflicts

`ConflictResolver` shows three columns (base, live, mine) per conflicting field from CFG's merge
result; per field choose live/mine or edit; resolution updates the entry with the new base
version.

### 5.7 Versions page

`/config/versions`: list of published versions (who, when, comment, entries count), version
detail (diff), **Roll back** (creates a draft with inverse entries, CFG §7.3) — opens the draft.

### 5.8 Soft locks

On opening an editor for a lockable type, the UI acquires a soft lock (CFG §9) and renews it
every 60 s; if another user holds it, the editor opens read-only with "being edited by X since
…", and holders of `config.locks.break` see "take over". Lock released on close.

## 6. Forms and validation (T3.10)

- `ObjectEditor<T>`: page layout (title, toolbar: save, revert, delete, history), sections from
  schema `x-wp-ui.groups`, `SchemaForm` (UIC) for schema-driven parts, custom components for
  typed parts (pickers, CEL editor). Unsaved changes guard on navigation.
- Client-side validation with Ajv against the same JSON Schemas (from SCM `schemas/` build
  output or package settings schema endpoints) — immediate feedback; server problems are the
  authority and merged by JSON pointer path.
- `FieldProblems` shows problems for a field (translated with `translateError`, args formatted).
- `ValidationPanel` (bottom panel tab "Problems"): all problems of the active draft grouped by
  object, filter errors/warnings, click → navigates to the object editor and focuses the field
  (by JSON pointer → field id mapping in `SchemaForm`), **Fix** buttons apply `FixSuggestion`
  JSON patches to the entry.
- Secret fields: write-only; sending `{ "$secret": value }`; displays "•••• (set)" for
  `$secretRef` / set markers.

## 7. Integrations (T3.11)

### 7.1 Packages catalogue (`/config/integrations/packages`)

Grid of installed packages: name, publisher, version(s), state (Active, Disabled, Invalid,
Superseded), signature status (signed by Watchpoint / dev-signed warning), connector types,
connections using it. Details page: manifest info, documentation (Markdown rendered with
`react-markdown` + sanitisation), versions with enable/disable, problems. **Install** dialog
(`config.integrations.install`): drag-and-drop `.wpkg`, upload with progress, shows
verification result (signature, compatibility, problems) before confirming enable.

### 7.2 Connection wizard

Steps:

1. **Type**: pick connector type from active packages (search, category filter).
2. **Settings**: `SchemaForm` from `…/settings-schema` (groups, advanced toggle, secrets).
3. **Test**: `POST /connections/test` with unsaved settings; shows result steps (reachability,
   authentication, version) with messages and duration; can continue with warnings, not with
   failure unless "save anyway" (confirm).
4. **Placement**: name (LocalizedNameInput), default location (picker), auto-import toggle,
   host mode (in-process / isolated / remote — options from manifest and M4 availability).
5. **Discovery**: create connection (draft entry or quick edit), start, `POST …/discover`
   (job with progress via `jobs` topic), then `DiscoveryReview`.
6. **Review and import**: tree of discovered servers/devices with type, native id, proposed
   location; select all/none, per subtree; set location/tags for selection; unknown types
   flagged; **Import** creates REG entries in the draft (or quick edit). Summary at the end.

### 7.3 Connection details

Status (live `connections` topic): state, reason, since, health, counters; actions start/stop/
restart (`config.integrations.operate`), rediscover, diagnostics actions from `IDiagnostics`,
protocol trace (duration picker, download link), state timeline chart (simple bar timeline),
settings edit (same form), devices of this connection (link to devices grid filtered).

## 8. Registry editors (T3.12)

- **DevicesGrid** (`DataGrid` server-side): columns name, type, location, server, tags,
  enabled, maintenance, orphaned, draft marker; filters as REG query parameters; selection for
  bulk actions.
- **DeviceEditor**: name (localized), location, tags, enabled, maintenance, attributes form
  (type schema), custom fields (site-defined), geometry (lat/lon/azimuth/fov/range; map picker in
  M6), links tab (`LinksEditor`), read-only native info (server, native id, vendor name,
  capabilities). Orphaned devices show "removed from source" with delete/keep actions.
- **LocationsEditor**: tree with drag-and-drop move (confirm when moving subtrees containing
  devices: count shown), create/rename/delete (delete blocked if not empty → problem with fix
  "move contents to parent"), kind, time zone, address.
- **TagsEditor**: list with colour, description, usage count; merge two tags (bulk entries).
- **LinksEditor**: links of a device: kind (camera-for, door-of, zone-of…), target picker,
  bidirectional display.
- **BulkEditDialog**: for N selected devices (≤ 5,000 per operation): set location, add/remove
  tags, enable/disable, maintenance on/off, set attribute value (only attributes common to all
  selected types); preview count; creates N entries in the active draft (or a hidden draft
  through quick edit with progress).
- **ImportExportDialog**: export devices/locations/tags as CSV/XLSX (CFG tabular) with column
  chooser; import: file pick → server-side parse → preview table with per-row problems → apply to
  draft. JSON bundle export/import of selected objects (CFG §10.1).

## 9. Alarm rules and procedures (T3.13)

- **AlarmRulesList**: rules with name, trigger summary (translated sentence), scope summary,
  priority, enabled, procedure, overrides count; filters; duplicate rule.
- **AlarmRuleEditor**:
  - General: name, description, priority (slider with named levels), category, enabled,
    active calendar.
  - **ScopeBuilder**: everything / locations / types / categories / tags any / exclude / devices;
    live **"matches N devices"** counter (`POST /alarm-rules/scope-preview`, ALM API), list of first 50.
  - **TriggerEditor**: kind (state, property condition, event, health, system event); for
    conditions a form-based builder (property, operator, value) that generates CEL, with
    "advanced" switch to raw CEL in `CelEditor`.
  - **CelEditor**: Monaco with CEL language (tokens, brackets), completion of variables from the
    scope's types (`device.*`, `prop("…")`, functions from EXP catalogue), live type-check with
    the TS implementation (markers), "test against device" panel evaluating with current values
    (`POST /alarm-rules/test-condition`, ALM API).
  - Lifecycle: start/end delays, auto-close, acknowledgement required, re-trigger policy.
  - Escalation steps (ordered list), actions (per kind schema forms), audience.
  - Procedure picker.
  - **OverridesEditor**: overrides per device/tag/location (enabled, priority, delays,
    procedure) — table with "most specific wins" explanation.
- **ProcedureEditor**: ordered steps (instruction text localized, checkbox, choice with
  options, text input, mandatory flag, conditional visibility on previous answers), reorder by
  drag-and-drop and keyboard, preview pane rendering the `ProcedureRunner` from UIO in
  read-only test mode.

## 10. Users, groups and rights (T3.14)

- **UsersGrid**: columns name, login, kind (user/template), groups, enabled, validity, last
  sign-in, MFA, **drift badge** (RGT §9: `DriftBadge` with tooltip and "re-apply" action);
  filters by group, kind, drift status; bulk actions through groups API or per selection
  (disable, extend validity, force password change, sign out, MFA reset) with confirmation.
- **UserEditor** tabs: Identity (name, login, e-mail, language, validity, enabled, directory
  source read-only), Security (password reset, MFA status/reset, client access kinds,
  workstation restrictions, session policy), Groups, **Rights**, Sessions (that user's), Audit
  (entries for that user, link to viewer). Template users show a "template" chip and no security
  tab.
- **RightsEditor**: grants grouped by permission family (accordion), each grant row: permission
  label, scope summary, schedule, constraints chips, source (manual / copied from X / directory
  mapping) with link to the copy operation; add/edit through **GrantDialog**:
  permission tree picker (family → permission, with descriptions and "sensitive" warnings),
  scope builder (same component as alarms, kinds allowed by the permission's scope kind),
  schedule calendar picker, constraints allowed by the definition. Escalation errors from the
  server are shown next to the grant (`rights.escalation.denied` with reason).
  Self-editing: the rights tab of the current user is read-only with explanation.
- **EffectiveRightsInspector**: pick permission + device/location → decision, effective
  constraints and explanation (matching grants and why others did not match) from
  `/users/{id}/effective`.
- **GroupsEditor**: name, description, members (picker + list), managers.
- **RightsCopyWizard** (`/config/rights-copy`):
  1. Source user (templates first).
  2. Targets: users and/or groups (members count shown), exclusions toggles.
  3. What to copy (checkbox list of `CopyWhat`) and mode (Replace / Merge / Remove) with plain
     explanations.
  4. Scope translation: table of locations/servers/devices/tags referenced by the source's
     grants; for each, "keep" or map to (picker); unmapped references listed.
  5. **Preview**: totals, warnings (sensitive gains, configuration rights gained, constraints
     relaxed), per-target expandable diff (added green / removed red / changed amber, with icons
     and text for accessibility), skipped grants with reasons, skipped targets; preview age
     countdown (15 min).
  6. Apply (reason) → result with copy operation link; **History** tab lists operations with
     undo and re-apply (re-apply opens step 5 with a new preview).
- **CompareUsers**: two user pickers, side-by-side grouped by family: only in A, only in B,
  different, identical (collapsed).

## 11. System pages (T3.14)

- **SessionsGrid** (`admin.sessions.view`): user, client kind, workstation, IP, started, last
  activity, side; live updates; **Sign out** (`admin.sessions.manage`) single/bulk with reason.
- **MaintenanceDialog** (`config.maintenance`): scope (whole site / locations picker), reason,
  expected end (with quick choices +1 h/+4 h/end of day), auto-end, notification mode,
  pause non-safety automations, notify group; **Impact** step calls `/maintenance/impact` and
  shows sessions to close, users locked out (list) vs keeping access, devices/alarms affected,
  what still runs; confirmation checkbox "I understand that N operators will be signed out";
  start. When active: banner with end/extend buttons (`MaintenanceBanner`).
- **AuditViewer** (`admin.audit.view`): filters (time range, actor picker, action prefix tree
  from catalogue, object type + id, outcome, min severity, text, correlation id), virtualised
  grid, detail drawer (actor info, objects with links, reason, details rendered as JSON tree,
  diffs rendered with `DiffView` when details contain a diff), chain verification status chip
  with "verify now" (job), CSV export.

## 12. Tests

Component and hook tests with Vitest + Testing Library + MSW + `FakeLiveHub`
(`@watchpoint/testing`); end-to-end in [tests/e2e-scenarios.md](../tests/e2e-scenarios.md)
(E-020…E-028).

| Id | Test |
|---|---|
| UIF-U-001 | Side switch visible only with a `config.*` permission; shortcut works; both sides keep state |
| UIF-U-002 | Navigator sections gated per permission |
| UIF-U-003 | 403 from API shows translated message inside the page |
| UIF-U-004 | Web app bundle contains no `@watchpoint/configuration` module (build analysis test) |
| UIF-U-005 | DraftPicker create/select persists per user |
| UIF-U-006 | useDraftObject reads through draft endpoint when a draft is active, module endpoint otherwise |
| UIF-U-007 | Save with draft → entry PUT; problems shown inline |
| UIF-U-008 | Save without draft → quick edit with If-Match; 412 shows reload/merge prompt |
| UIF-U-009 | Quick edit 202 → awaiting approval toast with link |
| UIF-U-010 | Draft dots and strike-through for deleted objects in grids/trees |
| UIF-U-011 | Draft bar counts update from `drafts` live topic |
| UIF-U-012 | Diff view grouping, field diff, revert entry |
| UIF-U-013 | Diff view "changed meanwhile" marker opens conflict resolver |
| UIF-U-014 | Publish dialog: blocked by errors, warnings acknowledgement, comment required setting |
| UIF-U-015 | Publish schedule at date-time (time zone aware) |
| UIF-U-016 | Publish result: version link / awaiting approval / failure problems |
| UIF-U-017 | Approval panel approve/reject; own draft cannot be approved |
| UIF-U-018 | Conflict resolver per field choice updates entry base version |
| UIF-U-019 | Versions list and rollback opens new draft |
| UIF-U-020 | Soft lock acquire/renew/release; read-only when held by other; take over with permission |
| UIF-U-021 | ObjectEditor unsaved changes guard |
| UIF-U-022 | Schema groups and advanced toggle render |
| UIF-U-023 | Ajv client validation messages translated |
| UIF-U-024 | Server problems merged by JSON pointer onto fields |
| UIF-U-025 | Validation panel grouping and filtering |
| UIF-U-026 | Validation panel click navigates and focuses field |
| UIF-U-027 | Fix button applies JSON patch to entry |
| UIF-U-028 | Secret fields: write-only, `$secret` sent, "set" display |
| UIF-U-029 | RTL layout of config shell, diff view and forms |
| UIF-U-030 | Keyboard accessibility (axe checks) of shell, draft bar, publish dialog |
| UIF-U-031 | Packages grid states and signature badges (dev-signed warning) |
| UIF-U-032 | Package details renders sanitised Markdown docs |
| UIF-U-033 | Install dialog upload progress and verification result before enable |
| UIF-U-034 | Install gated by `config.integrations.install` |
| UIF-U-035 | Wizard step 1 type search and filter |
| UIF-U-036 | Wizard step 2 settings form from schema endpoint incl. secrets |
| UIF-U-037 | Wizard step 3 test result steps; failure blocks unless save anyway |
| UIF-U-038 | Wizard step 4 placement and host mode options |
| UIF-U-039 | Wizard step 5 discovery job progress via jobs topic |
| UIF-U-040 | Discovery review selection (all/none/subtree) |
| UIF-U-041 | Discovery review set location/tags for selection |
| UIF-U-042 | Unknown types flagged and not importable |
| UIF-U-043 | Import creates draft entries / quick edit; summary |
| UIF-U-044 | Connection details live status updates |
| UIF-U-045 | Start/stop/restart gated by `config.integrations.operate` |
| UIF-U-046 | Diagnostics actions listed and executed with result |
| UIF-U-047 | Protocol trace duration and download |
| UIF-U-048 | State timeline rendering |
| UIF-U-049 | Settings edit with secrets replacement |
| UIF-U-050 | Wizard translations en/fr/ar complete (key coverage) |
| UIF-U-051 | Devices grid filters → REG query params; server paging |
| UIF-U-052 | Devices grid draft markers |
| UIF-U-053 | Device editor fields and attribute form from type schema |
| UIF-U-054 | Custom fields typed inputs |
| UIF-U-055 | Geometry fields validation |
| UIF-U-056 | Orphaned device actions |
| UIF-U-057 | Locations tree drag-and-drop move with confirmation count |
| UIF-U-058 | Location delete blocked with fix "move contents to parent" |
| UIF-U-059 | Location time zone and kind |
| UIF-U-060 | Tags editor colour/usage; merge tags creates bulk entries |
| UIF-U-061 | Links editor add/remove, bidirectional display |
| UIF-U-062 | Bulk edit: location, tags add/remove, enable, maintenance |
| UIF-U-063 | Bulk edit: common attributes only |
| UIF-U-064 | Bulk edit 5,000 limit and progress |
| UIF-U-065 | Export CSV/XLSX with column chooser |
| UIF-U-066 | Import preview with per-row problems |
| UIF-U-067 | Import apply to draft |
| UIF-U-068 | JSON bundle export/import of selection |
| UIF-U-069 | Registry editors respect location-scoped edit permission (read-only outside scope) |
| UIF-U-070 | Devices grid 100,000 rows smooth (virtualisation performance test) |
| UIF-U-071 | Alarm rules list summaries translated |
| UIF-U-072 | Duplicate rule |
| UIF-U-073 | Rule general fields and priority levels |
| UIF-U-074 | Scope builder matches counter and sample list |
| UIF-U-075 | Trigger builder generates CEL |
| UIF-U-076 | Advanced CEL switch preserves expression; back to builder when representable |
| UIF-U-077 | CEL editor tokens and completion from scope types and functions |
| UIF-U-078 | CEL editor type-check markers (TS implementation) |
| UIF-U-079 | CEL test against device with current values |
| UIF-U-080 | Lifecycle fields validation |
| UIF-U-081 | Escalation steps ordering |
| UIF-U-082 | Actions forms per kind schema |
| UIF-U-083 | Overrides editor and most-specific explanation |
| UIF-U-084 | Procedure editor step kinds |
| UIF-U-085 | Procedure reorder by drag-and-drop and keyboard |
| UIF-U-086 | Conditional visibility of steps |
| UIF-U-087 | Procedure preview uses UIO ProcedureRunner in test mode |
| UIF-U-088 | Localized instruction texts |
| UIF-U-089 | Rule editor saves to draft with problems |
| UIF-U-090 | RTL Monaco editor and procedure editor |
| UIF-U-091 | Users grid columns, filters, drift badge with re-apply |
| UIF-U-092 | Bulk user actions with confirmation |
| UIF-U-093 | User editor identity tab; directory users read-only fields |
| UIF-U-094 | Security tab: password reset, MFA reset, client kinds, workstations, session policy |
| UIF-U-095 | Template users: chip, no security tab |
| UIF-U-096 | Rights editor groups grants by family with source links |
| UIF-U-097 | Grant dialog permission tree with sensitive warnings |
| UIF-U-098 | Grant dialog scope kinds restricted by permission |
| UIF-U-099 | Grant dialog constraints allowed by definition |
| UIF-U-100 | Escalation denied shown on grant |
| UIF-U-101 | Own rights read-only |
| UIF-U-102 | Effective rights inspector decision and explanation |
| UIF-U-103 | Groups editor members and managers |
| UIF-U-104 | Rights copy wizard step navigation and validation |
| UIF-U-105 | Targets from users and groups with member counts and exclusions |
| UIF-U-106 | What/mode explanations |
| UIF-U-107 | Scope translation table: keep/map, unmapped listed |
| UIF-U-108 | Preview totals, warnings, per-target diffs (colour + icon + text) |
| UIF-U-109 | Preview skipped grants and targets with reasons |
| UIF-U-110 | Preview expiry countdown and re-preview |
| UIF-U-111 | Apply with reason → operation link; stale preview error handled |
| UIF-U-112 | History undo and re-apply |
| UIF-U-113 | Compare users grouped |
| UIF-U-114 | Sessions grid live and sign out with permission |
| UIF-U-115 | Maintenance dialog validation and quick end choices |
| UIF-U-116 | Maintenance impact step and confirmation checkbox required |
| UIF-U-117 | Maintenance banner extend/end |
| UIF-U-118 | Audit viewer filters → query params; detail drawer; diff rendering |
| UIF-U-119 | Audit verification chip and verify now job |
| UIF-U-120 | Audit CSV export |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T3.10 | §3–§6 | UIF-U-001…UIF-U-030 |
| T3.11 | §7 | UIF-U-031…UIF-U-050 |
| T3.12 | §8 | UIF-U-051…UIF-U-070 |
| T3.13 | §9 | UIF-U-071…UIF-U-090 |
| T3.14 | §10–§11 | UIF-U-091…UIF-U-120 |
