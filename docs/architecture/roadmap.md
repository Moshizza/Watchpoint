# Roadmap

Phases are ordered by dependency. Durations are deliberately not given here; they will be
estimated once the team size is known and the technical spikes are done.

## Phase 0 — Foundations and spikes

- Repository structure (service, UI, mobile, packages, docs), CI, code signing setup.
- Spikes (each ends with a short decision record):
  - NATS JetStream as event log + lease store; failover with fencing tokens (D8).
  - PostgreSQL HA: Patroni vs pg_auto_failover (D27).
  - Integration host isolation: in-process `AssemblyLoadContext` vs isolated host over gRPC;
    performance and crash behaviour.
  - CEL evaluator in TypeScript running in browser and V8/ClearScript (D14).
  - go2rtc with RTSP sources → WebRTC in Electron on Windows/Linux/macOS (H.264, H.265).
  - Vendor interface spikes: Salto SHIP, Milestone REST/API Gateway vs MIP SDK, SPC
    interfaces on the test panel.
- Device type and capability schema (JSON Schema), standard library v0.

## Phase 1 — Core MVP (single node)

- Service host (Windows service, systemd), setup wizard, `wpctl`.
- Configuration service (drafts/publish/versions), registry (servers, devices, locations,
  tags), type catalogue.
- Event pipeline, live state, history writer (Timescale), event journal.
- Commands with lifecycle and verification.
- Alarm rules, lifecycle, console, procedures (basic), sounds.
- Identity (local + LDAP), rights, user groups, rights-copy tool, audit trail.
- Integration runtime: packages folder, manifest, signing, connections, in-process + isolated
  hosts, health, discovery UI.
- Generic integrations: simulator, ping, Modbus TCP, MQTT.
- **Salto Space** integration.
- Desktop client (Electron): operation side (alarms, events, device explorer, device pages,
  commands) + configuration side (integrations, devices, locations, rules, users/rights).
- Localisation framework with en/fr/ar and RTL from the first screen.

## Phase 2 — Video, intrusion, screens

- Media gateway, video module, live/playback/PTZ in desktop client.
- **Milestone XProtect** integration.
- **Vanderbilt SPC** integration.
- Synoptics editor and viewer (plans, maps, dashboards).
- Web client (operation only).
- Notifications: e-mail, SMS (provider + GSM modem), webhooks.
- Licensing: footprint, request files, licence verification, quotas, restricted mode; first
  version of the licensing portal (requests, review queue, issuance).
- Reports v1 (alarm statistics, device availability, shift report).

## Phase 3 — Automation, resilience, multi-site

- Automations (no-code) with persisted step state.
- Scripting: script host, API, Monaco IDE with site-specific types, test mode.
- Redundancy: two nodes + witness, leases, failover, rolling upgrades.
- Scale-out roles and connection placement.
- Remote integration hosts.
- Federation: links, central console, handover schedules.
- Mobile app (alarms and events) + push relay.
- OIDC SSO, WebAuthn.

## Phase 4 — Hardening and market readiness

- Load testing programme and published capacity sheet.
- Security: penetration test, hardening checklist, SBOM, IEC 62443-4-1 process alignment.
- Honeywell Galaxy, Aritech ATS integrations; BACnet, OPC UA, SNMP packages.
- Installers and documentation for integrators; training material.
- Spanish, German, Turkish translations.

## Later

Video walls, mobile commands and video, 3D/BIM views, dashboards builder, analytics, AI
assistance in the script IDE, third-party integration SDK and partner certification.
