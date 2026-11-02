# Worked Example — Salto Space (Access Control)

## 1. Context

Salto Space is an access control system managing online doors (wired or wireless online),
offline/data-on-card doors, lockers, hotel rooms, zones and relays. Watchpoint does **not**
manage cardholders or rights: Salto does. Watchpoint supervises door states, receives
access events, and sends door commands.

Integration interface: Salto's **SHIP** host interface (XML over TCP or HTTPS, the latter
typically requiring a Salto OEM/partner licence) plus its event delivery options. Exact
calls, authentication, event formats and licensing must be taken from Salto's official
SHIP documentation obtained through the Salto partner programme.

## 2. Package

| Item | Value |
|---|---|
| Package | `watchpoint.integration.salto` |
| Connector | `SaltoSpaceConnector`, server type `salto.space` |
| Platforms | win-x64, linux-x64, linux-arm64 (pure .NET protocol implementation, no native SDK) |
| Isolation | `in-process-allowed` |
| Licence | `int.salto`, standard class |

## 3. Connection settings (generated form)

| Section | Fields |
|---|---|
| Server | Host, port, transport (TCP / HTTPS), TLS pinning (HTTPS), Salto system time zone |
| Authentication | Credentials or API key as required by the transport (stored in the vault) |
| Events | Event source: polling of the audit trail (interval) / event stream / webhook (Watchpoint hosts the authenticated inbound endpoint); catch-up after reconnection on/off |
| Polling | Online door status interval (default 2 s; adaptive: slower when nothing changes), inventory refresh interval |
| Options | Import lockers, hotel rooms, zones, relays; cardholder lookup on/off (privacy) |

## 4. Mapping to the model

| Salto object | Watchpoint | Device type | Licence class |
|---|---|---|---|
| Salto Space installation | Server | `salto.space` | free |
| Online door | Device | `ac.door` (+ online capabilities) | full |
| Offline door (data-on-card) | Device | `ac.door` with limited capabilities (no live position, events arrive late via card updates) | full |
| Door reader (if exposed) | Child device of door | `ac.reader` | free |
| Locker | Device | `ac.locker` | light |
| Hotel room | Device | `vendor.salto.hotel-room` (extends `ac.door` + Occupancy) | full |
| Zone | Device | `ac.access-zone` | free |
| Online relay / input | Device | `generic.binary-output` / `generic.binary-input` | light |
| Encoder | Device | `ac.encoder` | free |

Native ids: Salto's external identifiers (not names), so renames in Salto don't create
duplicates.

### 4.1 States

| Salto status (concept) | Property |
|---|---|
| Door open / closed / left open / forced / intrusion | `doorPosition.position` = open / closed / held-open / forced |
| Lock state, office mode, emergency mode | `lockable.lockState`, `doorMode.mode` |
| Online/offline, communication fault | `health.connectivity`, `health.faults` |
| Battery level (wireless locks) | `battery.level`, `battery.low` |
| Tamper | `tamper.tampered` |

### 4.2 Events

| Salto event (concept) | Watchpoint event |
|---|---|
| Access granted (with key/user reference, direction) | `accessEvents.granted` (credential ref, cardholder display name if allowed, direction) |
| Access denied (reason: out of schedule, no rights, expired, blacklisted…) | `accessEvents.denied` with standard reason codes |
| Door forced / left open | `doorPosition.forced` / `doorPosition.heldOpen` |
| Opened by online command | `openable.opened` correlated with the command |
| Emergency / office mode changes | `doorMode.modeChanged` |
| Low battery, tamper, offline | Health/Battery/Tamper events |

### 4.3 Commands

| Watchpoint command | Salto action (concept) | Verification |
|---|---|---|
| `openable.open` | Open online door (momentary) | door position open or "opened" event within 10 s |
| `doorMode.setMode(unlocked)` / `(normal)` | Start / end office mode | `doorMode.mode` |
| `doorMode.setMode(emergency-open)` / `(emergency-close)` / `(normal)` | Emergency open / close / end of emergency | `doorMode.mode` |
| `lockable.lock` / `unlock` (where supported) | Corresponding commands | `lockState` |
| Relay `switchOn/Off`, `pulse` | Relay activate / deactivate | relay state |
| Group commands (lockdown zone) | Multi-door request when supported (batchable) | per-door verification |

Every Salto per-door result is mapped to the command result, including Salto error codes
translated into standard failure categories ("door offline", "refused by system").

## 5. Behaviour and resilience

- **Command priority**: operator commands bypass status polling in the connector's queue.
- **Persistent cursor** for the audit trail (last event id) in the connection store →
  catch-up after outages and failover; idempotent event ids prevent duplicates.
- Bounded list reads (page limits, delay on empty pages).
- XML built with a proper serializer (escaping), never by string concatenation.
- Multi-system: several Salto connections in the same host without shared static state.
- Inbound endpoints (webhooks/event stream) authenticated and bound to the configured
  interface.

## 6. Default alarm rules shipped

| Rule | Priority band |
|---|---|
| Door forced | High |
| Door held open > 60 s (configurable per door) | Medium |
| Door offline > 2 min | Medium |
| Lock battery low | Low |
| Tamper | High |
| Repeated denied access on the same door (5 in 1 min) | Medium |
| Emergency mode activated | High (info if triggered by Watchpoint automation) |

## 7. Questions to confirm with Salto documentation

- Authentication options over TCP vs HTTPS; licence requirements for each.
- Best event source per Salto Space version (polling vs stream vs webhook), event
  ordering and identifiers for catch-up.
- Full list of door statuses, denial reasons and error codes.
- Rate limits and recommended polling intervals; bulk commands support.
- Whether Watchpoint should ever answer Salto-initiated requests (not planned: Watchpoint is
  not an access rights authority).

## 8. Test plan

- Simulator from recorded/handwritten SHIP exchanges for CI.
- Lab with a Salto Space server, 2 online doors, 1 offline door, 1 locker, 1 relay.
- Scenarios: each command with success/failure, network cut and catch-up, Salto restart,
  1,000 simulated doors status load, failover between nodes mid-command.
