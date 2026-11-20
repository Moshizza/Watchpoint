# C-SNMP — SNMP Connector

| | |
|---|---|
| Spec id | C-SNMP |
| Projects | `src/integrations/Watchpoint.Integration.Snmp` (package), tests `tests/integrations/Watchpoint.Integration.Snmp.Tests` |
| Package id | `watchpoint.integration.snmp` (builtin, licence class `standard`) |
| Milestone | M4 |
| Tasks | T4.07 |
| Event id range | 5400–5499 |
| Reference | RFC 3411–3418 (SNMPv3, USM), RFC 1905/3416 (v2c PDUs), RFC 2578–2580 (SMIv2), RFC 3826 (AES), RFC 7860 (HMAC-SHA-2) — public |

## 1. Purpose and scope

Supervise network and infrastructure equipment over **SNMP**: switches, routers, UPS, PDUs,
NVR/recorders, environmental probes, servers, printers. Poll values (GET/GETBULK, table
walks), receive **traps and informs**, map them to devices, properties and events through
**device profiles**, and import **MIB** files to name OIDs and enumerations.

Supported: SNMP v1, v2c, v3 (USM: noAuthNoPriv, authNoPriv, authPriv; auth MD5 (legacy, warning),
SHA-1, SHA-224/256/384/512; priv DES (legacy, warning), AES-128/192/256), traps v1/v2c/v3,
informs (acknowledged), SET for commands.

Not in scope v1: SNMP agent mode, AgentX, TSM/DTLS transport.

## 2. Dependencies

Abstractions, SDK, **Lextm.SharpSnmpLib** (MIT) for PDUs, BER, USM and transport. MIB parsing
is implemented in the package (`Mib/`, SMIv1/v2 subset, §5) because the library's MIB compiler
is not part of the free edition. Tests: SharpSnmpLib agent samples hosted in-process
(`TestAgent`) and the `net-snmp` `snmpd`/`snmptrap` container (BSD-style licence, test-only).

Contract addition: INT `IInboundEndpoints.RegisterUdpAsync(port, bindAddress, handler)` with
manifest permission `inboundUdp` (added to INT §7.3 and IRT by T4.07; IHO bridges it like TCP:
listener local to the host). Several SNMP connections share UDP 162 through a **node-level trap
dispatcher** (§6.3).

## 3. Layout

Log event ids: 5400–5499 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Integration.Snmp/
├── manifest.json, settings/snmp.settings.json
├── types/snmp.agent.json (server per agent), net.switch-port.json, ups.unit.json, env.probe.json (package types)
├── capabilities/net.interface.json, power.ups.json
├── profiles/   built-in device profiles: generic-mib2.json, if-mib-ports.json, ups-mib-rfc1628.json, host-resources.json
├── mibs/       bundled public IETF MIBs (SNMPv2-SMI, SNMPv2-TC, SNMPv2-MIB, IF-MIB, UPS-MIB, HOST-RESOURCES-MIB, ENTITY-MIB) — licences permit redistribution
├── i18n/, docs/
└── src/
    ├── SnmpConnector.cs, AgentSession.cs (per agent: credentials, engine discovery v3, request ids)
    ├── Polling/PollPlanner.cs (GET batching by max varbinds, GETBULK walks), TableWalker.cs
    ├── Profiles/DeviceProfile.cs, ProfileLoader.cs, ValueMapper.cs
    ├── Traps/TrapReceiver.cs, TrapMapper.cs, TrapDispatcher.cs (node-level, see §6.3)
    ├── Mib/MibLexer.cs, MibParser.cs, MibModule.cs, MibRepository.cs (OID ⇄ names, enums, TCs, notifications)
    ├── Commands/SetMapper.cs
    ├── SnmpDiagnostics.cs, SnmpLog.cs
```

## 4. Device profiles

```json
{
  "id": "if-mib-ports",
  "name": { "en": "Interfaces (IF-MIB)" },
  "requires": ["IF-MIB"],
  "scalars": [
    { "oid": "SNMPv2-MIB::sysUpTime.0", "property": "health.lastContact", "mode": "contactOnly" }
  ],
  "tables": [
    {
      "id": "ports",
      "indexOid": "IF-MIB::ifIndex",
      "device": { "nativeId": "if{index}", "type": "net.switch-port", "name": "{IF-MIB::ifDescr}",
                  "include": "IF-MIB::ifType in [6, 117]" },
      "columns": [
        { "oid": "IF-MIB::ifOperStatus", "property": "net.interface.operStatus", "map": { "1": "up", "2": "down", "7": "lowerLayerDown" } },
        { "oid": "IF-MIB::ifAdminStatus", "property": "net.interface.adminStatus" },
        { "oid": "IF-MIB::ifHCInOctets", "property": "net.interface.inBitsPerSecond", "rate": "counter64", "scale": 8 },
        { "oid": "IF-MIB::ifAlias", "attribute": "alias" }
      ],
      "commands": {
        "net.interface.disable": { "set": [ { "oid": "IF-MIB::ifAdminStatus.{index}", "type": "Integer32", "value": 2 } ] },
        "net.interface.enable":  { "set": [ { "oid": "IF-MIB::ifAdminStatus.{index}", "type": "Integer32", "value": 1 } ] }
      }
    }
  ],
  "traps": [
    { "notification": "IF-MIB::linkDown", "device": "if{IF-MIB::ifIndex}", "event": "net.interface.linkDown" },
    { "notification": "IF-MIB::linkUp",   "device": "if{IF-MIB::ifIndex}", "event": "net.interface.linkUp" }
  ]
}
```

| Field | Rules |
|---|---|
| OIDs | Symbolic `MODULE::name[.suffix]` (resolved through the MIB repository) or numeric `1.3.6.1…` |
| `scalars[]` | Values attached to the **agent device** (the server) or to a named device (`device`) |
| `tables[]` | One device per row (index from the table's INDEX; `{index}` = full index suffix); `include` filter (CEL-like subset: `in`, `==`, `!=`, `&&`, `||` over column values) |
| `map` | Raw integer/string → property enum/boolean; MIB enum labels used automatically when a column is an enumerated INTEGER and no map is given (label → enum value of the same name if it exists) |
| `rate` | `counter32`, `counter64`: property = Δvalue / Δtime × scale (wrap handled, discontinuity on agent restart detected by sysUpTime decrease) |
| `scale`, `offset`, `unit` | numeric conversion |
| `mode: contactOnly` | the value is not stored, it only proves contact (updates `lastContact`) |
| `traps[]` | `notification` (MIB name or OID), `device` (template over varbinds), `event` path, `payload` (varbind → event field map) |

Profiles are assigned per agent (§5 settings); site profiles can be added in settings like
Modbus templates. Validation as C-MODBUS §5.2 (`knownTypes.json`), plus MIB symbol resolution.

## 5. MIBs

- `MibRepository` loads bundled MIBs then **site MIBs** (uploaded through diagnostics action
  `importMib` / UIF connection page, stored in the connection settings as text, ≤ 2 MB each,
  ≤ 200 modules).
- Parser scope (sufficient for vendor MIBs in practice): `DEFINITIONS ::= BEGIN … END`,
  `IMPORTS … FROM`, `OBJECT IDENTIFIER ::= { parent n }`, `OBJECT-TYPE` (SYNTAX incl.
  enumerated INTEGER, SEQUENCE OF / SEQUENCE entries, INDEX, AUGMENTS), `TEXTUAL-CONVENTION`,
  `MODULE-IDENTITY`, `OBJECT-IDENTITY`, `NOTIFICATION-TYPE`, `TRAP-TYPE` (SMIv1), `OBJECT-GROUP`
  and `MODULE-COMPLIANCE` (parsed and ignored), comments `--`, macros skipped. Unknown
  constructs → warnings, not failures.
- Import result: modules, problems (missing imports listed by module name — user uploads them
  too), counts. Resolution errors in profiles reference the missing module.

## 6. Behaviour

### 6.1 Settings (`settings/snmp.settings.json`)

| Field | Type | Default | Rules |
|---|---|---|---|
| `agents` | array (≤ 5,000) | required | |
| `agents[].id` | string `^[A-Za-z0-9._-]{1,64}$` | required | native id |
| `agents[].name` | string | id | |
| `agents[].address`, `port` | string, integer | required, 161 | |
| `agents[].version` | `v1`, `v2c`, `v3` | `v2c` | v1/v2c show a security warning |
| `agents[].credentials` | string (credential set id) | `default` | |
| `agents[].profiles` | array of profile ids | [`generic-mib2`] | |
| `agents[].interval` | duration PT10S–PT1H | PT60S | |
| `agents[].timeout` / `retries` | duration / integer 0–5 | PT2S / 1 | |
| `agents[].maxRepetitions` / `maxVarbinds` | integer | 20 / 30 | GETBULK and GET batching |
| `agents[].trapSources` | array of IP/CIDR | [address] | which source addresses map traps to this agent |
| `credentialSets` | array | `[{ id: "default", community secret }]` | `{ id, version, community? (secret ref), user?, authProtocol?, privProtocol?, contextName? }` with secrets `community:<id>`, `authKey:<id>`, `privKey:<id>` |
| `traps.enabled` | boolean | true | |
| `traps.port` | integer | 162 | shared via dispatcher |
| `traps.acceptUnknownSources` | boolean | false | unknown sources → counted and logged, events on the server device if true |
| `profiles` | array of site profiles | [] | |
| `mibs` | array of `{ name, text }` | [] | site MIBs |

Secrets are declared dynamically: the manifest declares secret field **patterns**
`community:*`, `authKey:*`, `privKey:*` (INT secrets schema gains `pattern` fields — added to
SCM manifest schema by T4.07).

### 6.2 Polling

- Per agent `PollingLoop` with spread offsets (as C-PING). Plan: scalars batched into GET
  requests of ≤ `maxVarbinds`; tables walked with GETBULK (v2c/v3) or GETNEXT (v1) limited to
  the profile's columns; walk results cached per cycle.
- Rows appearing/disappearing → inventory changes (debounced, as C-MQTT §6.1). Row identity =
  index; devices for vanished rows become orphaned through REG reconciliation (not deleted).
- Errors: `noSuchObject`/`noSuchInstance` → point unsupported (retried every 30 min);
  timeouts after retries → agent offline (devices unknown with reason `agentUnreachable`);
  authentication errors (v3 `usmStats*` reports) → agent fault `configError` with plain message.
- v3 engine discovery and time synchronisation per agent; engine boots/time cached in
  `IConnectionStore`.

### 6.3 Traps (`TrapDispatcher`, node-level)

- The first SNMP connection registers UDP `traps.port` through `RegisterUdpAsync` with
  `shared: true` key `snmp-traps`; IRT keeps one socket per (port, bind address) and fans out
  datagrams to every connection registered under the same share key (in-process). Isolated/
  remote hosts own their own socket (one per host).
- Each connection decodes datagrams whose source matches one of its agents' `trapSources`;
  v3 traps require a matching USM user (authoritative engine = sender); informs are
  acknowledged with a Response PDU after the sink accepted the event.
- Trap → profile `traps[]` mapping → event on the device (or on the agent server device when no
  mapping matches: generic event `snmp.trapReceived { oid, varbinds (≤ 20, values as strings) }`).
- Duplicate informs (retransmissions, same request id within 30 s) are acknowledged again but not
  re-reported.

### 6.4 Commands

Profile `commands` map capability commands to SET requests (typed: Integer32, OctetString,
IpAddress, Unsigned32, TimeTicks, Counter64 not allowed); the agent's response error status maps
to failures (`noAccess`/`notWritable` → `NotPermitted`; `wrongValue` → `InvalidArguments`);
read-back GET of the same OIDs immediately after.

## 7. Connection test

For the first 5 agents: v3 engine discovery, `sysDescr.0`, `sysObjectID.0`, `sysUpTime.0`,
then one walk step of each profile table; steps "Agent reachable", "Authentication (SHA-256 /
AES-128)", "Profile IF-MIB ports: 48 rows"; v1/v2c show "Community authentication is not
encrypted" warning; trap port binding test ("UDP 162 available" or "in use by another program").

## 8. Diagnostics

| Action | Arguments | Result |
|---|---|---|
| `get` | `{ agentId, oids[] }` | Values with types, MIB names, enum labels |
| `walk` | `{ agentId, rootOid, max = 1000 }` | Varbinds (helps writing profiles) |
| `set` | `{ agentId, oid, type, value }` | Audited, confirmation in UI |
| `importMib` | `{ text }` | Parse result and problems; adds to settings (draft) |
| `lookup` | `{ nameOrOid }` | MIB node details |
| `traps` | `{ last = 50 }` | Recent traps received (decoded, matched or not) |
| `stats` | — | Requests, timeouts, errors by agent, traps by source |

## 9. Observability

| EventId | Level | Message |
|---|---|---|
| 5400 | Information | SNMP connector started: {Agents} agents, {Profiles} profiles, traps on UDP {Port} |
| 5401 | Warning | Agent {AgentId} unreachable after {Retries} retries |
| 5402 | Warning | Agent {AgentId} authentication failed ({Report}) |
| 5410 | Information | MIB {Module} imported ({Objects} objects, {Problems} problems) |
| 5420 | Debug | Trap {Oid} from {Source} mapped to {Device} |
| 5421 | Warning | Trap from unknown source {Source} |
| 5430 | Warning | UDP port {Port} unavailable: {Error} |

## 10. Tests

| Id | Layer | Test |
|---|---|---|
| SNMP-U-001 | U | MIB lexer/parser: OBJECT-TYPE, TCs, enumerations, tables with INDEX/AUGMENTS, NOTIFICATION-TYPE, TRAP-TYPE, imports, comments |
| SNMP-U-002 | U | Bundled IETF MIBs parse without errors; OID ⇄ name resolution |
| SNMP-U-003 | U | Missing imports reported by module name |
| SNMP-U-004 | U | Profile validation (OIDs resolve, types/properties known, include filter syntax) |
| SNMP-U-005 | U | Value mapping: enums (MIB labels and explicit maps), scale/offset, units |
| SNMP-U-006 | U | Counter rates with wrap (32/64) and discontinuity on sysUpTime decrease |
| SNMP-U-007 | U | Poll planner: GET batching ≤ maxVarbinds, GETBULK walks limited to columns, v1 GETNEXT |
| SNMP-U-008 | U | Table rows → devices; include filter; row add/remove → inventory changes |
| SNMP-U-009 | U | noSuchObject/noSuchInstance handling and retry |
| SNMP-U-010 | U | Timeouts/retries → agent offline; recovery |
| SNMP-U-011 | U | v3: engine discovery, time window, auth/priv combinations (SHA-2, AES) with RFC test vectors for key localisation |
| SNMP-U-012 | U | v3 auth error reports → plain message |
| SNMP-U-013 | U | Credential sets and dynamic secret field patterns |
| SNMP-U-014 | U | Trap decoding v1/v2c/v3; source matching; unknown sources policy |
| SNMP-U-015 | U | Trap mapping to device events; generic trapReceived fallback |
| SNMP-U-016 | U | Inform acknowledgement after sink acceptance; duplicate informs |
| SNMP-U-017 | U | Shared UDP port fan-out between two connections (IRT dispatcher) |
| SNMP-U-018 | U | SET command mapping, error status mapping, read-back |
| SNMP-U-019 | U | TestAsync steps and warnings |
| SNMP-U-020 | U | Diagnostics get/walk/set/importMib/lookup/traps/stats; set audited |
| SNMP-U-021 | U | Settings validation errors with paths |
| SNMP-U-022 | U | Translations coverage; manifest validates (incl. secret patterns) |
| SNMP-I-001 | I | net-snmp `snmpd` container: v2c and v3 authPriv polling of IF-MIB ports and HOST-RESOURCES |
| SNMP-I-002 | I | `snmptrap` linkDown/linkUp → events on the right port devices |
| SNMP-I-003 | I | Informs v2c/v3 acknowledged |
| SNMP-I-004 | I | SET ifAdminStatus via command with verification (writable test agent) |
| SNMP-I-005 | I | Import a vendor-style MIB file with dependencies; profile using it works |
| SNMP-I-006 | I | Load: 1,000 agents × 50 OIDs at 60 s — no backlog (in-process test agents, benchmark) |
| SNMP-C | C | Conformance suite with `TestAgent` as `IVendorSimulator` |

## 11. Tasks

| Task | Sections | Tests |
|---|---|---|
| T4.07 | all (plus INT `RegisterUdpAsync` and manifest secret patterns) | SNMP-U-001…SNMP-U-022, SNMP-I-001…SNMP-I-006, conformance |
