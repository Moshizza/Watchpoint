# Architecture Decision Log

Short records of the main decisions. Status: **Accepted** (agreed with the product owner),
**Proposed** (recommended, to be confirmed), **Spike** (needs a technical experiment).

| # | Decision | Status | Rationale (short) | Document |
|---|---|---|---|---|
| D1 | Watchpoint is integration-only: no own access control, VMS, intrusion or fire functions | Accepted | Product positioning | [Overview](overview.md) |
| D2 | Model = Servers → Devices → Device types → Capabilities (typed properties, commands, events) | Accepted | Universal, typed, extensible by data | [Servers and devices](../modules/servers-and-devices.md) |
| D3 | Integrations are packages of DLLs in a folder, discovered by manifest, run as connections inside Watchpoint | Accepted | User requirement; simpler installation | [Integrations](../modules/integrations.md) |
| D4 | Connections can run in-process, in an isolated local host or in a remote host — same code | Proposed | Keeps crash isolation and Windows-only SDK support without separate products | [Integrations §6](../modules/integrations.md#6-integration-hosts-and-isolation) |
| D5 | Packages are signed; manifest is read without executing code | Proposed | Integrity, predictable discovery | [Integrations §8](../modules/integrations.md#8-trust-signing-and-licensing-of-packages) |
| D6 | Service in .NET 10, hosted as Windows service / systemd / container | Proposed | DLL plugins, vendor SDKs, cross-platform | [Technology stack](technology-stack.md) |
| D7 | Default database: PostgreSQL + TimescaleDB, bundled | Accepted | Free, no size limits, cross-platform, time-series history | [Technology stack](technology-stack.md) |
| D8 | NATS JetStream as durable event log and cluster coordination (leases) | Spike | Crash-safe state, failover, federation resume | [Core runtime](../modules/core-runtime.md) |
| D9 | One React + TypeScript UI for desktop and web; desktop shell = Electron | Proposed | Shared code, consistent WebRTC on all OSes (Tauri rejected for Linux/macOS web view variance) | [Technology stack §2.4](technology-stack.md#24-why-electron-for-the-desktop-client-and-not-tauri-or-a-native-ui-toolkit) |
| D10 | Single desktop client for operation and configuration; web = operation only; mobile = alarms and events | Accepted | User requirement | [Desktop client](../clients/desktop-client.md) |
| D11 | System maintenance mode suspends operation sessions; processing and alarms continue | Accepted (behaviour details proposed) | User requirement with safety rules | [Desktop client §6](../clients/desktop-client.md#6-suspending-operation-maintenance-mode) |
| D12 | No profiles: rights on users, user groups for organisation, rights-copy tool with template users, preview, re-apply and drift indicator | Accepted | User requirement | [Users and rights](../modules/users-and-rights.md) |
| D13 | Synoptics are declarative documents rendered by a fixed engine (Konva); no compilation | Accepted | No code errors for non-developers | [Synoptics](../modules/synoptics.md) |
| D14 | CEL for expressions, one TypeScript evaluator shared by clients and server sandbox | Spike | Safe, bounded, consistent | [Technology stack §2.6](technology-stack.md#26-why-cel-for-expressions) |
| D15 | Scripting in TypeScript, Monaco IDE, V8 isolates (ClearScript) in a separate process | Proposed | Modern IDE experience and real sandbox | [Scripting](../modules/scripting.md) |
| D16 | Commands are first-class objects with lifecycle, results and verification | Accepted | Main weakness of legacy PSIMs | [Commands](../modules/commands.md) |
| D17 | Alarm rules are separate entities; device types ship default rules | Proposed | Flexibility and zero-config start | [Events and alarms](../modules/events-and-alarms.md) |
| D18 | Video via media gateway (go2rtc) to WebRTC; Watchpoint never records | Proposed | Browser-native video, no vendor players on PCs | [Video](../modules/video.md) |
| D19 | Licensing: signed licence file, machine footprint with tolerance, manual review by licensing team, online/offline activation, perpetual + SUP / subscription / customer cloud | Accepted (prices proposed) | User requirement and market practice | [Licensing](../modules/licensing.md) |
| D20 | A licence problem never stops supervision (restricted mode keeps alarms working) | Proposed | Safety | [Licensing §8](../modules/licensing.md#8-enforcement--safety-first) |
| D21 | Redundancy with leases and fencing tokens; witness for two-node sites | Proposed | No split brain | [Redundancy](../modules/redundancy-and-multi-site.md) |
| D22 | Federation of autonomous sites with resumable, rights-checked links and alarm handover | Accepted (design proposed) | Multi-site requirement | [Redundancy §4](../modules/redundancy-and-multi-site.md#4-multi-site-federation) |
| D23 | Configuration via drafts, validation, atomic publish, versions and rollback | Proposed | Safe concurrent configuration | [Configuration management](../modules/configuration-management.md) |
| D24 | Languages v1: en, fr, ar (RTL); next es, de, tr | Accepted | Target markets | [Localisation](../modules/localisation.md) |
| D25 | Push notifications through a company-operated push relay (minimal payload) | Proposed | Required by Apple/Google; offline sites use other channels | [Notifications](../modules/notifications.md) |
| D26 | First integrations: Salto Space, Milestone XProtect, Vanderbilt SPC; then Honeywell Galaxy, Aritech ATS | Accepted | Popularity and available test hardware | [Integration examples](../integrations/README.md) |
| D27 | PostgreSQL HA supervisor: Patroni or pg_auto_failover | Spike | Both free; choose on operational simplicity | [Redundancy §2.2](../modules/redundancy-and-multi-site.md#22-components-and-how-each-is-made-redundant) |
| D28 | Pluggable storage module: store interfaces, database providers in tiers (PostgreSQL default; SQL Server supported in phase 2; MySQL/MariaDB, Oracle, ClickHouse on demand), per-store mapping, migration between databases, certification suite | Accepted | Compatibility with the databases customers already use, without multiplying risk | [Data storage](../modules/data-storage.md) |
