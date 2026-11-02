# Scripting

> Requirement: scripting like in classic PSIMs, but better — a modern IDE inside the client
> where users write scripts, see errors as they type, get suggestions, just like a modern
> code editor.

Scripting is for what [automations](automations.md) (no-code) cannot express: custom
logic, calculations, integrations with external web services, complex correlations.

## 1. Language and runtime

| Choice | Detail |
|---|---|
| Language | **TypeScript** (compiled to JavaScript) |
| Engine | **V8** isolates via Microsoft ClearScript, in a separate `watchpoint-script-host` process |
| Isolation | Each script runs in its own isolate: own memory heap, no shared globals, no file system, no process, no raw network |
| Limits per script | Memory (default 64 MB), CPU time per handler call (default 2 s), total CPU share, max timers, max pending commands, max outbound HTTP calls per minute |
| Failure behaviour | An exception, limit breach or infinite loop aborts only that handler call; the script is suspended after repeated failures with a system event (and alarm if configured). The node and other scripts are unaffected |
| Host crash | The script host process is restarted by the node; scripts resume; events that arrived meanwhile are delivered (with an option to skip stale ones) |

## 2. The Watchpoint script API

Scripts interact with Watchpoint only through a typed API (published as TypeScript
declaration files). Main parts:

| Area | What scripts can do |
|---|---|
| **Triggers** | React to events (by type, scope, payload), property changes, alarm transitions, command results, schedules (cron-like and calendars), HTTP webhooks (inbound, authenticated), manual runs, messages from other scripts |
| **Devices** | Find devices (by id, name, location, type, tag), read properties and overall state, read history |
| **Commands** | Send commands and **await their result** (`succeeded`, `failed` with reason, `verified`) |
| **Alarms** | Raise custom alarms, comment, acknowledge (if granted), read alarms |
| **Events** | Emit custom events (typed by a schema the script declares) |
| **Virtual devices** | Create and update **virtual devices** owned by the script (e.g. a computed "Building occupancy" counter) that appear like any device in lists, synoptics and rules |
| **Notifications** | Send through configured channels (e-mail, SMS, push, webhook) using templates |
| **HTTP** | `fetch` to **allow-listed** hosts only, with timeouts |
| **Storage** | Persistent key-value store per script (replicated in the cluster) |
| **Time** | Timers, delays, site time zone helpers, calendars |
| **Logging** | `log.info/warn/error` to the script console and logs |
| **Automations** | Start automations, be called from automations as an action (with typed parameters and return value) |

Everything a script does runs **as its script identity** with its own rights (see
[Users and rights](users-and-rights.md)): a script allowed only to read Building A cannot
open a door in Building B. Rights for script identities are granted with the same editor
and rights-copy tool as for people.

## 3. The IDE

Located in the desktop client, configuration side (*Configuration → Scripts*). Built on
**Monaco** (the editor of VS Code).

| Feature | Description |
|---|---|
| Errors while typing | Red/yellow underlines from the TypeScript language service running in the client (no server round trip); problems panel listing all errors with plain explanations |
| Completion | Of the API, and of **the site's own objects**: device names, locations, tags, event types, property names and enum values — generated from the live configuration as TypeScript types, so `door.doorPosition.position === "` proposes `open`, `closed`, `forced`… |
| Hover documentation | Types, descriptions and examples for every API member, in the user's language |
| Signature help, go to definition, find references, rename | Standard TypeScript tooling |
| Quick fixes | e.g. "Device 'Back door' not found — did you mean 'Back door B'?" |
| Snippets and templates | "React to a forced door", "Daily report", "Call a REST API", "Counter of people in a zone" |
| Formatting and lint | Prettier-like formatting; lint rules for common mistakes (unawaited command, unbounded loop in handler) |
| Multi-file scripts | A script can have several files and import shared libraries published by the site |
| Diff and versions | Compare with the published version; history; roll back |
| AI-assisted help (optional, later) | Explain an error, suggest code — only if the site enables it |

### 3.1 Testing and debugging

| Tool | Description |
|---|---|
| **Run in test mode** | Executes the draft script in a test isolate with **simulated or recorded events**: pick an event from history or build one with a form; commands are **not sent** to devices but shown as "would send Open to Main entrance" |
| **Console** | Live log output of the running script, filterable |
| **Breakpoints and step-through** | V8 inspector protocol bridged to the IDE for test mode (step, watch variables, call stack) |
| **Execution history** | Each handler run: trigger, duration, outcome, logs, commands sent |
| **Metrics** | Runs per minute, average/max duration, errors, limit breaches |

### 3.2 Publishing

- Saving compiles TypeScript in the client (instant feedback) and again on the server
  (authoritative check with the same compiler version).
- A script with errors cannot be published; the previous published version keeps running.
- Publishing is a configuration change (draft → publish, versions, rollback, optional
  approval). Running scripts are swapped to the new version without losing their storage.

## 4. Why this is safer and better

| Problem in classic designs | Watchpoint |
|---|---|
| Scripts run inside the server process with full privileges | Separate process, V8 isolates, explicit rights |
| A slow script stalls all processing | Scripts run outside the event pipeline; limits per call |
| Errors appear at build time on the server, in a compiler's words | Errors as you type, in plain language, with fixes |
| Bad operand names discovered only at compile | Completion and type-checking from the live configuration |
| No test without affecting the field | Test mode with simulated events and dry-run commands |
| Reloading leaks memory | Isolates are disposed cleanly |

## 5. Limits of scripting (by design)

- No direct access to the database, file system or OS.
- No loading of DLLs or npm packages at runtime. Shared code comes from site libraries
  (TypeScript) published like scripts. A curated set of utility libraries (date handling,
  CSV, crypto hashes) is bundled.
- Outbound network only through `fetch` to allow-listed hosts.
