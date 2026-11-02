# Worked Example — Vanderbilt SPC (Intrusion)

## 1. Context and choice

Vanderbilt SPC is an intrusion panel range widely installed in Europe and the Middle East,
designed with third-party integration in mind, and test hardware is available. It is the
first intrusion integration; Honeywell Galaxy (legacy serial/gateway interfaces) and Aritech
ATS follow.

Candidate interfaces (to confirm with Vanderbilt's integration documentation for the panel
firmware in use): the panel's IP event reporting/communication protocols for events and
states, and Vanderbilt's documented integration API or gateway options for commands (arm,
disarm, bypass, outputs, doors). The choice depends on firmware versions, licensing on the
Vanderbilt side and command coverage; a technical spike with the test panel decides.

## 2. Package

| Item | Value |
|---|---|
| Package | `watchpoint.integration.vanderbilt-spc` |
| Connector | `SpcPanelConnector`, server type `vanderbilt.spc.panel` |
| Platforms | win-x64, linux-x64, linux-arm64 (pure .NET expected) |
| Isolation | `in-process-allowed` (to revisit if a vendor library is required) |
| Licence | `int.spc`, standard class |

## 3. Connection settings

| Section | Fields |
|---|---|
| Panel | Address, port(s), protocol/interface choice, panel time zone, encryption key or credentials (vault) |
| Events | Event reporting channel (panel → Watchpoint listener, authenticated), catch-up from panel log on reconnection |
| Polling | State refresh interval (areas, zones, outputs), inventory refresh |
| Command user | Panel user/code used for commands (vault) — distinct from Watchpoint users; the Watchpoint operator identity is recorded in Watchpoint's command history |

## 4. Mapping to the model

| SPC object | Watchpoint | Device type | Licence class |
|---|---|---|---|
| Panel | Server | `vanderbilt.spc.panel` (Health, Tamper, PowerSupply, Battery, clock) | free |
| Area | Device | `intrusion.area` | full |
| Zone | Device (child of its primary area; links to other areas) | `intrusion.zone` | light |
| Output (mapping gate / physical output) | Device | `generic.binary-output` or `intrusion.siren` | light |
| Door (SPC door control) | Device | `ac.door` (subset) | full |
| Keypad | Device | `intrusion.keypad` | free |
| Expander / power supply | Device | `intrusion.expander` | free |
| Communicator / ATS paths | Device | `intrusion.communicator` | free |

## 5. States and events

| SPC concept | Watchpoint |
|---|---|
| Area unset / partset / fullset, entry/exit delay, alarm | `armable.armState` |
| Zone normal / open / tamper / short / masked / fault | `zoneInput.state` |
| Zone inhibited / isolated | `bypassable.bypassed` (with sub-kind) |
| Alarm, confirmed alarm, panic, duress | Events on area/zone: `zoneInput.activated` while armed, plus panic and duress events declared by the package (`vanderbilt.spc.panic`, `vanderbilt.spc.duress`), candidates for promotion to a standard *IntrusionEvents* capability |
| Set/unset by user (panel user id) | `armable.armed` / `disarmed` with panel user reference |
| Mains fail, battery low, tamper, comm path fail | Health/PowerSupply/Battery/Tamper events |

## 6. Commands

| Watchpoint command | Panel action | Verification | Default risk |
|---|---|---|---|
| `armable.arm(full)` / `arm(partial)` | Fullset / partset area | `armState` reaches armed (after exit delay) | sensitive |
| `armable.disarm` | Unset area | `armState = disarmed` | sensitive |
| `armable.arm` failure | Returns open zones/faults preventing arming → `invalid-state` with zone list | — | — |
| `bypassable.bypass/unbypass` | Inhibit/isolate, de-inhibit zone | `bypassed` | sensitive |
| `alarmMemory.reset` | Restore / reset alarms | — | normal |
| Output `switchOn/Off` | Control output/mapping gate | output state | normal |
| `openable.open` (doors) | Momentary door release | door event | sensitive |
| Clock sync | Set panel time | drift < 2 s | normal |

## 7. Default alarm rules shipped

| Rule | Priority band |
|---|---|
| Intrusion alarm (zone activated in armed area) | Critical |
| Panic / duress | Critical |
| Zone tamper | High |
| Panel tamper, mains fail > 5 min, battery low | High / Medium / Low |
| Panel offline > 1 min | High |
| Area not armed at scheduled time (automation-based) | Medium |

## 8. Questions to confirm

- Interface per firmware version; licences/features needed on the panel.
- Authentication/encryption of event reporting and command interface.
- Command coverage (arm modes, inhibit vs isolate, doors, outputs).
- How the panel identifies users in events; event log access for catch-up.

## 9. Test plan

- Test panel with 2 areas, 8 zones, 2 outputs, 1 door, keypad.
- Scenarios: arm/disarm from Watchpoint and from keypad (states consistent), arming refused
  with open zone (reason shown), zone alarm while armed → Critical alarm, tamper, mains fail
  simulation, network cut and catch-up, clock drift detection.
