# SCR — Scripting

| | |
|---|---|
| Spec id | SCR |
| Projects | `src/service/Watchpoint.Scripting` (node side: scripts as configuration, gateway, triggers), `src/service/Watchpoint.ScriptHost` (process: V8 isolates), `src/integrations/Watchpoint.Integration.Virtual` (built-in package for virtual devices), `schemas/proto/script_host.proto`, `types/script-api/` (TypeScript declarations `watchpoint.d.ts`), UI `src/ui/packages/configuration/src/scripts/` (IDE) |
| Milestone | M8 |
| Tasks | T8.03 (§4–§6), T8.04 (§7–§9), T8.05 (§10–§11), T8.06 (§12) |
| Event id range | 3000–3099 |
| Design docs | [Scripting](../../docs/modules/scripting.md) |

## 1. Purpose and scope

TypeScript scripts for what automations cannot express, executed in **V8 isolates** in a
separate **script host** process with strict limits, interacting with Watchpoint only through a
typed **script API** executed by a node-side **gateway** under the script's own identity and
rights. A Monaco-based **IDE** with type-checking against generated site typings, test mode with
simulated events and dry-run commands, and an inspector bridge for debugging.

## 2. Dependencies

| Uses | Used by |
|---|---|
| Node side: K, CFG (object types `scripts.script`, `scripts.library`), IHO (`ProcessSupervisor`), PIP (subscriber Order 450 for triggers, side-effect queue class `Script`), CMD, ALM, EVT (custom events), NOT, REG/STA/HIS (reads), AUT (call/start), IDN (Script identities), RGT, SEC (webhook secrets, script storage encryption), LIC (`opt.scripting`), AUD | AUT (`callScript`), ALM (Script action), UIF (IDE) |
| Script host: Microsoft.ClearScript.V8 (MIT; bundles V8 BSD-3), Grpc.Net.Client, `typescript` npm package (Apache-2.0) loaded into a compiler isolate | |
| IDE: Monaco (MIT) with its bundled TypeScript worker, `@watchpoint/api-client` | |

## 3. Layout

Log event ids: 3000–3099 ([README §8](../README.md#8-observability-conventions)).

```
schemas/proto/script_host.proto              package watchpoint.scripthost.v1
types/script-api/watchpoint.d.ts             public API declarations (versioned with the API, docs comments en/fr/ar via @i18n tags)
Watchpoint.Scripting/
├── Contracts/ScriptDocument.cs (files, entry, identity, limits, triggers metadata), ScriptRun.cs, IScriptService.cs, ScriptErrors.cs
├── Internal/
│   ├── Host/ScriptHostManager.cs (process via IHO ProcessSupervisor), HostLink.cs (gRPC server side), Deployment.cs (load/swap/unload)
│   ├── Compile/ServerCompiler.cs (asks the host's compiler isolate), SiteTypingsGenerator.cs
│   ├── Gateway/ScriptApiGateway.cs (dispatch API calls, rights), Handlers/*.cs (devices, commands, alarms, events, notify, http, storage, automations, time, log)
│   ├── Triggers/ScriptTriggerIndex.cs, ScriptSubscriber.cs (Order 450), WebhookTriggers.cs, Schedules.cs
│   ├── Storage/ScriptKeyValueStore.cs (wp_scripting.kv), RunHistory.cs
│   └── Testing/TestSessionService.cs, InspectorBridge.cs
├── ScriptingModule.cs, ScriptingLog.cs
Watchpoint.ScriptHost/
├── Program.cs (--pipe, --token), HostLinkClient.cs
├── Isolates/IsolatePool.cs, ScriptIsolate.cs (one per script), Limits.cs (heap, CPU time watchdog), ApiBindings.cs (JS ↔ gRPC calls), Timers.cs
├── Compiler/CompilerIsolate.cs (typescript.js), ModuleLoader.cs (site libraries, bundled utilities)
└── Inspector/InspectorServer.cs (V8 inspector protocol over the link)
Watchpoint.Integration.Virtual/              (package watchpoint.integration.virtual, builtin, licence class included)
```

## 4. Script host process (T8.03)

- One script host process per node (M8), started by `ScriptHostManager` through IHO's
  `ProcessSupervisor` (same readiness, liveness, restart and crash-loop rules, IHO §6.2);
  resource limits as IHO §6.3 (default memory 2 GB for the whole host).
- Protocol `script_host.proto`: host is the gRPC client on a local pipe/socket (as IHO §5.1),
  bidirectional `Session` stream with messages: `Deploy(scriptId, version, compiled JS modules,
  source maps, limits)`, `Undeploy`, `Invoke(scriptId, handlerId, payload, invocationId,
  deadline)`, `InvocationResult`, `ApiCall(invocationId, method, args JSON)` /
  `ApiResult`, `Log`, `Metrics`, `TimerFired` (timers live in the host), `Compile(files)` /
  `CompileResult`, `Inspector` frames, `Status`.
- Payloads JSON (UTF-8) with a 1 MB cap per message (larger → error `scr.payload.too_large`).

## 5. Isolates and limits (T8.03)

- One **V8 isolate per published script** (ClearScript `V8ScriptEngine` with
  `V8ScriptEngineFlags.EnableTaskPromiseConversion`, no host objects except the API bindings,
  `AllowReflection = false`, no `require`, no `eval`/`Function` constructor — disabled via V8
  flag `--disallow-code-generation-from-strings`).
- Modules: ES modules compiled from TypeScript; imports only of the script's own files, site
  libraries (`@site/<lib>`), and bundled utilities (`@wp/date` (date-fns subset, MIT), `@wp/csv`,
  `@wp/crypto` (SHA-256/HMAC via host binding)).
- Limits (defaults, per script override within site maxima):

| Limit | Default | Enforcement |
|---|---|---|
| Heap | 64 MB | `MaxRuntimeHeapSize`; breach → isolate terminated, script restarted (counts as failure) |
| CPU per handler invocation | 2 s | Watchdog `Interrupt()` after deadline (CPU time measured by isolate) → invocation fails `scr.limit.cpu` |
| Total CPU share | 25 % of one core per script (sliding 1 min) | Invocations delayed when exceeded (queue) |
| Concurrent invocations | 8 per script (async handlers interleave on one thread per isolate) | Further queued (≤ 1,000, then dropped and counted) |
| Timers | 100 active | `scr.limit.timers` |
| Pending API calls | 50 | `scr.limit.pending` |
| Outbound HTTP | 60 calls/min | gateway |
| Storage | 10 MB per script, values ≤ 256 KB | gateway |

- **Failure policy**: an exception or limit breach aborts only that invocation (logged with
  source-mapped stack). ≥ 10 failures in 5 min → script **suspended** (system event
  `scripts.suspended`, alarm-able), resumed manually or after `scr.suspendCooldown` (30 min) if
  configured.
- Host crash: node restarts the host, redeploys published scripts; events that arrived meanwhile
  are delivered from the subscriber's buffer (≤ 10,000 per script, older dropped) unless the
  script declares `skipStaleAfter` (e.g. 60 s) — then older ones are skipped.

## 6. Deployment (T8.03)

- Scripts are CFG object types `scripts.script` (files map, entry file, identity, limits,
  enabled, `skipStaleAfter`) and `scripts.library` (files, exported API). Publish validation =
  **server compile** (§11) with zero errors; dependencies on libraries resolved by version
  (scripts pin `@site/lib@<publishedVersion>` implicitly at publish).
- Apply: `Deploy` new version into a fresh isolate, run its top-level module code (registers
  handlers via the API, max 5 s), then atomically switch triggers to the new isolate and
  `Undeploy` the old one after its running invocations finish (≤ 30 s, then terminated). Storage
  is kept across versions.
- `opt.scripting` licence feature required (LIC gate); without it publish is refused.

## 7. Script API (T8.04)

Declared in `types/script-api/watchpoint.d.ts` (module `watchpoint`). Excerpt:

```ts
import { on, devices, commands, alarms, events, notify, http, storage, automations, time, log, virtual } from "watchpoint";

on.event({ path: "doorPosition.forced", scope: { locations: ["Building A"] } }, async (e) => {
  const door = await devices.get(e.source);
  const cams = await door.linked("camera-for");
  for (const cam of cams) await commands.send(cam, "ptz.gotoPreset", { preset: "door" });
  await alarms.raise({ title: `Forced: ${door.name}`, priority: 800, source: door.id });
});

on.property({ device: "Room 101 temperature", property: "measurement.value" }, (c) => { /* c.value, c.previous */ });
on.alarm({ transition: "acknowledged", rule: "std.ac.door.forced" }, handler);
on.commandResult({ command: "openable.open" }, handler);
on.schedule({ cron: "0 20 * * 1-5" } | { calendar: "night", edge: "start" }, handler);
on.webhook("occupancy-in", { schema: {/* JSON schema */} }, async (req) => ({ status: 200 }));
on.message("channel-name", handler);                           // from other scripts
export async function countPeople(zone: string): Promise<number> { … }   // callable from automations (typed)
```

| Area | Members (all async; rights of the script identity apply) |
|---|---|
| `devices` | `get(idOrName)`, `find(query)` (location/type/category/tag/text; ≤ 1,000 results), `device.state`, `device.prop(path)`, `device.history(path, range, bucket)`, `device.linked(kind)` |
| `commands` | `send(device, command, args, { await: "accepted"|"completed"|"verified", reason?, priority? }) → CommandResult` (CMD issuer `Script`), `sendGroup(selector, …)` |
| `alarms` | `raise({...}) → AlarmRef` (custom alarm of rule `script.<scriptKey>`), `comment`, `acknowledge` (if granted), `close`, `query` |
| `events` | `emit(path, payload)` — custom event types declared in the script document (`eventTypes` with payload schema, category, severity) |
| `virtual` | `define({ id, type, name, location })`, `set(id, property, value)`, `health(id, connectivity)` — §8 |
| `notify` | `send({ recipients, template | subject/body, channels })` (NOT) |
| `http` | `fetch(url, init)` subset (method, headers, body string/JSON, timeout ≤ 30 s, response ≤ 5 MB) — URL host must match the site allow-list entry assigned to the script (`httpEndpoints`) |
| `storage` | `get/set/delete/list(prefix)` per script, JSON values, replicated (M9) |
| `automations` | `start(id, params)`, `run(id, params) → result` (await `called` automations) |
| `time` | `now()`, `inSchedule(calendar)`, `zoned(tz)`, timers `setTimeout/setInterval/clearX` (host-side) |
| `log` | `debug/info/warn/error` → script console (live), logs (EventId 3050) |
| `messages` | `publish(channel, payload)` to other scripts' `on.message` |

The gateway (`ScriptApiGateway`) validates arguments (JSON schemas generated from the d.ts via a
build step `tools/script-api-schemas`), checks rights with RGT for the identity and the target
(device scope, permission per call: e.g. `commands.<cap>.<cmd>`, `alarms.raise.script` added to
catalogue), executes through module services, maps errors to `WatchpointError { code, message
(localized), details }` thrown in the script.

## 8. Triggers, virtual devices, webhooks, storage (T8.04)

- Triggers registered by the top-level code are reported to the node (`RegisterTriggers`),
  indexed by `ScriptTriggerIndex`; `ScriptSubscriber` (PIP partition subscriber, Order 450, live
  records only — no invocations during recovery replay) matches records and enqueues
  invocations (side-effect class `Script`, per script ordering preserved).
- **Virtual devices**: package `watchpoint.integration.virtual` provides one hidden connection
  per script (created/removed with the script), server `virtual.server` named after the script;
  `virtual.define` adds devices of **any standard type** to the connection's inventory
  (`ReportInventoryChanged`), auto-imported in the location given; `virtual.set` reports
  properties through the connection's sink — so virtual devices behave exactly like real ones
  (states, alarms, history, synoptics, licence: virtual devices are **free** class).
- **Webhooks**: `POST /api/v1/scripts/hooks/{scriptKey}/{name}` authenticated by HMAC/shared token
  (secret per hook), payload validated against the declared schema, handler result → HTTP
  response (status, JSON body ≤ 64 KB); timeout 10 s; rate limit per hook.
- **Storage**: `wp_scripting.kv (script_id, key, value jsonb, updated_at)` with size accounting,
  replicated with the database (M9). Secrets must not be stored there (documented; secrets are
  provided to HTTP calls through the endpoint allow-list, §9).

## 9. HTTP allow-list and security (T8.04)

- Site setting `Scripts:HttpEndpoints`: named entries `{ name, baseUrl (https), allowedMethods,
  headers (may reference SEC secrets as `{{secret:name}}` injected by the gateway, never visible
  to the script), tls options }`; a script declares which endpoint names it uses (validated at
  publish). Requests are executed **by the gateway** in the node (not by V8), so the script never
  sees raw sockets or secrets.
- Redirects followed only within the same base URL; response headers filtered (no `Set-Cookie`).
- Every API call is attributable (run id, script, identity); commands/alarms audited as usual
  with issuer Script.

## 10. IDE (T8.05)

- Configuration side `/config/scripts/{id}`: file tree (multi-file), Monaco editors with
  TypeScript worker configured with `watchpoint.d.ts` + **site typings** + library declarations;
  problems panel (all files) with plain explanations for common TS errors (mapping table of TS
  error codes → localized hints), quick fixes from the language service + Watchpoint fixes
  (unknown device name → nearest names via Levenshtein from typings).
- **Site typings** (`GET /api/v1/scripts/typings`, ETag, regenerated on registry/model changes,
  per user rights scope): union types of device names (`DeviceName`), locations, tags, event
  paths, per-type property maps with enum literal types — e.g.
  `devices.get("Main entrance")` returns `DoorDevice` whose `prop("doorPosition.position")` is
  `"open" | "closed" | "forced" | …`. Large sites: names union capped at 20,000 entries
  (beyond: `string` with a completion provider calling `GET /api/v1/devices?text=`).
- Hover docs in the user's language (d.ts JSDoc with `@i18n` blocks), signature help, go to
  definition, find references, rename (within the script).
- Lint rules (custom TS-service-based checks): unawaited `commands.send`, `while(true)` without
  await in handlers, handlers registered inside handlers, storage writes in tight loops.
- Formatting: Monaco's TypeScript formatter (Prettier-like defaults).
- Snippets/templates: "React to a forced door", "Daily report", "Call a REST API",
  "People counter in a zone", "Virtual device from MQTT data".
- Versions: diff with the published version (Monaco diff editor), history list, rollback (CFG).

## 11. Server compile (T8.05)

`ServerCompiler` sends files + library declarations + `watchpoint.d.ts` + current site typings
to the host's **compiler isolate** (`typescript` package version pinned and identical to the
IDE's Monaco TS version — CI check), options `strict`, `target ES2022`, `module ESNext`,
`noEmitOnError`; returns diagnostics and emitted JS + source maps. Publish refused on any error
(`scr.compile.failed` with diagnostics). Compile time limit 20 s, memory 512 MB isolate.

## 12. Test mode and debugging (T8.06)

- **Test session** (`POST /api/v1/scripts/{id}/test-sessions` with the draft files): compiled and
  deployed into a separate **test isolate** with the same limits; API gateway in **dry-run
  mode**: reads are real (within rights), side effects are recorded not executed (commands "would
  send Open to Main entrance", alarms/events/notifications/http described; storage uses a
  throwaway copy of the script's storage; virtual devices simulated).
- Trigger simulation: pick a record from history (events, alarms, property changes — search UI)
  or build one with a form generated from the trigger's payload type; fire; results stream to the
  IDE console (logs, side effects, errors with source-mapped stacks, duration).
- Option "real commands on selected test devices" (permission `scripts.testReal`).
- **Debugger**: V8 inspector protocol (ClearScript `EnableDebugging` on the test isolate only)
  bridged over the host link and a WebSocket `/api/v1/scripts/test-sessions/{id}/inspector`
  (desktop configuration endpoint) to the IDE's minimal debugger UI (breakpoints in Monaco
  gutter, step over/into/out, continue, call stack, scopes/watch). Paused test isolates time out
  after 10 min. Never available on production isolates.
- Execution history (production): last 1,000 invocations per script (trigger, duration,
  outcome, logs excerpt, side effects summary) in `wp_scripting.runs`, metrics per script.

## 13. Data, API, errors, observability

Tables `wp_scripting`: `scripts`/`libraries` (via CFG), `kv`, `runs` (id, script_id, version,
handler, trigger summary, started_at, duration_ms, outcome, error, logs excerpt), `hooks`
(script_id, name, secret_ref), `test_sessions`.

| Method and path | Permission | Description |
|---|---|---|
| `GET/POST/PATCH/DELETE /api/v1/scripts…`, `/api/v1/script-libraries…` | `config.scripts.view/edit` | CFG-backed |
| `POST /api/v1/scripts/compile` | `config.scripts.edit` | Server compile of given files |
| `GET /api/v1/scripts/typings` | `config.scripts.edit` | Site typings (d.ts) |
| `GET /api/v1/scripts/{id}/runs` / `metrics` | `config.scripts.view` | History |
| `POST /api/v1/scripts/{id}/suspend|resume` | `config.scripts.edit` | |
| `POST /api/v1/scripts/{id}/test-sessions`, `…/{sid}/fire`, `DELETE …/{sid}`, WS `…/{sid}/inspector`, WS `…/{sid}/console` | `config.scripts.edit` | Test mode |
| `GET /api/v1/scripts/{id}/console` (WS) | `config.scripts.view` | Live production console |
| `POST /api/v1/scripts/hooks/{scriptKey}/{name}` | hook signature | Webhooks |

Errors: `scr.compile.failed`, `scr.limit.cpu`, `scr.limit.heap`, `scr.limit.timers`,
`scr.limit.pending`, `scr.limit.http`, `scr.limit.storage`, `scr.payload.too_large`,
`scr.script.suspended`, `scr.http.endpoint_not_allowed`, `scr.api.invalid_arguments`,
`scr.api.forbidden` (permission, target), `scr.host.unavailable`, `scr.licence.missing`.

| EventId | Level | Message |
|---|---|---|
| 3000 | Information | Script host ready (V8 {V8Version}), {Scripts} scripts deployed |
| 3001 | Error | Script host exited ({ExitCode}); restarting |
| 3010 | Information | Script {Key} v{Version} deployed in {ElapsedMs} ms |
| 3020 | Warning | Script {Key} invocation {Handler} failed: {Error} |
| 3021 | Warning | Script {Key} limit breached: {Limit} |
| 3022 | Error | Script {Key} suspended after {Failures} failures |
| 3050 | Information | [script {Key}] {Message} (script log) |

Metrics: `watchpoint.scripts.invocations` (counter: script, outcome),
`watchpoint.scripts.duration` (histogram), `watchpoint.scripts.queue.depth` (gauge),
`watchpoint.scripts.heap.bytes` (gauge per script).

## 14. Tests

| Id | Test |
|---|---|
| SCR-U-001…005 | Host process supervision via ProcessSupervisor (fake), protocol handshake, message size cap |
| SCR-U-006…010 | Isolate creation flags: no eval/Function, no reflection, no host objects other than bindings; module loader restrictions |
| SCR-U-011…016 | Limits: heap breach terminates isolate; CPU watchdog interrupt; CPU share queuing; concurrent invocations; timers; pending calls |
| SCR-U-017…020 | Failure policy: invocation failure isolation, suspension after 10 in 5 min, cooldown resume |
| SCR-U-021…024 | Host crash recovery: redeploy, buffered events delivery, skipStaleAfter |
| SCR-U-025…030 | Deployment: top-level registration time limit, atomic switch, old isolate drained/terminated, storage kept, licence gate |
| SCR-U-031…040 | API gateway: argument schema validation per method, rights per call and target, error mapping to WatchpointError with localized messages |
| SCR-U-041…048 | devices/commands/alarms/events/notify/automations handlers against module fakes |
| SCR-U-049…053 | Triggers: registration, index matching, live-only (no replay), ordering per script, side-effect class |
| SCR-U-054…058 | Virtual devices: hidden connection lifecycle, define → inventory/auto-import, set → sink, health, free licence class |
| SCR-U-059…062 | Webhooks: signature/token, schema validation, response mapping, timeout, rate limit |
| SCR-U-063…066 | Storage: get/set/delete/list, size accounting and limits, version swap keeps data |
| SCR-U-067…070 | HTTP allow-list: endpoint names, secret header injection invisible to scripts, redirect rules, response limits |
| SCR-U-071…076 | Site typings generation: unions, per-type property maps with enum literals, rights scope, caps, ETag regeneration on changes |
| SCR-U-077…080 | IDE: TS worker configuration with typings, problems panel hints mapping, Watchpoint quick fixes (nearest names) |
| SCR-U-081…084 | IDE: lint rules (unawaited command, busy loop, nested registration, storage loop) |
| SCR-U-085…087 | IDE: multi-file tree, libraries imports, snippets/templates |
| SCR-U-088…090 | Server compile: same TS version as IDE (CI check), strict options, errors block publish, time/memory limits |
| SCR-U-091…095 | Test sessions: separate isolate, dry-run gateway (side effects recorded), throwaway storage copy, simulated virtual devices |
| SCR-U-096…098 | Trigger simulation from history records and forms; console streaming with source-mapped stacks |
| SCR-U-099…100 | Real commands on test devices permission |
| SCR-U-101…104 | Debugger: breakpoints, stepping, scopes/watch via inspector bridge; paused timeout; not available for production isolates |
| SCR-U-105 | Execution history and metrics |
| SCR-I-001 | Real script host with ClearScript V8 on Windows and Linux CI: deploy, invoke, logs |
| SCR-I-002 | Infinite loop script interrupted at 2 s; others unaffected |
| SCR-I-003 | Memory bomb script terminated at heap limit; host stays up |
| SCR-I-004 | Kill script host process → restart and redeploy; buffered events delivered |
| SCR-I-005 | Forced door script on simulator: PTZ preset commands and custom alarm end-to-end with rights |
| SCR-I-006 | Load: 100 scripts × 50 events/s — p99 invocation latency < 50 ms |

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| T8.03 | §4–§6 | SCR-U-001…SCR-U-030, SCR-I-001…SCR-I-006 |
| T8.04 | §7–§9 | SCR-U-031…SCR-U-070 |
| T8.05 | §10–§11 | SCR-U-071…SCR-U-090 |
| T8.06 | §12–§13 | SCR-U-091…SCR-U-105 |
