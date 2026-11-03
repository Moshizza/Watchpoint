# UIO — UI Operation Side

| | |
|---|---|
| Spec id | UIO |
| Package | `src/ui/packages/operation` (`@watchpoint/operation`) |
| Milestone | M1 (part 1: §4–§6), M2 (part 2: §7–§11) |
| Tasks | T1.23 (§4–§6), T2.14 (§7–§9), T2.15 (§10–§11) |
| Design docs | [Desktop client §3](../../docs/clients/desktop-client.md#3-operation-side--layout-and-features), [Events and alarms §5–§6](../../docs/modules/events-and-alarms.md) |

## 1. Purpose and scope

All operator screens shared by the desktop and web apps: sign-in, application shell, device
explorer, device page, connection status (M1); alarm console, alarm details and procedures,
sounds and pop-ups, event journal, command buttons and command journal (M2).

Synoptics (SYN) and video (VID) add their own packages later and plug into the shell's panels.

API names in part 2 refer to the ALM, CMD and EVT specs (M2); field names must follow those specs
when they differ from the examples here.

## 2. Dependencies

`@watchpoint/api-client`, `live`, `model`, `i18n`, `ui`; React Router 7 (MIT) for routing.

## 3. Package layout

```
packages/operation/src/
├── app/OperationApp.tsx          route tree, providers slot (apps provide platform adapter)
├── shell/                        top bar, navigator, context panel, bottom panel, user menu, search
├── auth/                         SignInPage, ChangePasswordStep, MfaStep (M3 UI), SessionGuard
├── explorer/                     LocationTreePanel, ServerTreePanel, FavoritesPanel, DeviceListView
├── device/                       DevicePage, OverviewTab, ChildrenTab, LinksTab, AttributesTab, DeviceSummaryCard
├── alarms/          (M2)         AlarmConsole, AlarmRow, AlarmFilters, AlarmDetails, ProcedureRunner, AlarmTimeline, SoundManager, PopupManager
├── events/          (M2)         EventJournal, EventRow, EventFilters, EventDetails
├── commands/        (M2)         CommandButton, CommandForm, CommandStatus, CommandJournal, FourEyesPanel
└── shared/                       hooks (useDevice, useLocationTree…), formatters
```

### 3.1 Platform adapter (provided by the app: DSK or WEB)

```ts
export interface OperationPlatform {
  kind: "desktop" | "web";
  tokenStore: PlatformTokenStore;
  serverProfile(): { baseUrl: string; siteName?: string };
  openInNewWindow?(route: string, options?: { monitor?: number }): void;   // desktop only
  bringToFront?(): void;                                                    // desktop only
  showOsNotification?(title: string, body: string, route?: string): void;
  playSound(soundId: string, loop: boolean): SoundHandle;                  // default Web Audio implementation
  storage: { get(key: string): string | null; set(key: string, v: string): void }; // per-user UI preferences
}
```

## 4. Sign-in and shell (part 1, T1.23)

### 4.1 Sign-in page (`/sign-in`)

| Element | Behaviour |
|---|---|
| Site header | Site name from `/system/info`; desktop shows the selected connection profile (DSK) |
| Language selector | en / fr / ar, switches immediately (RTL for ar), remembered per workstation |
| Login, password | Required; password field with show/hide; Enter submits |
| Errors | `translateError` (e.g. "Login or password incorrect.", "Account locked until 10:42.") |
| Steps | `next: changePassword` → change password form with policy hints from field errors; `next: mfa` (UI in M3) |
| Maintenance | Error `maintenance.active` → full-page message with initiator, reason, expected end (MNT, M3) |
| After success | Navigate to the last route or `/explore` |

### 4.2 Shell

Layout per [desktop client §3.1](../../docs/clients/desktop-client.md#31-main-window): top bar
(site name, global search, alarm counters badge (M2), live status dot, user menu), navigator
(start side), main area with tabs, context panel (end side), bottom panel (alarm console from M2).

| Feature | Behaviour |
|---|---|
| Tabs | Each route opens in the active tab; middle-click / Ctrl+click opens a new tab; back/forward per tab (history 50) |
| Global search (Ctrl+K) | Searches devices (REG text), locations; results grouped; Enter opens |
| User menu | Language, theme (dark/light/high contrast), time display (user vs site time zone), about (versions), sign out |
| Connection banner | From `LiveClient` status and REST failures: red banner "Connection to the server lost — reconnecting… Last update 10:41:05"; all live values show a stale marker while disconnected |
| Session events | `Closing(sessionRevoked)` → sign-in page with message; idle lock (SessionPolicy.IdleLock) → lock overlay requiring password (re-sign-in reusing the session if still valid) |
| Preferences | Panel sizes, last route, favourites stored through `platform.storage` per user id |

## 5. Device explorer (part 1)

### 5.1 Navigator

- Tabs **Locations** and **Servers** (+ **Favourites** list).
- Location tree: from `GET /locations` (whole tree), lazily rendered (`VirtualTree`); each node
  shows the aggregated state of its subtree: worst state dot + counters of alarm/fault/warning
  (from `useAggregates` for visible nodes only).
- Server tree: servers by connection, then device hierarchy (roots first, children lazy via
  `GET /devices?parent=`).
- Selecting a node shows the device list of that node in the main area (`/explore/locations/:id`,
  `/explore/servers/:id`).

### 5.2 Device list

| Column | Source |
|---|---|
| State | `StateBadge` from live state (`useDeviceStates` for visible rows only) |
| Name | `displayName` |
| Type | type name translation + icon |
| Location | location path (last 2 levels, full on hover) |
| Server | server display name |
| Primary value | `display.primaryProperty` formatted |
| Last change | time of the latest property change (relative, absolute on hover) |

Filters: text, category, type, state (multi), tags, maintenance/disabled toggles; server-side
paging (`GET /devices` with cursor); sort by name/type/location. Double-click → device page.
Multi-select prepared for bulk commands (M2).

## 6. Device page (part 1)

Route `/devices/:id`. Header: icon, display name, overall state (with cause property), location
breadcrumb (clickable), server, flags (maintenance, disabled, orphaned, server unavailable).

| Tab | Content |
|---|---|
| Overview | Properties grouped by capability (capability name as section), each `PropertyValue` with quality and times; Health section (connectivity, faults with translated labels, last contact); Last events (from state `lastEvents`) |
| Children | Child devices list (same list component) |
| Links | Linked devices by link kind |
| Attributes | Read-only attributes and custom fields (formatted) |
| Commands (M2) | Command buttons (§10) |
| Events (M2) | Event journal filtered on this device |
| Alarms (M2) | Active and recent alarms of this device |

The context panel shows `DeviceSummaryCard` of the selection anywhere in the app (explorer,
alarms, synoptics later).

## 7. Alarm console (part 2, T2.14)

Bottom panel (dockable, can be undocked to its own window on desktop).

| Area | Behaviour |
|---|---|
| Counters bar | Active, unacknowledged, mine, by priority band — click filters |
| List | Virtualised; default sort: unacknowledged first, then priority (1 = highest), then raised time desc; row = priority band colour stripe + icon, time (site time zone with marker if different from user's), source display name, location path, title (translated rule name / event title), state, owner, counter (×n), small action buttons |
| Live data | Live topic `alarms` (ALM) with filter = console filters; snapshot + deltas |
| Filters | Priority range, state, location subtree, type/category, mine/unassigned, site (federation later); saved filter presets per user |
| Actions | Acknowledge (row button, selection, "acknowledge next most urgent" — keyboard `A`), open details (`Enter`), show on plan (M6), show video (M6), comment, transfer, shelve (duration ≤ permission max, reason required), close (report if required) |
| Concurrency | Server results like "already acknowledged by Sara" shown as toast; list reflects the server state |
| Performance | 10,000 active alarms render smoothly; deltas batched per frame |

## 8. Alarm details and procedures (part 2)

Opened in the context panel or as a dialog/window (pop-up rules §9).

| Section | Content |
|---|---|
| Summary | Priority band, title, source with live state, location breadcrumb, times (raised, acknowledged, resolved), owner, counter with first/last occurrence, rule name |
| Procedure | `ProcedureRunner`: steps in order — instruction (text), checklist (checkboxes), question (choices; branching), input (text/number/choice/photo upload), call (contact with phone/intercom action), action button (command/automation with rights), wait (timer). Answers saved immediately (API) and shown in the timeline; mandatory steps block closing; progress indicator |
| Timeline | Transitions, comments, procedure answers, escalations, actions results, notifications sent — chronological |
| Comment box | Text (≤ 2,000 chars), attach photo (M5 files) |
| Linked devices | Linked cameras (M6 video tiles), linked devices states |
| Actions | Acknowledge, take ownership, transfer (user or group picker), shelve, close (report text required if the rule says so), link to incident (later) |

## 9. Sounds and pop-ups (part 2)

| Feature | Rule |
|---|---|
| Sound per priority band | Default sound files bundled (`critical.ogg` looping, `high.ogg`, `medium.ogg`, `low.ogg`; none for info); site configurable later |
| Critical | Loops until every critical alarm visible to the user is acknowledged; the loop restarts if a new critical arrives |
| Mute | "Mute 5 min" button for users with permission `alarms.mute`; always shows a muted indicator |
| Pop-up | When a new alarm with priority band ≥ user setting (default High) arrives: desktop → `platform.bringToFront()` + details opened; background tab/web → OS/browser notification (`platform.showOsNotification`) |
| Browser autoplay | Web: sounds enabled after the first user interaction; banner "Click to enable alarm sounds" until then |
| Multiple windows | Only one window per client instance plays sounds (leader election through `BroadcastChannel` on web, IPC on desktop) |

## 10. Commands in the UI (part 2, T2.15)

### 10.1 Command buttons

- Generated from the device type's commands (MOD definitions) the user may execute
  (permission `commands.<capability>.<command>` and scope — server-confirmed list from
  `GET /devices/{id}/commands` (CMD) returning available commands with effective risk and
  constraints).
- Parameters → `SchemaForm` in a popover (defaults pre-filled).
- Risk handling:

| Effective risk | UI |
|---|---|
| normal | Click → send |
| sensitive | `ConfirmDialog` naming the device and the effect (translation `….confirm`), optional reason |
| critical | Confirm + mandatory reason; step-up authentication if required (password/TOTP/passkey, IDN §10); four-eyes: shows "waiting for approval" with approver hint; cancel possible |

- Inline status after sending (from live topic `commands` filtered on own commands):
  `Sending… → Queued → Sent → Succeeded ✓ → Verified ✓✓` or `Failed ✗ <translated reason>` /
  `Timed out` / `Rejected (<reason>)` / `Not verified ⚠`. Status stays visible 10 s after the end,
  then collapses into the command journal.
- Bulk commands: from a multi-selection in the device list, only commands common to all selected
  devices and batchable; progress panel with per-device results and "retry failed".

### 10.2 Command journal

Panel listing the user's commands (live), with filters; supervisors with `commands.viewAll` see all
commands. Columns: time, device, command, arguments summary, issuer, status, reason, duration.

## 11. Event journal (part 2, T2.15)

Route `/events`, also embedded in the device page.

| Feature | Behaviour |
|---|---|
| Live mode | Live topic `events` with filters; newest on top; pause button (buffer while paused, counter of new events) |
| History mode | Time range picker → `GET /events` (EVT) with cursor paging; same filters |
| Filters | Time, type (event path), severity, location subtree, server, device, tag, person reference (with personal-data permission), operator |
| Columns | Time (source time with received time on hover), severity icon, device, event title (translated with payload arguments), location, details |
| Personal data | Holder names shown only with permission; otherwise "Cardholder #ref" (server already masks; UI never un-masks) |
| Export | CSV/XLSX/PDF of the current query (job, M5 for PDF) |
| Operator notes | "Add note" with optional device and location; appears as an operator event |

## 12. Tests

Component and hook tests with Vitest + Testing Library + MSW + `FakeLiveHub`; end-to-end
coverage in [tests/e2e-scenarios.md](../tests/e2e-scenarios.md).

| Id | Test |
|---|---|
| UIO-U-001 | Sign-in success navigates to last route |
| UIO-U-002 | Sign-in errors translated (invalid credentials, locked with time, disabled) |
| UIO-U-003 | Change-password step shows policy field errors |
| UIO-U-004 | Language switch on sign-in page applies RTL for Arabic |
| UIO-U-005 | Session revoked closing → back to sign-in with message |
| UIO-U-006 | Idle lock overlay appears after IdleLock and unlocks with password |
| UIO-U-007 | Shell renders navigator at start side, context panel at end side (LTR and RTL) |
| UIO-U-008 | Tabs: open, switch, close, back/forward history per tab |
| UIO-U-009 | Ctrl/middle click opens new tab |
| UIO-U-010 | Global search finds devices and locations; keyboard navigation |
| UIO-U-011 | Connection banner on live disconnect; values marked stale |
| UIO-U-012 | Preferences persisted per user via platform storage |
| UIO-U-013 | Location tree renders whole tree lazily |
| UIO-U-014 | Tree node aggregated state from aggregates topic for visible nodes only |
| UIO-U-015 | Server tree: connection → servers → device roots → lazy children |
| UIO-U-016 | Selecting node shows device list for that scope |
| UIO-U-017 | Device list columns and live state only for visible rows |
| UIO-U-018 | Device list filters map to API query parameters |
| UIO-U-019 | Device list infinite scroll with cursors |
| UIO-U-020 | Device list sort |
| UIO-U-021 | Favourites add/remove persisted |
| UIO-U-022 | Device page header shows state, cause, breadcrumb, flags |
| UIO-U-023 | Overview groups properties by capability with translated names |
| UIO-U-024 | Property quality stale/uncertain rendering |
| UIO-U-025 | Health section with translated faults |
| UIO-U-026 | Last events section |
| UIO-U-027 | Children and links tabs |
| UIO-U-028 | Attributes tab formatting (durations, units, localized names) |
| UIO-U-029 | Unknown device id → not found view |
| UIO-U-030 | Pseudo-locale render of explorer and device page has no untranslated text |
| UIO-U-031 | Alarm console snapshot and deltas from alarms topic |
| UIO-U-032 | Default sort: unacknowledged, priority, time |
| UIO-U-033 | Counters bar values and click-to-filter |
| UIO-U-034 | Filters update the subscription |
| UIO-U-035 | Saved filter presets |
| UIO-U-036 | Acknowledge row action calls API and reflects server state |
| UIO-U-037 | Acknowledge selection |
| UIO-U-038 | "Acknowledge next most urgent" keyboard shortcut |
| UIO-U-039 | Already acknowledged by other → toast, row updated |
| UIO-U-040 | Transfer dialog with user/group picker |
| UIO-U-041 | Shelve requires reason and duration ≤ permission max |
| UIO-U-042 | Close requires report when the rule demands |
| UIO-U-043 | Console renders 10,000 alarms virtualised |
| UIO-U-044 | Site time zone marker when different from user time zone |
| UIO-U-045 | Details summary with live source state |
| UIO-U-046 | Procedure: instruction and checklist steps saved |
| UIO-U-047 | Procedure: question branching |
| UIO-U-048 | Procedure: input types including photo upload (mock) |
| UIO-U-049 | Procedure: action button executes command with rights |
| UIO-U-050 | Procedure: wait step timer |
| UIO-U-051 | Mandatory steps block closing |
| UIO-U-052 | Timeline shows transitions, comments, answers chronologically |
| UIO-U-053 | Comment length limit |
| UIO-U-054 | Take ownership |
| UIO-U-055 | Sound per priority band played on new alarm |
| UIO-U-056 | Critical sound loops until acknowledged; restarts on new critical |
| UIO-U-057 | Mute 5 min requires permission; indicator shown |
| UIO-U-058 | Pop-up: desktop bringToFront and details opened for ≥ threshold |
| UIO-U-059 | Pop-up: OS notification when in background |
| UIO-U-060 | Web autoplay banner until first interaction |
| UIO-U-061 | Single sound leader among windows |
| UIO-U-062 | Alarm console RTL layout |
| UIO-U-063 | Alarm title translated with arguments |
| UIO-U-064 | Personal data masked in alarm details without permission |
| UIO-U-065 | Console keyboard navigation (up/down, Enter, A) |
| UIO-U-066 | Undocked console window (desktop adapter) |
| UIO-U-067 | Federation site column hidden when single site |
| UIO-U-068 | Alarm resolved while open → details update live |
| UIO-U-069 | Escalation entries appear in timeline |
| UIO-U-070 | Accessibility checks (axe) on console and details |
| UIO-U-071 | Command buttons only for available commands from API |
| UIO-U-072 | Parameters form with defaults |
| UIO-U-073 | Normal risk sends immediately |
| UIO-U-074 | Sensitive risk shows confirm with device and effect |
| UIO-U-075 | Critical risk requires reason |
| UIO-U-076 | Step-up authentication dialog when required |
| UIO-U-077 | Four-eyes waiting state and cancel |
| UIO-U-078 | Inline status progression to verified |
| UIO-U-079 | Failed status shows translated reason |
| UIO-U-080 | Timed out / rejected / not verified states |
| UIO-U-081 | Status collapses after 10 s into journal |
| UIO-U-082 | Bulk command availability = intersection of batchable commands |
| UIO-U-083 | Bulk progress panel and retry failed |
| UIO-U-084 | Command journal own commands live |
| UIO-U-085 | Command journal all commands with permission |
| UIO-U-086 | Event journal live mode newest first |
| UIO-U-087 | Live pause buffers and counts new events |
| UIO-U-088 | History mode with time range and cursor paging |
| UIO-U-089 | Event filters map to API |
| UIO-U-090 | Event title translation with payload arguments |
| UIO-U-091 | Source time vs received time display |
| UIO-U-092 | Personal data masked without permission |
| UIO-U-093 | Export starts job and offers download |
| UIO-U-094 | Operator note creation |
| UIO-U-095 | Device page events and alarms tabs filtered by device |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.23 | §3–§6 | UIO-U-001…UIO-U-030 |
| T2.14 | §7–§9 | UIO-U-031…UIO-U-070 |
| T2.15 | §10, §11 | UIO-U-071…UIO-U-095 |
