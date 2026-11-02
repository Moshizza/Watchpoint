# Watchpoint Documentation

Architecture and module design of **Watchpoint**, a security hypervision platform (PSIM).

## Reading order

1. [Overview](architecture/overview.md) — what Watchpoint is, principles, scope of v1
2. [Glossary](architecture/glossary.md)
3. [System architecture](architecture/system-architecture.md) — components, data stores,
   deployment shapes, main flows
4. [Technology stack](architecture/technology-stack.md) — choices and why
5. [Servers and devices](modules/servers-and-devices.md) — the universal model
6. [Integrations](modules/integrations.md) — packages, connectors, connections, hosts

## Architecture

| Document | Content |
|---|---|
| [Overview](architecture/overview.md) | Vision, principles, what changes compared with classic PSIMs, v1 scope |
| [Glossary](architecture/glossary.md) | Vocabulary |
| [System architecture](architecture/system-architecture.md) | Components, data stores, deployments, flows |
| [Technology stack](architecture/technology-stack.md) | Technologies, licences, rationale |
| [Decision log](architecture/decisions.md) | Main decisions and their status |
| [Roadmap](architecture/roadmap.md) | Phases |

## Modules

| Document | Content |
|---|---|
| [Servers and devices](modules/servers-and-devices.md) | Servers, devices, device types, capabilities, locations, tags |
| [Integrations](modules/integrations.md) | Packages of DLLs, manifest, connector contract, connections, isolation, upgrades |
| [Commands](modules/commands.md) | Typed commands, lifecycle, risk levels, verification |
| [Core runtime](modules/core-runtime.md) | Service host, event pipeline, live state, back-pressure, health |
| [Events and alarms](modules/events-and-alarms.md) | Events, alarm rules, lifecycle, procedures, escalation, handover |
| [Users and rights](modules/users-and-rights.md) | Users, user groups, rights, rights-copy tool, authentication, audit, privacy |
| [Configuration management](modules/configuration-management.md) | Drafts, validation, publish, versions, import/export |
| [Synoptics](modules/synoptics.md) | No-code interactive screens |
| [Automations](modules/automations.md) | No-code flows |
| [Scripting](modules/scripting.md) | TypeScript scripts, sandbox, IDE |
| [Video](modules/video.md) | Media gateway, live, playback, PTZ |
| [API and live protocol](modules/api-and-protocol.md) | REST, live stream, versioning |
| [Notifications](modules/notifications.md) | E-mail, SMS, push, webhooks |
| [History and reports](modules/history-and-reports.md) | Storage, retention, reports |
| [Licensing](modules/licensing.md) | Licensed items, editions, prices, footprint, activation, portal, enforcement |
| [Redundancy and multi-site](modules/redundancy-and-multi-site.md) | Failover, scale-out, federation |
| [Localisation](modules/localisation.md) | Languages, RTL, time zones |

## Clients

| Document | Content |
|---|---|
| [Desktop client](clients/desktop-client.md) | One app for operation and configuration, maintenance mode |
| [Web client](clients/web-client.md) | Operation only, performance protection |
| [Mobile app](clients/mobile-app.md) | Alarms and events |

## Operations

| Document | Content |
|---|---|
| [Security](operations/security.md) | Transport, vault, supply chain, isolation, logging |
| [Deployment and operations](operations/deployment-and-operations.md) | Installers, setup wizard, upgrades, backups, monitoring |
| [Performance and capacity](operations/performance-and-capacity.md) | Load model, targets, load tests, sizing |

## Integration worked examples

| Document | Content |
|---|---|
| [Overview](integrations/README.md) | Why these three |
| [Salto Space](integrations/salto-space.md) | Access control |
| [Milestone XProtect](integrations/milestone-xprotect.md) | Video |
| [Vanderbilt SPC](integrations/vanderbilt-spc.md) | Intrusion |

## Conventions

- Diagrams use [Mermaid](https://mermaid.js.org/), rendered by GitHub.
- Vendor protocol details are to be confirmed with official vendor documentation; Watchpoint
  is designed independently from other products' implementations.
