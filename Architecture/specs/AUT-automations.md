# AUT — Automations (no-code)

| | |
|---|---|
| Spec id | AUT |
| Projects | `src/service/Watchpoint.Automations`, UI `src/ui/packages/configuration/src/automations/` (editor) and `src/ui/packages/operation/src/automations/` (runs panel, operator tasks) |
| Milestone | M8 |
| Tasks | T8.01 (§4–§8), T8.02 (§9–§10) |
| Event id range | 2900–2999 |
| Design docs | [Automations](../../docs/modules/automations.md) |

## 1. Purpose and scope

*When something happens, if conditions hold, do these steps* — configured as a **flow
document** (no code), executed by a durable engine whose run state is persisted step by step in
the event log so that a restart or failover **continues** a run instead of repeating side
effects. Includes operator tasks (ask/confirm/choose), concurrency policies, dry-run testing,
templates, a visual editor (React Flow) and a live runs panel.

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, PIP (subscriber Order 400, partition records, `IPartitionDispatcher`, OnTick), CFG (object type `automations.automation`), EXP (conditions), SCH (schedule triggers, calendars, sun times), CMD (commands with issuer Automation), ALM (raise/update/close, comments; alarm transitions as triggers; action kind `Automation`), NOT (notify), VID (snapshots, bookmarks, show camera), SYN/UIO (show synoptic/camera/popup on workstations via live `Notice`), SCR (call script function, virtual devices), IDN (script identities kind Script), RGT (identity rights), AUD | ALM (Automation action), SYN (automation buttons), SCR (`automations.start`), UIO (manual start, operator tasks) |

## 3. Layout

```
Watchpoint.Automations/
├── Contracts/
│   ├── AutomationDocument.cs (trigger, nodes, edges, settings), Blocks/*.cs, AutomationRun.cs, RunStatus.cs
│   ├── IAutomationService.cs (start, cancel, pause, resume, answer task), OperatorTask.cs, AutomationErrors.cs
├── Internal/
│   ├── Compile/FlowCompiler.cs (document → executable plan, validation), BlockCatalogue.cs
│   ├── Triggers/TriggerIndex.cs, AutomationSubscriber.cs (IPartitionSubscriber, Order 400), ScheduleTriggers.cs, WebhookTriggers.cs
│   ├── Engine/RunExecutor.cs, RunStateStore.cs (per partition), StepExecutors/*.cs, Timers.cs, ConcurrencyGate.cs, DryRunSink.cs
│   ├── Tasks/OperatorTaskService.cs
│   ├── Templates/ (flows JSON en/fr/ar)
│   └── Storage/AutomationsDbContext.cs (run summaries for queries)
├── AutomationsModule.cs, AutomationsLog.cs
```

## 4. Document model (T8.01)

```jsonc
{
  "schemaVersion": 1,
  "id": "…", "key": "fire-evacuation-a", "name": { "en": "Fire evacuation – Building A" },
  "enabled": true, "safety": true,                    // safety: keeps running during maintenance "pause non-safety automations" (MNT)
  "identity": "user-…",                               // Script-kind user whose rights apply (required)
  "trigger": { "kind": "alarm", "transition": "raised", "rule": "std.fire.alarm", "scope": { "locations": ["loc-A"] } },
  "concurrency": { "policy": "skipIfRunning", "key": "{{trigger.location}}" },   // parallel | queue | skipIfRunning | replace; optional key per value
  "variables": [ { "name": "openFailures", "type": "int", "initial": 0 } ],
  "nodes": [
    { "id": "n1", "type": "command", "target": { "selector": { "location": "{{trigger.location}}", "category": "accessControl", "type": "ac.door" } },
      "command": "doorMode.setMode", "arguments": { "mode": "emergencyOpen" }, "await": "verified", "timeout": "PT15S",
      "onFailure": "continue", "outputs": { "failed": "openFailures" } },
    { "id": "n2", "type": "showOnScreens", "audience": { "workstationGroups": ["video-wall"] }, "show": { "cameras": { "linkedTo": "{{trigger.source}}" } } },
    { "id": "n3", "type": "notify", "recipients": [ { "group": "security-team" } ], "template": "automation.fire" },
    { "id": "n4", "type": "if", "condition": { "expression": "vars.openFailures > 0" } },
    { "id": "n5", "type": "raiseAlarm", "title": { "en": "Exit door not open" }, "priority": 900, "source": "{{trigger.source}}" }
  ],
  "edges": [ { "from": "n1", "to": "n2" }, { "from": "n2", "to": "n3" }, { "from": "n3", "to": "n4" }, { "from": "n4", "to": "n5", "branch": "then" } ],
  "failurePolicy": "stopAndAlarm",                    // stop | continue | stopAndAlarm (raises system alarm "Automation failed")
  "limits": { "maxDuration": "PT1H", "maxSteps": 500 }
}
```

### 4.1 Block catalogue

| Category | Blocks (type) |
|---|---|
| Triggers | `event` (event path + scope + payload filters), `property` (device/scope + property + condition, edge `becomesTrue`), `alarm` (transition raised/acknowledged/resolved/closed/escalated; rule, priority range, scope), `commandResult` (command, outcome), `schedule` (cron, calendar edge, sunrise/sunset ± offset for a location's coordinates), `manual` (with typed parameters; from synoptic button, alarm procedure, menu), `called` (by script/automation, typed parameters and return), `webhook` (inbound, §6.4), `maintenance` (started/ended) |
| Conditions | `if` (condition group: device state, property comparison, time/calendar, alarm state, variable comparison, CEL expression; AND/OR nesting), `switch` (value → branches) |
| Actions | `command` (single/group selector; await `accepted|completed|verified`; outputs succeeded/failed counts), `raiseAlarm`, `updateAlarm` (priority, comment), `closeAlarm`, `comment`, `notify`, `showOnScreens` (synoptic, cameras, popup message → workstations/user groups), `setVirtual` (virtual device property, SCR), `startAutomation` (fire-and-forget or await), `callScript` (function, typed args, result into variable), `http` (allow-listed endpoint id, method, body template, response into variable), `bookmark`, `snapshot`, `setVariable` |
| Flow | `wait` (duration), `waitUntil` (condition + timeout → branches `met`/`timeout`), `repeat` (count ≤ 100, body sub-flow), `parallel` (branches, join all/any), `try` (body, onFailure branch), `end` |
| Operator | `askOperator` (audience users/groups/workstations; kind confirm/choose/text; text localized; timeout + default; outputs answer, answeredBy) |

Placeholders `{{trigger.*}}`, `{{vars.*}}`, `{{steps.<id>.*}}` in strings and selectors —
template syntax shared with NOT renderer (no code). Expressions use EXP with an environment
containing `trigger`, `vars`, `steps`, `device(...)`, `now`, `inSchedule(...)`.

## 5. Compilation and validation (T8.01)

`FlowCompiler` (on CFG validate and at load): schema; graph checks (single trigger, all nodes
reachable, no cycles except through `repeat`, branches labelled correctly, `parallel` joins
well-formed); references exist (devices, selectors resolve to ≥ 1 device at validation →
warning when empty, rules, templates, groups, workstations groups, scripts and functions with
signature match, virtual devices, HTTP endpoints); expression checks (EXP); placeholders exist in
the trigger's output model; command arguments validate against type schemas; **identity rights**:
the identity must hold the permissions for every command/notify/alarm action on the targeted
scope (RGT `Explain` used to produce plain messages: "The automation identity 'Fire robot' is not
allowed to open doors of Building B"); limits. Plain-language problems with fixes, like CFG.

## 6. Triggers (T8.01)

- `AutomationSubscriber` (PIP partition subscriber, Order 400 — after ALM 300): `TriggerIndex`
  maps record kinds (event paths, properties, alarm transitions, command results) to automations;
  evaluation of trigger filters on the record; matching → **start request**.
- Start requests are posted to the **automation's partition** (`Partitioning.Of(automationId)`)
  via `IPartitionDispatcher` as record `automation.startRequested { automationId, version,
  trigger payload, correlation }` so that runs of one automation are serialised in one partition
  (concurrency policies are deterministic).
- Schedule triggers through SCH (`TriggerSpec` cron / calendar edge / `Sun(location, event,
  offset)` — SCH gains sun times computation with NodaTime + a NOAA algorithm implementation,
  added by T8.01); fired once per occurrence (leader-only in M9).
- Manual/called: `POST /api/v1/automations/{id}/start { parameters }` (permission
  `automations.run` + scope of the automation's location, or the caller identity for scripts).
- **Webhook**: `POST /api/v1/automations/hooks/{hookId}` with HMAC-SHA256 signature header
  (`X-Watchpoint-Signature`, secret per hook in SEC) or shared token; body JSON validated
  against the hook's parameter schema; rate-limited 60/min per hook.
- Recovery: triggers are evaluated on live records only — on replay the subscriber does not
  start runs (PIP recovery flag), avoiding duplicate runs.

## 7. Engine (T8.01)

### 7.1 Run lifecycle and persistence

Records in the automation partition (all JSON payloads, small):

| Record | Meaning |
|---|---|
| `automation.runStarted { runId, automationId, version, trigger, vars }` | after concurrency gate admits |
| `automation.stepStarted { runId, stepId, attempt }` | before a step's side effect |
| `automation.stepCompleted { runId, stepId, outcome, outputs }` | after it |
| `automation.waiting { runId, stepId, until?, condition?, taskId? }` | timers, waitUntil, operator tasks |
| `automation.runFinished { runId, status (succeeded|failed|cancelled|timedOut|skipped), error? }` | |
| `automation.control { runId, action (pause|resume|cancel), by }` | operator control |

- `RunStateStore` (in-memory per partition, snapshot participant via STA-style
  `ISnapshotParticipant`) rebuilds runs from records on recovery.
- **Resume rule**: a step with `stepStarted` and no `stepCompleted` after recovery is resolved by
  kind: `command` → query CMD by `ClientCommandId` = `aut:<runId>:<stepId>:<attempt>` (CMD's issuer
  idempotency key) and continue with its actual outcome; `notify` → NOT dedup key ensures no
  double send, mark completed; `raiseAlarm`/`updateAlarm`/`closeAlarm` → idempotent by key; `http`,
  `callScript` → outcome unknown → treated as failed with `aut.step.outcome_unknown` and the
  failure policy applies (documented: at-most-once for non-idempotent external calls); pure
  steps (conditions, variables) re-executed.
- Timers (`wait`, `waitUntil` timeout, task timeout, maxDuration) in `OnTick`; `waitUntil`
  conditions re-evaluated on relevant records (index by referenced devices) and on tick.

### 7.2 Concurrency policies

Per automation (optionally per `key` value): `parallel` (≤ `maxParallel` 20), `queue` (FIFO, ≤ 50
queued, older dropped with log), `skipIfRunning` (new trigger recorded as run `skipped`),
`replace` (cancel running then start). Enforced in the partition → deterministic.

### 7.3 Step execution

Side-effect steps run through PIP side-effect queues (class `Automation`, timeout per step,
default 30 s, ≤ `limits.maxDuration`) and report results back as `stepCompleted` records via the
dispatcher. Commands: CMD issuer `Automation { automationId, runId }`, identity from the
document, priority from step (default Normal), result awaited per `await`. Group commands use
CMD batches where available.

### 7.4 Operator tasks

`askOperator` creates an `OperatorTask` (id, run, audience, kind, text, options, timeout, default)
→ live `Notice` `automation.task` to the audience (desktop and web, mobile in M10) + tasks list
API; first valid answer (user in audience with `automations.answer`) recorded as
`automation.taskAnswered` → run continues; timeout → default answer. Tasks visible in the runs
panel and in the related alarm details when the trigger is an alarm.

### 7.5 Maintenance and licence

During maintenance with `PauseNonSafetyAutomations`, non-`safety` automations do not start
(runs `skipped` with reason) and running ones pause at the next step boundary until the end.
Scripting-related blocks (`callScript`) require `opt.scripting` (LIC) where the edition lacks it
(validation error).

## 8. Dry run (T8.01)

`POST /api/v1/automations/{id}/test { triggerPayload | sampleRecordId, mode: dryRun | realOnTestDevices, testDevices? }`:

- Runs the **draft or published** version in an isolated executor (not in the partition, no
  records written to WP_LOG); side effects replaced by the `DryRunSink` (commands listed with
  resolved targets and arguments, notifications rendered, alarms described, HTTP requests shown
  without sending, operator tasks answered from the test request or default), waits shortened
  (time acceleration ×60, max 10 min wall time).
- `realOnTestDevices`: commands executed only on devices listed in `testDevices` (others dry);
  requires `automations.testReal`.
- Result: step trace (inputs, outputs, branch taken, durations), problems.

## 9. Editor (T8.02)

- Configuration side route `/config/automations/{id}`: React Flow canvas with palette (§4.1),
  node forms (generated from block schemas + custom pickers: device selector builder, rule
  picker, recipients, templates, CEL editor), edge labels for branches, auto-layout with `dagre` (MIT), minimap, undo/redo, validation panel and
  inline node badges with plain messages, placeholder completion in text fields.
- Test panel: choose a trigger sample (recent matching records list, or form), run dry-run,
  step-by-step trace highlighted on the canvas.
- Templates gallery: "Lockdown building", "Fire alarm: unlock doors and show cameras", "Door
  forced: snapshot + notify guard", "Night arming check", "Parking full" — instantiated with a
  wizard asking for the template's parameters (locations, groups).
- Versions/publish via CFG (draft bar), identity picker (Script users) with rights summary.

## 10. Runs panel (T8.02)

Operation side *Automations* (permission `automations.view`): running and recent runs (filters
automation, status, time), run detail (timeline of steps with outcomes, variables, operator
tasks, link to trigger record/alarm), controls pause/resume/cancel (`automations.control`),
manual start buttons for `manual` automations (`automations.run`), operator tasks inbox.

Permissions added to RGT: `automations.view` (Locations), `automations.run` (Locations),
`automations.control` (Locations), `automations.answer` (None), `automations.testReal` (None),
`config.automations.view/edit` (Locations).

## 11. Data, API, errors, observability

Run summaries are projected to `wp_automations.runs` (id, automation_id, version, status,
started_at, finished_at, trigger jsonb, steps_count, error) and `run_steps` (run_id, step_id,
attempt, started_at, completed_at, outcome, outputs jsonb) by a durable consumer, for queries;
retention 90 days (HIS-like job).

| Method and path | Permission | Description |
|---|---|---|
| `GET/POST/PATCH/DELETE /api/v1/automations…` | `config.automations.view/edit` | CFG-backed documents |
| `POST /api/v1/automations/{id}/start` | `automations.run` | Manual start with parameters |
| `POST /api/v1/automations/{id}/test` | `config.automations.edit` | Dry run |
| `GET /api/v1/automation-runs?automation=&status=&from=&to=` / `{runId}` | `automations.view` | |
| `POST /api/v1/automation-runs/{runId}/pause|resume|cancel` | `automations.control` | |
| `GET /api/v1/automation-tasks?mine=true` / `POST …/{taskId}/answer` | `automations.answer` | Operator tasks |
| `POST /api/v1/automations/hooks/{hookId}` | signature/token | Webhook trigger |
| `GET /api/v1/automation-templates` | `config.automations.edit` | Templates |

Live topic `automationRuns` (filter automation ids/status) and `automationTasks` (own).

Errors: `aut.document.invalid`, `aut.identity.missing_rights` (args permission, scope),
`aut.trigger.invalid`, `aut.reference.missing`, `aut.run.not_found`, `aut.run.not_running`,
`aut.task.not_found`, `aut.task.already_answered`, `aut.task.not_in_audience`,
`aut.step.outcome_unknown`, `aut.limits.exceeded`, `aut.hook.invalid_signature`,
`aut.concurrency.queue_full`.

| EventId | Level | Message |
|---|---|---|
| 2900 | Information | Automation {Key} run {RunId} started by {Trigger} |
| 2901 | Information | Automation {Key} run {RunId} finished {Status} in {ElapsedMs} ms |
| 2910 | Warning | Automation {Key} step {StepId} failed: {Error} |
| 2911 | Warning | Automation {Key} run {RunId} step {StepId} outcome unknown after recovery |
| 2920 | Information | Operator task {TaskId} answered by {User}: {Answer} |

Metrics: `watchpoint.automations.runs` (counter: automation, status),
`watchpoint.automations.running` (gauge), `watchpoint.automations.step.duration` (histogram).

## 12. Tests

| Id | Test |
|---|---|
| AUT-U-001…010 | Schema and graph validation: single trigger, reachability, cycles only via repeat, branches, parallel joins, limits |
| AUT-U-011…018 | Reference validation for each block kind; empty selectors warning; script signatures |
| AUT-U-019…022 | Identity rights validation with plain messages from RGT Explain |
| AUT-U-023…025 | Placeholders and expression environment checks |
| AUT-U-026…033 | Triggers: event, property edge, alarm transitions with filters, command result, schedule (cron, calendar edge, sunrise/sunset with fixture coordinates), manual parameters, called, maintenance |
| AUT-U-034 | Webhook signature/token validation, schema, rate limit |
| AUT-U-035 | No runs started during recovery replay |
| AUT-U-036 | Start requests routed to the automation partition |
| AUT-U-037…040 | Concurrency policies parallel/queue/skip/replace with keys |
| AUT-U-041…045 | Records sequence per run; state rebuild from records; snapshot participation |
| AUT-U-046…050 | Resume rules per step kind after crash between stepStarted and stepCompleted (CMD idempotency, NOT dedup, alarms idempotent, http/script unknown → failure policy) |
| AUT-U-051…054 | Flow: if/switch, wait, waitUntil met/timeout, repeat bound, parallel join all/any, try/onFailure |
| AUT-U-055 | Failure policies stop/continue/stopAndAlarm |
| AUT-U-056 | Operator tasks: audience, first answer wins, timeout default, permissions |
| AUT-U-057 | Maintenance pause of non-safety automations |
| AUT-U-058 | Dry run: side effects captured, time acceleration, trace |
| AUT-U-059 | Real-on-test-devices mode restricted to listed devices and permission |
| AUT-U-060 | Limits maxDuration/maxSteps enforced |
| AUT-U-061…070 | Editor: palette, node forms per block, pickers, edges and branch labels, dagre layout, undo/redo, validation badges, placeholder completion |
| AUT-U-071…075 | Editor test panel: samples, dry-run, trace highlighting |
| AUT-U-076…078 | Templates gallery and wizard instantiation |
| AUT-U-079…085 | Runs panel: list filters, run detail timeline, controls permissions, manual start form, tasks inbox, live updates, RTL |
| AUT-I-001 | Fire evacuation template on simulator campus: doors opened and verified, cameras shown on wall workstation (Notice), notification sent |
| AUT-I-002 | Kill the node during a run waiting 30 s → after restart the run resumes at the wait, no duplicate commands |
| AUT-I-003 | Crash between command stepStarted and stepCompleted → resumes with CMD outcome, no duplicate |
| AUT-I-004 | Operator task answered from web client; timeout default path |
| AUT-I-005 | Webhook trigger with HMAC from a test client |
| AUT-I-006 | Concurrency skipIfRunning under a burst of 100 triggers |
| AUT-I-007 | Calendar edge schedule (FakeTimeProvider) end-of-day arming check |
| AUT-I-008 | Load: 50 automations triggered 1,000 times/min — partition latency within PIP budget |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T8.01 | §4–§8, §11 | AUT-U-001…AUT-U-060, AUT-I-001…AUT-I-008 |
| T8.02 | §9–§10 | AUT-U-061…AUT-U-085 |
