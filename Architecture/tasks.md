# Task Sheet

Every unit of work for Watchpoint is a task listed here. Tasks are sized for **1 to 5 days**
of work by one person. Each task names the spec sections to implement and the tests that
must pass (see [README §12 — definition of done](README.md#12-definition-of-done-every-task)).

## 1. How to take a task

### 1.1 With GitHub issues (normal way)

Each task has a GitHub issue (column **Issue**) once its milestone is opened.

1. Pick a task whose status is `todo` and whose dependencies are all `done`.
2. Open its issue and **assign yourself** (right panel → Assignees). Assignment is instant
   and visible to everyone; if someone is already assigned, pick another task.
3. Create the branch `<owner>/<task-id>-<short-slug>` from `main`, where `<owner>` is your
   agent name from the [agent registry](#agent-registry) (e.g.
   `claude-atlas/T1.07-registry-core`) or your GitHub username if you work without an agent
   (e.g. `moshizza/T1.07-registry-core`). Never work on `main`.
4. In your first commit on that branch, set the task's **Owner** to that exact branch name and
   **Status** to `in progress`; push it and open a **draft** pull request. Agents also comment
   on the issue (they cannot be assigned on GitHub). The issue assignment or comment is the
   source of truth while the task is in progress.
5. Open a pull request titled `<task-id> <title>` with `Closes #<issue>` in the
   description, and the list of test ids implemented.
6. After approval and green CI, squash-merge. The issue closes automatically. Set the task
   **Status** to `done` in the same pull request.

### 1.2 Without GitHub issues (fallback)

If issues are not available (or a task has no issue yet):

1. Edit the task row on `main`: put your branch name in **Owner**, today's date in the status
   (`in progress (2026-11-03)`), and push that single change **before** starting work.
2. If your push is rejected because `main` moved, pull, check the row again: if someone else
   claimed it first, it is theirs.

### 1.3 Statuses

| Status | Meaning |
|---|---|
| `todo` | Not started; available when dependencies are done |
| `blocked` | Waiting for something outside the task list (say what in the issue) |
| `in progress` | Someone is working on it |
| `in review` | Pull request open |
| `done` | Merged into `main` |

A task with no visible activity (commits or comments) for **7 days** may be released by the
project lead (owner removed, status back to `todo`) after asking the owner.

### 1.4 GitHub Project board (set up once by the project owner)

1. GitHub → your profile or the repository → **Projects → New project → Board**, name it
   "Watchpoint".
2. Columns (Status field): `Todo`, `In progress`, `In review`, `Done`.
3. Add custom fields: **Milestone** (single select M0…M12), **Spec** (text), **Task id**
   (text).
4. Project settings → **Workflows**: enable "Item added → Todo", "Item reopened → Todo",
   "Pull request merged → Done", "Item closed → Done".
5. Repository → Issues → filter `label:task` → select all → "Projects" → add to
   "Watchpoint".
6. Labels used on issues: `task`, `milestone:M0`…`milestone:M12`, `spec:<ID>`, `spike`,
   `e2e`, `docs-mismatch`.

### 1.5 Agent registry

<a id="agent-registry"></a>
Every AI agent working on the project has a unique name, used as its branch prefix and in the
**Owner** column. Rules for agents: [`CLAUDE.md`](../CLAUDE.md) (non-Claude agents: also [`AGENTS.md`](../AGENTS.md)).

| Agent name | Person responsible | Added |
|---|---|---|
| `claude-atlas` | Mohcine (Moshizza) | 2026-11-03 |
| `jules-1` | Mohcine (Moshizza) | 2026-11-05 |
| `claude-orion` | Mohcine (Moshizza) | 2026-11-07 |
| `jules-3` | Mohcine (Moshizza) | 2026-11-09 |
| `claude-lyra` | Mohcine (Moshizza) | 2026-11-13 |
| `claude-vega` | Mohcine (Moshizza) | 2026-11-10 |
| `claude-rigel` | Mohcine (Moshizza) | 2026-11-14 |
| `claude-sirius` | Mohcine (Moshizza) | 2026-11-14 |
| `claude-deneb` | Mohcine (Moshizza) | 2026-11-14 |

## 2. Tasks

Columns: **Deps** = tasks that must be `done` first. **Tests** = test ids (or ranges) from the
spec that must exist and pass.

### M0 — Foundations

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T0.01 | Repository skeleton: `src/` tree, `Watchpoint.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, `.gitignore`, pnpm workspace, `.nvmrc` | README §3–§7 | — | CI-001 (solution builds empty), CI-002 (workspace installs) | [#1](https://github.com/Moshizza/Watchpoint/issues/1) | claude-atlas/T0.01-repository-skeleton | done |
| T0.02 | CI workflow `ci.yml`, issue and pull-request templates, Dependabot config, Semgrep and secret-scan jobs | README §10–§11 | T0.01 | CI-003 (workflow runs on PR), CI-004 (failing test fails CI) | [#2](https://github.com/Moshizza/Watchpoint/issues/2) | claude-atlas/T0.02-ci-workflow | done |
| T0.03 | Kernel: typed ids, `Result`/`Error`/`ErrorKind`, `TimeProvider` helpers, paging and sorting | K §4–§7 | T0.01 | K-U-001…K-U-030 | [#3](https://github.com/Moshizza/Watchpoint/issues/3) | claude-atlas/T0.03-kernel-part1 | done |
| T0.04 | Kernel: cursors, JSON options and converters, translatable texts, validation helpers, `WpValue`, `Watchpoint.Kernel.Testing` | K §7–§11 | T0.03 | K-U-031…K-U-062 | [#4](https://github.com/Moshizza/Watchpoint/issues/4) | claude-atlas/T0.04-kernel-part2 | done |
| T0.05 | Test infrastructure: `Watchpoint.Testing`, Testcontainers fixtures (PostgreSQL + TimescaleDB, NATS), architecture tests, test naming analyzer | TST | T0.03 | TST-U-001…TST-I-010 | [#5](https://github.com/Moshizza/Watchpoint/issues/5) | claude-orion/T0.05-test-infrastructure | done |
| T0.06 | JSON Schemas: capability, device type, integration manifest; schema versioning | SCM §3–§5 | T0.01 | SCM-U-001…SCM-U-020 | [#6](https://github.com/Moshizza/Watchpoint/issues/6) | jules-1/T0.06-json-schemas-14734361605283558806 | done |
| T0.07 | Standard type library v0 + `wptypes` validator CLI | SCM §6–§8 | T0.06 | SCM-U-021…SCM-U-040 | [#7](https://github.com/Moshizza/Watchpoint/issues/7) | claude-atlas/T0.07-type-library-wptypes | done |
| T0.08 | UI workspace skeleton: package scaffolds, TypeScript strict, ESLint, Prettier, Vitest, Tailwind preset | README §7, UIC §3 | T0.01 | UIC-U-001…UIC-U-003 | [#8](https://github.com/Moshizza/Watchpoint/issues/8) | claude-atlas/T0.08-ui-workspace-skeleton | done |
| T0.09 | Spike: NATS JetStream as event log and KV lease store (throughput, replay, KV CAS) → decision record | PIP §5, CLU §4 | T0.05 | Spike report | [#9](https://github.com/Moshizza/Watchpoint/issues/9) | claude-orion/T0.09-spike-nats-jetstream | done |
| T0.10 | Spike: go2rtc RTSP → WebRTC in Electron on Windows, Linux, macOS (H.264, H.265) → decision record | VID §4 | T0.01 | Spike report | [#10](https://github.com/Moshizza/Watchpoint/issues/10) | — | todo |
| T0.11 | Spike: connector isolation — `AssemblyLoadContext` in-process vs gRPC isolated host (latency, crash behaviour) → decision record | IRT §5, IHO §4 | T0.03 | Spike report | [#11](https://github.com/Moshizza/Watchpoint/issues/11) | claude-atlas/T0.11-spike-connector-isolation | done |
| T0.12 | Fix intermittent INT-U-020 and HST-I-005 | INT §10, HST §7 | — | INT-U-020, HST-I-005 | — | claude-rigel/flaky-int-u-020-hst-i-005 | done |
| T0.13 | Make wall-clock timing tests CI-safe | TST §10, STO §12 | — | STO-I-011, timing-test audit | — | claude-deneb/T0.13-ci-safe-timing-tests | in progress |

### M1 — First device on screen

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T1.01 | Host: generic host, node settings, module registration, Windows service / systemd hosting, health endpoints | HST §4–§6 | T0.04 | HST-U-001…HST-U-015, HST-U-026, HST-U-027, HST-I-001…HST-I-004 | [#12](https://github.com/Moshizza/Watchpoint/issues/12) | claude-orion/T1.01-host-finish | done |
| T1.02 | Host: process supervisor, bundled NATS start/stop, dev compose stack | HST §7–§8 | T1.01 | HST-U-016…HST-U-025, HST-U-028…HST-U-045, HST-I-005…HST-I-007, HST-I-009, HST-I-010 (HST-I-008 written, enabled by T1.16) | [#13](https://github.com/Moshizza/Watchpoint/issues/13) | claude-lyra/T1.02-process-supervisor-nats | done |
| T1.03 | Storage: store contracts, provider model, connection manager, migration runner, PostgreSQL provider skeleton | STO §4–§6 | T0.05 | STO-U-001…STO-U-015, STO-I-001…STO-I-006 | [#14](https://github.com/Moshizza/Watchpoint/issues/14) | claude-atlas/T1.03-storage-contracts | done |
| T1.04 | Storage: configuration store base (EF context per module, versioning column), state store (snapshot upserts) | STO §7–§8 | T1.03 | STO-U-016…STO-U-025, STO-I-007…STO-I-015 | [#15](https://github.com/Moshizza/Watchpoint/issues/15) | claude-atlas/T1.04-storage-state-store | done |
| T1.05 | Secrets vault: master key providers (file, DPAPI), AES-GCM, secret API, access audit hook | SEC | T1.04 | SEC-U-001…SEC-U-020, SEC-I-001…SEC-I-005 | [#16](https://github.com/Moshizza/Watchpoint/issues/16) | claude-atlas/T1.05-secrets-vault | done |
| T1.06 | Model: type catalogue (load standard library and package types, resolve `extends`, validate, query) | MOD | T0.07, T1.04 | MOD-U-001…MOD-U-035, MOD-I-001…MOD-I-004 | [#17](https://github.com/Moshizza/Watchpoint/issues/17) | jules-3/T1.06-model-type-catalogue-6607702112006375908 | done |
| T1.07 | Registry: servers, devices, locations, tags, links — entities, persistence, queries | REG §4–§7 | T1.06 | REG-U-001…REG-U-030, REG-I-001…REG-I-004, REG-I-006, REG-I-009 | [#18](https://github.com/Moshizza/Watchpoint/issues/18) | jules-3/T1.07-registry-core-1150500357666495793 | done |
| T1.08 | Registry: inventory reconciliation (rediscovery by native id, orphans, vendor names) | REG §8 | T1.07 | REG-U-031…REG-U-045, REG-I-007 | [#19](https://github.com/Moshizza/Watchpoint/issues/19) | jules-7325543338548804014-454e7657 | done |
| T1.09 | Pipeline: event log streams, envelope, partition router, log writer/reader, consumer helpers | PIP §4–§6 | T1.02, T0.09 | PIP-U-001…PIP-U-020, PIP-I-001…PIP-I-008, REG-I-005 | [#20](https://github.com/Moshizza/Watchpoint/issues/20) | — | todo |
| T1.10 | Pipeline: ingest (type validation, units, dedup, deadband, enrichment), back-pressure, side-effect dispatcher | PIP §7–§9 | T1.09, T1.07 | PIP-U-021…PIP-U-047, PIP-I-009…PIP-I-015 | [#21](https://github.com/Moshizza/Watchpoint/issues/21) | — | todo |
| T1.11 | State: live store, overall state computation, aggregates, queries, change feed | STA §4–§7 | T1.10 | STA-U-001…STA-U-035 | [#22](https://github.com/Moshizza/Watchpoint/issues/22) | — | todo |
| T1.12 | State: snapshotter and recovery (snapshot + replay) | STA §8 | T1.11 | STA-U-036…STA-U-045, STA-I-001…STA-I-006 | [#23](https://github.com/Moshizza/Watchpoint/issues/23) | — | todo |
| T1.13 | Integration contract (`Watchpoint.Integration.Abstractions`) | INT §4–§8 | T0.03, T0.04 | INT-U-001…INT-U-010 | [#24](https://github.com/Moshizza/Watchpoint/issues/24) | claude-orion/T1.13-integration-contract | done |
| T1.14 | Integration SDK helpers (polling scheduler, resilience, device map, connection store client, secure HTTP, redacting logger) | INT §9–§11 | T1.13 | INT-U-011…INT-U-040 | [#25](https://github.com/Moshizza/Watchpoint/issues/25) | jules-523469058024656279-7e0abb3b | done |
| T1.15 | Integration runtime: package folder watcher, manifest reader, verifier (dev mode), package catalogue, load contexts | IRT §4–§6 | T1.13, T1.06 | IRT-U-001…IRT-U-025, IRT-I-001…IRT-I-004 | [#26](https://github.com/Moshizza/Watchpoint/issues/26) | claude-vega/T1.15-integration-runtime-packages | done |
| T1.16 | Integration runtime: connection manager, connection state machine, watchdog, connector context implementation, connection store | IRT §7–§10 | T1.15, T1.10, T1.08, T1.05 | IRT-U-026…IRT-U-060, IRT-I-005…IRT-I-012, HST-I-008 (enable; written in T1.02) | [#27](https://github.com/Moshizza/Watchpoint/issues/27) | — | todo |
| T1.17 | Simulator connector (types, scenarios, random and scripted changes, commands with configurable outcomes) | C-SIM | T1.14 | SIM-U-001…SIM-U-030, SIM-I-002, SIM-I-003 | [#28](https://github.com/Moshizza/Watchpoint/issues/28) | jules-2365140055605449599-7f8a9063 | done |
| T1.17b | Simulator, Modbus and MQTT end-to-end tests | C-SIM, C-MODBUS, C-MQTT | T1.16, T1.07, T1.09, T4.05, T4.06 | SIM-I-001, SIM-I-004, SIM-I-005, MODB-I-002, MQTT-I-004 | — | — | todo |
| T1.18 | Identity minimal: local users, Argon2id, sign-in, access/refresh tokens, bootstrap admin, session registry | IDN §4–§7 | T1.04 | IDN-U-001…IDN-U-030, IDN-I-001…IDN-I-006 | [#29](https://github.com/Moshizza/Watchpoint/issues/29) | jules-14674020379193774426-43566f4b | done |
| T1.18r | Identity security review and fixes (T1.18 code) | IDN §4–§7 | T1.18 | IDN-U-*, IDN-I-*, new tests (IDN-U-101…IDN-U-124) | — | claude-sirius/T1.18r-identity-security-review | done |
| T1.18s | Identity settings binding (§7.1) and per-site token issuer | IDN §6–§7 | T1.18r | (tests to define) | — | — | todo |
| T1.19 | API: REST framework (problem details, paging, ETag, idempotency, permission filter, OpenAPI) + endpoints for registry, state, connections, types, dev seed | API §4–§7 | T1.11, T1.16, T1.18 | API-U-001…API-U-030, API-I-001…API-I-015, REG-I-008, REG-I-010 | [#30](https://github.com/Moshizza/Watchpoint/issues/30) | — | todo |
| T1.20 | API: live hub (subscriptions, snapshots + deltas, sequences, resume, coalescing, limits) | API §8–§10 | T1.19 | API-U-031…API-U-055, API-I-016…API-I-025 | [#31](https://github.com/Moshizza/Watchpoint/issues/31) | — | todo |
| T1.21 | UI core: generated API client, live connection client, model package | UIC §4–§6 | T1.20, T0.08 | UIC-U-004…UIC-U-030 | [#32](https://github.com/Moshizza/Watchpoint/issues/32) | — | todo |
| T1.22 | UI core: i18n (en/fr/ar, RTL, ICU), design system base (theme, layout, DataGrid, tree, state badges) | UIC §8–§10 | T0.08 | UIC-U-031…UIC-U-060 | [#33](https://github.com/Moshizza/Watchpoint/issues/33) | jules-1/T1.22-ui-core-12272030307080811265 | done |
| T1.23 | UI operation part 1: app shell, sign-in, device explorer (locations/servers), device page (read-only), connection banner | UIO §4–§6 | T1.21, T1.22 | UIO-U-001…UIO-U-030 | [#34](https://github.com/Moshizza/Watchpoint/issues/34) | — | todo |
| T1.24 | Desktop shell part 1: hardened Electron shell, site profiles, certificate pinning, token vault, preload bridge, UI hosting on the node, dev packaging | DSK §4–§8 | T1.23 | DSK-U-001…DSK-U-020 | [#35](https://github.com/Moshizza/Watchpoint/issues/35) | — | todo |
| T1.25 | E2E harness (compose stack, seeded node, Playwright for desktop and browser) + scenarios E-001…E-004 | tests/e2e-scenarios | T1.24, T1.17 | E-001…E-004 | [#36](https://github.com/Moshizza/Watchpoint/issues/36) | — | todo |

### M2 — Act and react

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T2.01 | Commands: model, validation (schema, rights hook, state), lifecycle state machine, log records | CMD §4–§6 | T1.16 | CMD-U-001…CMD-U-030 | [#37](https://github.com/Moshizza/Watchpoint/issues/37) | — | todo |
| T2.02 | Commands: per-connection priority queues, per-device serialisation, idempotency, timeouts, failover-safe queue | CMD §7–§8 | T2.01 | CMD-U-031…CMD-U-050, CMD-I-001…CMD-I-008 | [#38](https://github.com/Moshizza/Watchpoint/issues/38) | — | todo |
| T2.03 | Commands: verification and group commands | CMD §9–§10 | T2.02 | CMD-U-051…CMD-U-065 | [#39](https://github.com/Moshizza/Watchpoint/issues/39) | — | todo |
| T2.04 | Commands: REST + live messages, command journal queries | CMD §11 | T2.03, T1.20 | CMD-I-009…CMD-I-015 | [#40](https://github.com/Moshizza/Watchpoint/issues/40) | — | todo |
| T2.05 | Events: model, event type registry, operator notes, journal query API | EVT | T1.10, T1.19 | EVT-U-001…EVT-U-020, EVT-I-001…EVT-I-008 | [#41](https://github.com/Moshizza/Watchpoint/issues/41) | — | todo |
| T2.06 | Expressions: CEL subset in C# (parser, type checker against the model, cost-limited evaluator) | EXP §4–§7 | T1.06 | EXP-U-001…EXP-U-040 | [#42](https://github.com/Moshizza/Watchpoint/issues/42) | jules-1/T2.06-expressions-cel-subset-9548461993034351337 | done |
| T2.07 | Expressions: CEL subset in TypeScript + shared conformance vectors | EXP §8–§9 | T2.06, T1.21 | EXP-U-041…EXP-U-060, EXP-C-001 | [#43](https://github.com/Moshizza/Watchpoint/issues/43) | — | todo |
| T2.08 | Scheduling: calendars, holiday sets (ICS import), time triggers, site time zones | SCH | T1.04 | SCH-U-001…SCH-U-030 | [#44](https://github.com/Moshizza/Watchpoint/issues/44) | jules-17736694224672813908-6b9354a8 | done |
| T2.09 | Alarms: rule model, rule index, trigger evaluation (event, state, health, expression) | ALM §4–§6 | T2.05, T2.06 | ALM-U-001…ALM-U-035 | [#45](https://github.com/Moshizza/Watchpoint/issues/45) | — | todo |
| T2.10 | Alarms: alarm lifecycle state machine, partition-safe transitions, store, REST + live | ALM §7–§9 | T2.09 | ALM-U-036…ALM-U-070, ALM-I-001…ALM-I-010 | [#46](https://github.com/Moshizza/Watchpoint/issues/46) | — | todo |
| T2.11 | Alarms: start/end delays, composite rules, escalation, procedures, audience | ALM §10–§13 | T2.10, T2.08 | ALM-U-071…ALM-U-110 | [#47](https://github.com/Moshizza/Watchpoint/issues/47) | — | todo |
| T2.12 | Alarms: default rules from device types, alarm actions (commands, pop-ups; notification hook) | ALM §14–§15 | T2.11, T2.03 | ALM-U-111…ALM-U-125, ALM-I-011…ALM-I-014 | [#48](https://github.com/Moshizza/Watchpoint/issues/48) | — | todo |
| T2.13 | History part 1: writer (events, alarms, commands, property changes, audit placeholder), basic queries | HIS §4–§7 | T1.10 | HIS-U-001…HIS-U-020, HIS-I-001…HIS-I-010 | [#49](https://github.com/Moshizza/Watchpoint/issues/49) | — | todo |
| T2.14 | UI operation: alarm console, alarm details, procedures runner, sounds, pop-up rules | UIO §7–§9 | T2.10, T1.23 | UIO-U-031…UIO-U-070 | [#50](https://github.com/Moshizza/Watchpoint/issues/50) | — | todo |
| T2.15 | UI operation: event journal, command buttons with inline status, command journal | UIO §10–§11 | T2.04, T2.05 | UIO-U-071…UIO-U-095 | [#51](https://github.com/Moshizza/Watchpoint/issues/51) | — | todo |
| T2.16 | E2E scenarios E-010…E-016 | tests/e2e-scenarios | T2.14, T2.15, T2.12, T2.17 | E-010…E-016 | [#52](https://github.com/Moshizza/Watchpoint/issues/52) | — | todo |
| T2.17 | Desktop shell part 2: multiple windows and monitors, undocked panels, bring to front, OS notifications, sound leadership, mDNS site discovery | DSK §9–§10 | T1.24, T2.14 | DSK-U-021…DSK-U-029 | [#53](https://github.com/Moshizza/Watchpoint/issues/53) | — | todo |

### M3 — Configure safely, many users

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T3.01 | Configuration: drafts, change sets, validation framework (plain-language problems) | CFG §4–§6 | T1.07 | CFG-U-001…CFG-U-030 | [#54](https://github.com/Moshizza/Watchpoint/issues/54) | — | todo |
| T3.02 | Configuration: publish, versions, rollback, propagation through the log | CFG §7–§8 | T3.01, T1.09 | CFG-U-031…CFG-U-050, CFG-I-001…CFG-I-008 | [#55](https://github.com/Moshizza/Watchpoint/issues/55) | — | todo |
| T3.03 | Configuration: import/export (JSON, CSV/XLSX), soft locks, optimistic merge | CFG §9–§10 | T3.02 | CFG-U-051…CFG-U-070 | [#56](https://github.com/Moshizza/Watchpoint/issues/56) | — | todo |
| T3.04 | Rights: permission catalogue, grants, scopes, schedules, constraints, evaluator, visible-set cache, API filtering | RGT §4–§7 | T1.18, T1.07, T2.08, T3.01 | RGT-U-001…RGT-U-050, RGT-I-001…RGT-I-004, RGT-I-008 | [#57](https://github.com/Moshizza/Watchpoint/issues/57) | — | todo |
| T3.05 | Rights: user groups, template users, rights-copy (preview, apply, undo, re-apply), drift indicator, escalation protection | RGT §8–§10 | T3.04, T3.02 | RGT-U-051…RGT-U-090, RGT-I-005…RGT-I-007 | [#58](https://github.com/Moshizza/Watchpoint/issues/58) | — | todo |
| T3.06 | Audit trail: writer, hash chain, verification, query API | AUD | T2.13 | AUD-U-001…AUD-U-020, AUD-I-001…AUD-I-005 | [#59](https://github.com/Moshizza/Watchpoint/issues/59) | — | todo |
| T3.07 | Identity: LDAP/AD, OIDC sign-in, directory group → template mapping | IDN §8–§9 | T3.05 | IDN-U-031…IDN-U-050, IDN-I-007…IDN-I-012 | [#60](https://github.com/Moshizza/Watchpoint/issues/60) | — | todo |
| T3.08 | Identity: MFA (TOTP, WebAuthn), lockout, session management, break-glass, client kind binding | IDN §10–§12 | T1.18 | IDN-U-051…IDN-U-080 | [#61](https://github.com/Moshizza/Watchpoint/issues/61) | — | todo |
| T3.09 | Maintenance mode (site and location scope, session gate, auto-end, notifications hook) | MNT | T3.08, T3.04 | MNT-U-001…MNT-U-020, MNT-I-001…MNT-I-004 | [#62](https://github.com/Moshizza/Watchpoint/issues/62) | — | todo |
| T3.10 | UI configuration: shell, side switch, draft bar, schema-generated forms, validation panel | UIF §4–§6 | T3.02, T1.23 | UIF-U-001…UIF-U-030 | [#63](https://github.com/Moshizza/Watchpoint/issues/63) | — | todo |
| T3.11 | UI configuration: integrations catalogue, connection wizard (test, discovery, import), connection details | UIF §7 | T3.10, T1.16 | UIF-U-031…UIF-U-050 | [#64](https://github.com/Moshizza/Watchpoint/issues/64) | — | todo |
| T3.12 | UI configuration: devices, locations, tags, links, bulk edit | UIF §8 | T3.10 | UIF-U-051…UIF-U-070 | [#65](https://github.com/Moshizza/Watchpoint/issues/65) | — | todo |
| T3.13 | UI configuration: alarm rules (with CEL editor), procedures editor | UIF §9 | T3.10, T2.11, T2.07 | UIF-U-071…UIF-U-090 | [#66](https://github.com/Moshizza/Watchpoint/issues/66) | — | todo |
| T3.14 | UI configuration: users, groups, rights editor, rights-copy wizard, sessions, maintenance dialog | UIF §10–§11 | T3.10, T3.05, T3.09, T3.06 | UIF-U-091…UIF-U-120 | [#67](https://github.com/Moshizza/Watchpoint/issues/67) | — | todo |
| T3.16 | Desktop shell part 3: workstation enrolment and certificate sign-in, kiosk mode | DSK §11–§12 | T2.17, T3.08 | DSK-U-030…DSK-U-034 | [#69](https://github.com/Moshizza/Watchpoint/issues/69) | — | todo |
| T3.15 | E2E scenarios E-020…E-028 | tests/e2e-scenarios | T3.11…T3.14 | E-020…E-028 | [#68](https://github.com/Moshizza/Watchpoint/issues/68) | — | todo |

### M4 — Real protocols

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T4.01 | Isolated integration host process, gRPC protocol, resource limits, restart policy | IHO §4–§7 | T1.16, T0.11 | IHO-U-001…IHO-U-030, IHO-I-001…IHO-I-008 | [#70](https://github.com/Moshizza/Watchpoint/issues/70) | — | todo |
| T4.02 | Remote integration host: pairing, mTLS, outbound connection, package push, buffering | IHO §8–§10 | T4.01, T1.05 | IHO-U-031…IHO-U-050, IHO-I-009…IHO-I-014, ALM-U-126 | [#71](https://github.com/Moshizza/Watchpoint/issues/71) | — | todo |
| T4.03 | Connector conformance suite (`Watchpoint.Integration.Testing`) applied to C-SIM | INT §12 | T1.17 | INT-C-001…INT-C-040 | [#72](https://github.com/Moshizza/Watchpoint/issues/72) | jules-1/T4.03-connector-conformance-suite-4547813729490188497 | done |
| T4.04 | Ping / HTTP check connector | C-PING | T4.03 | PING-U-001…PING-U-020, PING-I-001…PING-I-004, conformance | [#73](https://github.com/Moshizza/Watchpoint/issues/73) | — | todo |
| T4.05 | Modbus TCP connector with mapping templates | C-MODBUS | T4.03 | MODB-U-001…MODB-U-030, MODB-I-001, MODB-I-003…MODB-I-006, conformance | [#74](https://github.com/Moshizza/Watchpoint/issues/74) | jules-4/T4.05-modbus-tcp-connector-275718167346246961 | done |
| T4.05c | Modbus integration tests to spec: MODB-I-001 (3 units, 200 points, polling), MODB-I-003 (server restart/reset, qualities, recovery), MODB-I-006 (50 units × 100 registers load benchmark); the T4.05 versions test other things | C-MODBUS §tests | T4.05 | MODB-I-001, MODB-I-003, MODB-I-006 | — | — | todo |
| T4.06 | MQTT connector | C-MQTT | T4.03 | MQTT-U-001…MQTT-U-022, MQTT-I-001, MQTT-I-002, MQTT-I-003, MQTT-I-005, MQTT-I-006, conformance | [#75](https://github.com/Moshizza/Watchpoint/issues/75) | jules-4049734125398880372-2291d2a8 | done |
| T4.07 | SNMP connector (polling, traps, MIB import, inbound UDP contract) | C-SNMP | T4.03 | SNMP-U-001…SNMP-U-022, SNMP-I-001…SNMP-I-006, conformance | [#76](https://github.com/Moshizza/Watchpoint/issues/76) | — | todo |
| T4.09 | Package upgrades per connection: settings migrations, canary, automatic rollback, bulk upgrade | IRT §11 | T4.01 | IRT-U-061…IRT-U-063 | [#78](https://github.com/Moshizza/Watchpoint/issues/78) | — | todo |
| T4.08 | E2E scenarios E-030…E-034 | tests/e2e-scenarios | T4.01…T4.07 | E-030…E-034 | [#77](https://github.com/Moshizza/Watchpoint/issues/77) | — | todo |

### M5 — History, reports, notifications

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T5.01 | History part 2: retention policies, compression, continuous aggregates / aggregation jobs | HIS §8–§10 | T2.13 | HIS-U-021…HIS-U-040, HIS-I-011…HIS-I-020 | [#79](https://github.com/Moshizza/Watchpoint/issues/79) | — | todo |
| T5.02 | History: archive export (Parquet), privacy purge and pseudonymisation | HIS §11–§12 | T5.01 | HIS-U-041…HIS-U-055 | [#80](https://github.com/Moshizza/Watchpoint/issues/80) | — | todo |
| T5.03 | File store (local + replication interface), file references, quotas | FIL | T1.04, T1.19 | FIL-U-001…FIL-U-020, FIL-I-001…FIL-I-005 | [#81](https://github.com/Moshizza/Watchpoint/issues/81) | — | todo |
| T5.04 | Notifications core: router, recipients, templates, delivery queue, e-mail and webhook channels | NOT §4–§8 | T2.12, T3.02, T3.04 | NOT-U-001…NOT-U-040, NOT-I-001…NOT-I-006 | [#82](https://github.com/Moshizza/Watchpoint/issues/82) | — | todo |
| T5.05 | Notifications: SMS providers, GSM modem, on-call rosters, flood summariser, quotas | NOT §9–§11 | T5.04, T2.08 | NOT-U-041…NOT-U-070 | [#83](https://github.com/Moshizza/Watchpoint/issues/83) | — | todo |
| T5.06 | Reports: template model, HTML rendering, PDF (headless Chromium), XLSX, CSV, standard templates | RPT §4–§7 | T5.01, T5.03, T3.02 | RPT-U-001…RPT-U-030, RPT-I-001…RPT-I-003 | [#84](https://github.com/Moshizza/Watchpoint/issues/84) | — | todo |
| T5.07 | Reports: scheduling and delivery; UI for reports | RPT §8 | T5.06, T5.04 | RPT-U-031…RPT-U-045, RPT-I-004, RPT-I-005 | [#85](https://github.com/Moshizza/Watchpoint/issues/85) | — | todo |
| T5.08 | E2E scenarios E-040…E-044 | tests/e2e-scenarios | T5.02…T5.07 | E-040…E-044 | [#86](https://github.com/Moshizza/Watchpoint/issues/86) | — | todo |

### M6 — Synoptics, video, web

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T6.01 | Synoptics service: document schema, storage, binding validation, migrations of documents | SYN §4–§6 | T3.02, T5.03 | SYN-U-001…SYN-U-025 | [#87](https://github.com/Moshizza/Watchpoint/issues/87) | — | todo |
| T6.02 | Synoptic viewer (Konva, viewport subscriptions, level of detail, alarm focus) | SYN §7–§8 | T6.01, T2.14 | SYN-U-026…SYN-U-055 | [#88](https://github.com/Moshizza/Watchpoint/issues/88) | — | todo |
| T6.03 | Synoptic editor (canvas, layers, import backgrounds, device drag-and-drop, smart symbols, validation, simulation mode) | SYN §9–§11 | T6.02, T3.10 | SYN-U-056…SYN-U-100 | [#89](https://github.com/Moshizza/Watchpoint/issues/89) | — | todo |
| T6.04 | Map synoptics (MapLibre, PMTiles served by the node) | SYN §12 | T6.02 | SYN-U-101…SYN-U-115 | [#90](https://github.com/Moshizza/Watchpoint/issues/90) | — | todo |
| T6.05 | Video module: go2rtc supervision, stream requests, tickets, rights, PTZ arbiter | VID §4–§7 | T0.10, T1.16, T4.01, T5.03 | VID-U-001…VID-U-035, VID-I-001…VID-I-006 | [#91](https://github.com/Moshizza/Watchpoint/issues/91) | — | todo |
| T6.06 | Video UI: tiles, layouts, playback timeline, PTZ controls, joystick | VID §8–§9 | T6.05, T2.14 | VID-U-036…VID-U-060 | [#92](https://github.com/Moshizza/Watchpoint/issues/92) | — | todo |
| T6.07 | Generic RTSP/ONVIF camera connector | C-RTSP | T4.03, T6.05 | RTSP-U-001…RTSP-U-020, RTSP-I-001…RTSP-I-006, conformance | [#93](https://github.com/Moshizza/Watchpoint/issues/93) | — | todo |
| T6.08 | Web client app (operation-only build, session kind, hosting by the node) | WEB | T2.15, T6.02, T6.06 | WEB-U-001…WEB-U-020 | [#94](https://github.com/Moshizza/Watchpoint/issues/94) | — | todo |
| T6.09 | E2E scenarios E-050…E-056 | tests/e2e-scenarios | T6.03, T6.04, T6.07, T6.08 | E-050…E-056 | [#95](https://github.com/Moshizza/Watchpoint/issues/95) | — | todo |

### M7 — Licensing

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T7.01 | Footprint collector (Windows, Linux, VM, container host-id), licence file verification, quotas | LIC §4–§7 | T1.07, T1.16, T1.18 | LIC-U-001…LIC-U-045 | [#96](https://github.com/Moshizza/Watchpoint/issues/96) | — | todo |
| T7.02 | Activation requests (online, offline file), licence import, enforcement and restricted mode, UI panel | LIC §8–§12 | T7.01, T3.02, T3.10 | LIC-U-046…LIC-U-080, LIC-I-001…LIC-I-006 | [#97](https://github.com/Moshizza/Watchpoint/issues/97) | — | todo |
| T7.03 | Licensing portal backend: accounts, orders, requests, review queue, issuance with signing key, check-in API | LPT §4–§8 | T0.04 | LPT-U-001…LPT-U-050, LPT-I-001…LPT-I-010 | [#98](https://github.com/Moshizza/Watchpoint/issues/98) | — | todo |
| T7.04 | Licensing portal UI (customers, partners, licensing team) | LPT §9 | T7.03 | LPT-U-051…LPT-U-070 | [#99](https://github.com/Moshizza/Watchpoint/issues/99) | — | todo |
| T7.05 | E2E scenarios E-060…E-064 | tests/e2e-scenarios | T7.02, T7.04 | E-060…E-064 | [#100](https://github.com/Moshizza/Watchpoint/issues/100) | — | todo |

### M8 — Automate

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T8.01 | Automations engine: triggers, conditions, steps, persisted runs, concurrency policies, dry run | AUT §4–§8 | T2.12, T2.07, T3.02 | AUT-U-001…AUT-U-060, AUT-I-001…AUT-I-008 | [#101](https://github.com/Moshizza/Watchpoint/issues/101) | — | todo |
| T8.02 | Automations editor (React Flow) and runs panel | AUT §9–§10 | T8.01, T3.10 | AUT-U-061…AUT-U-085 | [#102](https://github.com/Moshizza/Watchpoint/issues/102) | — | todo |
| T8.03 | Script host process: V8 isolates (ClearScript), limits, gRPC protocol, restart | SCR §4–§6 | T4.01, T3.02 | SCR-U-001…SCR-U-030, SCR-I-001…SCR-I-006 | [#103](https://github.com/Moshizza/Watchpoint/issues/103) | — | todo |
| T8.04 | Script API (service side gateway, triggers, devices, commands, alarms, virtual devices, storage, fetch allow-list) | SCR §7–§9 | T8.03, T8.01 | SCR-U-031…SCR-U-070 | [#104](https://github.com/Moshizza/Watchpoint/issues/104) | — | todo |
| T8.05 | Script IDE: Monaco, generated site typings, diagnostics, versions | SCR §10–§11 | T8.04, T3.10 | SCR-U-071…SCR-U-090 | [#105](https://github.com/Moshizza/Watchpoint/issues/105) | — | todo |
| T8.06 | Script test mode and debugging (simulated events, dry-run commands, inspector bridge) | SCR §12–§13 | T8.05 | SCR-U-091…SCR-U-105 | [#106](https://github.com/Moshizza/Watchpoint/issues/106) | — | todo |
| T8.07 | E2E scenarios E-070…E-075 | tests/e2e-scenarios | T8.02, T8.06 | E-070…E-075 | [#107](https://github.com/Moshizza/Watchpoint/issues/107) | — | todo |

### M9 — Cluster, health, federation

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T9.01 | Cluster membership, node roles, lease manager with fencing tokens | CLU §4–§6 | T0.09, T1.09 | CLU-U-001…CLU-U-035, CLU-I-001…CLU-I-006 | [#108](https://github.com/Moshizza/Watchpoint/issues/108) | — | todo |
| T9.02 | Failover of connections, pipeline partitions, command queues, timers | CLU §7–§8 | T9.01, T2.03 | CLU-U-036…CLU-U-060, CLU-I-007…CLU-I-015 | [#109](https://github.com/Moshizza/Watchpoint/issues/109) | — | todo |
| T9.03 | Placement engine, drain, rolling upgrade support, client node lists | CLU §9–§10 | T9.02 | CLU-U-061…CLU-U-080 | [#110](https://github.com/Moshizza/Watchpoint/issues/110) | — | todo |
| T9.04 | Spike + integration: PostgreSQL HA (Patroni vs pg_auto_failover), witness packaging | CLU §11 | T9.01 | Spike report, CLU-I-016…CLU-I-020 | [#111](https://github.com/Moshizza/Watchpoint/issues/111) | — | todo |
| T9.05 | System health dashboard: collectors, thresholds, capacity advisor, diagnostic bundle | HLT | T9.01 | HLT-U-001…HLT-U-030 | [#112](https://github.com/Moshizza/Watchpoint/issues/112) | — | todo |
| T9.06 | Federation links: mTLS gRPC, filters, resume cursors, federation identity | FED §4–§7 | T9.02, T3.04 | FED-U-001…FED-U-040, FED-I-001…FED-I-004 | [#113](https://github.com/Moshizza/Watchpoint/issues/113) | — | todo |
| T9.07 | Federation: central console, remote commands, handover schedules, remote queries | FED §8–§10 | T9.06, T2.11 | FED-U-041…FED-U-070, FED-I-005…FED-I-008 | [#114](https://github.com/Moshizza/Watchpoint/issues/114) | — | todo |
| T9.08 | Load-test programme (k6 + device simulator), capacity sheet | tests/performance-tests | T9.03 | P-001…P-012 | [#115](https://github.com/Moshizza/Watchpoint/issues/115) | — | todo |
| T9.09 | E2E scenarios E-080…E-088 | tests/e2e-scenarios | T9.04, T9.07 | E-080…E-088 | [#116](https://github.com/Moshizza/Watchpoint/issues/116) | — | todo |

### M10 — Mobile

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T10.01 | Mobile app (sign-in, alarms, details, events, notifications settings, profile) | MOB | T2.14, T3.08, T5.04 | MOB-U-001…MOB-U-050 | [#117](https://github.com/Moshizza/Watchpoint/issues/117) | — | todo |
| T10.02 | Push relay service + node push channel | PSH | T5.04 | PSH-U-001…PSH-U-025, PSH-I-001…PSH-I-004 | [#118](https://github.com/Moshizza/Watchpoint/issues/118) | — | todo |
| T10.03 | E2E scenarios E-090…E-093 | tests/e2e-scenarios | T10.01, T10.02 | E-090…E-093 | [#119](https://github.com/Moshizza/Watchpoint/issues/119) | — | todo |

### M11 — Fit any IT, ship it

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T11.01 | SQL Server provider (all four stores), certification suite on SQL Server | STO-MS §4–§8 | T5.02 | STO-MS-I-001…STO-MS-I-030 | [#120](https://github.com/Moshizza/Watchpoint/issues/120) | — | todo |
| T11.02 | Store mapping and `wpctl storage migrate` | STO-MS §9–§10 | T11.01 | STO-MS-U-001…STO-MS-U-020, STO-MS-I-031…STO-MS-I-040 | [#121](https://github.com/Moshizza/Watchpoint/issues/121) | — | todo |
| T11.03 | Installers: Inno Setup (Windows), .deb/.rpm (nfpm), OCI images, Helm chart, client installers | DEP §3–§6 | T9.03 | DEP-I-001…DEP-I-015 | [#122](https://github.com/Moshizza/Watchpoint/issues/122) | — | todo |
| T11.04 | Setup wizard (first run) | DEP §7 | T11.03, T7.02 | DEP-U-001…DEP-U-020 | [#123](https://github.com/Moshizza/Watchpoint/issues/123) | — | todo |
| T11.05 | Signed updates (node, clients, packages), backups and restore, `wpctl` commands | DEP §8–§10 | T11.03 | DEP-U-021…DEP-U-050, DEP-I-016…DEP-I-025 | [#124](https://github.com/Moshizza/Watchpoint/issues/124) | — | todo |
| T11.06 | Security hardening checklist, SBOM and third-party notices generation, penetration test preparation | tests/security-tests | T11.05 | S-001…S-030 | [#125](https://github.com/Moshizza/Watchpoint/issues/125) | — | todo |
| T11.07 | E2E scenarios E-100…E-105 | tests/e2e-scenarios | T11.02, T11.05 | E-100…E-105 | [#126](https://github.com/Moshizza/Watchpoint/issues/126) | — | todo |

### M12 — Vendor integrations (specs completed when vendor documentation is available)

| ID | Task | Spec | Deps | Tests | Issue | Owner | Status |
|---|---|---|---|---|---|---|---|
| T12.01 | Salto Space connector | C-SALTO | T4.03 | SALTO-*, conformance | — | — | blocked (vendor docs) |
| T12.02 | Milestone XProtect connector | C-MILE | T4.03, T6.05, T4.01 | MILE-*, conformance | — | — | blocked (vendor docs) |
| T12.03 | Vanderbilt SPC connector | C-SPC | T4.03 | SPC-*, conformance | — | — | blocked (vendor docs) |

## 3. Documentation tasks (done by the documentation author)

| ID | Task | Status |
|---|---|---|
| D.01 | Architecture README, build order, task sheet | done |
| D.02 | M0 specs (K, TST, SCM) | done |
| D.03 | M1 specs | done |
| D.04 | Cross-module tests (integration matrix, e2e scenarios, performance, security) | done for M1–M11 (M12 with vendor specs) |
| D.05 | M2–M11 specs | done |
| D.06 | M12 specs | blocked (vendor docs) |
