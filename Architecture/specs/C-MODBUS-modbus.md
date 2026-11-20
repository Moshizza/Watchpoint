# C-MODBUS — Modbus TCP Connector

| | |
|---|---|
| Spec id | C-MODBUS |
| Projects | `src/integrations/Watchpoint.Integration.Modbus` (package), tests `tests/integrations/Watchpoint.Integration.Modbus.Tests` |
| Package id | `watchpoint.integration.modbus` (builtin, licence class `standard`) |
| Milestone | M4 |
| Tasks | T4.05 |
| Event id range | 5200–5299 |
| Reference | Modbus Application Protocol Specification v1.1b3 and Modbus Messaging on TCP/IP Implementation Guide v1.0b (modbus.org, public) |

## 1. Purpose and scope

Supervise and control equipment speaking **Modbus TCP** (PLCs, I/O modules, energy meters,
UPS, HVAC controllers, generator panels, gateways to Modbus RTU) with **mapping templates**
that turn registers and coils into standard device types (binary inputs/outputs, analog
inputs) or package-defined types.

Supported: Modbus TCP client (and RTU-over-TCP framing for serial gateways), function codes 1,
2, 3, 4, 5, 6, 15, 16, exception responses, multiple unit ids per connection (gateways),
block-read optimisation, writes for commands with read-back verification.

Not supported in v1: Modbus RTU over a local serial port (later through a remote host with a
serial port — same code with a `SerialTransport`), function codes 20/21/23/43, Modbus server
(slave) mode.

## 2. Dependencies

Abstractions, SDK (`ConnectorBase`, `PollingLoop`, `TcpRequestClient`, `FramedStream`,
`ResiliencePresets`). **No third-party Modbus library**: the client is implemented in the
package (≈ 600 lines; full control of timeouts, transaction ids and pipelining, no licence
question). Tests use the package's own `ModbusTestServer` (in the test project).

## 3. Layout

```
Watchpoint.Integration.Modbus/
├── manifest.json, settings/modbus.settings.json
├── types/modbus.server.json (connection root), modbus.unit.json (one per unit id, optional grouping)
├── templates/                     built-in mapping templates (§5): generic-io-8di-8do.json, generic-analog-4ai.json, energy-meter-basic.json
├── schemas/template.schema.json   mapping template schema (validated by wptypes plugin)
├── rules/, i18n/, docs/
└── src/
    ├── Protocol/ModbusPdu.cs, MbapFrame.cs, RtuOverTcpFrame.cs (CRC-16), FunctionCodes.cs, ModbusException.cs
    ├── Transport/ModbusTcpClient.cs (transaction ids, pipelining up to maxInFlight), ITransport.cs
    ├── Mapping/MappingTemplate.cs, PointDefinition.cs, DataTypes.cs (decoders/encoders), TemplateLoader.cs
    ├── Polling/ReadPlanner.cs (block merge), PollScheduler.cs
    ├── Commands/CommandMapper.cs
    ├── ModbusConnector.cs, ModbusDiagnostics.cs, ModbusLog.cs
```

## 4. Protocol implementation

- **MBAP** header: transaction id (uint16, incremented, wraps), protocol id 0, length, unit id.
  Responses matched by transaction id; unmatched or late responses dropped and counted.
- **Pipelining**: up to `maxInFlight` requests outstanding (default 1 — many devices do not
  support more; gateways often support 4–8).
- **RTU over TCP** (`framing: rtuOverTcp`): ADU = unit id + PDU + CRC-16 (poly 0xA001); no
  transaction ids → strictly one request at a time; inter-frame idle time `rtuFrameGap`.
- Limits enforced per request: read registers ≤ 125, read coils/discrete ≤ 2,000, write
  registers ≤ 123, write coils ≤ 1,968 (spec limits), configurable lower (`maxRegistersPerRead`).
- Exception responses mapped: 01 illegal function → point marked unsupported (quality Bad,
  logged once); 02 illegal address → same; 03 illegal value → command failure `InvalidArguments`;
  04 device failure → device fault `hardwareFault`; 05/07 acknowledge/busy → retry after
  `busyRetryDelay` (max 3); 0A/0B gateway path unavailable / target failed to respond → device
  offline (unit), connection stays up.
- Timeouts: `requestTimeout` per request; 3 consecutive timeouts on a unit → that unit's devices
  offline (others unaffected); TCP errors → reconnect loop (`ConnectorBase` backoff), all devices
  `unknown`/offline with reason.
- Addresses in settings are **zero-based protocol addresses**; templates may use the
  traditional 1-based "40001" notation through `addressStyle: modicon` (converted:
  40001 → holding 0, 30001 → input 0, 10001 → discrete 0, 00001 → coil 0).

## 5. Mapping templates

```json
{
  "$schema": "https://schemas.watchpoint.dev/modbus-template/1.json",
  "id": "generic-io-8di-8do",
  "name": { "en": "Generic I/O module 8 DI / 8 DO", "fr": "…", "ar": "…" },
  "addressStyle": "zeroBased",
  "devices": [
    { "key": "di{n}", "repeat": { "n": [1, 8] }, "type": "generic.binary-input",
      "name": { "en": "Input {n}" },
      "points": [ { "property": "binaryValue.value", "table": "discreteInput", "address": "{n-1}", "dataType": "bool" } ] },
    { "key": "do{n}", "repeat": { "n": [1, 8] }, "type": "generic.binary-output",
      "points": [ { "property": "binaryValue.value", "table": "coil", "address": "{n-1}", "dataType": "bool" } ],
      "commands": {
        "switchable.switchOn":  { "write": [ { "table": "coil", "address": "{n-1}", "value": true } ], "verify": "binaryValue.value == true" },
        "switchable.switchOff": { "write": [ { "table": "coil", "address": "{n-1}", "value": false } ] },
        "switchable.toggle":    { "write": [ { "table": "coil", "address": "{n-1}", "value": "!binaryValue.value" } ] },
        "pulse.pulse":          { "write": [ { "table": "coil", "address": "{n-1}", "value": true } ], "thenAfter": "{args.duration}", "thenWrite": [ { "table": "coil", "address": "{n-1}", "value": false } ] }
      } }
  ],
  "health": { "commFailWhen": "timeouts", "faults": [ { "fault": "powerFail", "table": "discreteInput", "address": 100, "dataType": "bool", "activeWhen": true } ] }
}
```

### 5.1 Point fields

| Field | Values |
|---|---|
| `table` | `coil`, `discreteInput`, `holding`, `input` |
| `address` | integer or `{expr}` (only `n`, arithmetic) |
| `dataType` | `bool` (coil/discrete, or `bit` of a register with `bit: 0–15`), `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`, `float32`, `float64`, `string` (`length` registers, ASCII/UTF-8, trimmed NULs), `bcd16`, `bcd32` |
| `wordOrder` / `byteOrder` | `big` (default) / `little` — e.g. "CDAB" float = wordOrder little, byteOrder big |
| `scale`, `offset` | value = raw × scale + offset (decimal arithmetic) |
| `enum` | map raw integer → enum value of the property (e.g. 0 → "normal", 1 → "active") |
| `unit` | unit for measurement properties (converted by MOD) |
| `interval` | poll interval override for this point (e.g. energy counters every 5 min) |
| `deadband` | minimum change to report (absolute) for numeric values |
| `property` | target property path of the device type; or `attribute:<name>` to read a static attribute once at start (serial number, firmware) |

### 5.2 Templates sources

- Built-in templates (package folder), **site templates** (JSON documents in the connection
  settings `templates[]`, inline — edited in UIF with a JSON editor and validated against
  `template.schema.json`), and a library import (upload `.json`).
- Template validation (in `InitializeAsync` and settings validation): device types exist — the
  connector has no MOD access, so it validates against the types declared in its manifest plus
  a `knownTypes.json` resource generated at build from the standard library (type ids, property
  paths and data types); REG validates again at import — property paths exist in the type with compatible data types,
  write values compatible, address ranges within table limits, no duplicate device keys after
  repeat expansion.

## 6. Settings (`settings/modbus.settings.json`)

| Field | Type | Default | Rules |
|---|---|---|---|
| `host` | string | required | host name or IP |
| `port` | integer | 502 | |
| `framing` | enum `tcp`, `rtuOverTcp` | `tcp` | |
| `requestTimeout` | duration PT0.1S–PT30S | PT1S | |
| `maxInFlight` | integer 1–16 | 1 | forced 1 for rtuOverTcp |
| `interRequestDelay` | duration PT0S–PT1S | PT0S | some devices need pauses |
| `rtuFrameGap` | duration | PT0.005S | rtuOverTcp |
| `defaultInterval` | duration PT0.2S–PT1H | PT1S | |
| `maxRegistersPerRead` / `maxCoilsPerRead` | integer | 125 / 2000 | ≤ spec limits |
| `maxGap` | integer 0–50 | 10 | addresses gap merged into one block read |
| `busyRetryDelay` | duration | PT0.2S | |
| `templates` | array of template documents | [] | site templates |
| `units` | array (≤ 247) | required ≥ 1 | |
| `units[].unitId` | integer 0–255 | 1 | 255/0 common for direct TCP devices |
| `units[].id` | string `^[A-Za-z0-9._-]{1,64}$` | required | native id of the unit server |
| `units[].name` | string | id | |
| `units[].instances` | array | required | |
| `units[].instances[].template` | string (template id) | required | |
| `units[].instances[].prefix` | string | "" | prefix of generated device native ids (`<prefix><key>`) |
| `units[].instances[].addressOffset` | integer | 0 | added to every address of the template |
| `units[].instances[].names` | object key → LocalizedName | {} | rename generated devices |
| `units[].instances[].exclude` | array of keys | [] | skip devices |

No secrets (Modbus has no authentication). The documentation insists on network segregation
(warning shown in the wizard).

## 7. Behaviour

### 7.1 Inventory

Server `modbus.server` (native id `gateway`, the TCP endpoint) → one `modbus.unit` server per
unit (native id = units[].id) → devices from template instances (native id
`<unitId>/<prefix><key>`). Attributes from `attribute:` points read at first connect.

### 7.2 Read planning (`ReadPlanner`, pure)

For each (unit, table, interval): sort point addresses, merge into blocks when the gap ≤
`maxGap` and the block size ≤ max per read; points spanning several registers (32/64-bit,
strings) are never split across blocks. Plans are rebuilt when settings or the device map
change. Unsupported points (exception 01/02) are removed from plans and retried every 10 min.

### 7.3 Polling and reporting

- `PollingLoop` per (unit, interval); blocks of a unit executed sequentially (respecting
  `interRequestDelay` and `maxInFlight`), units in parallel (gate size = number of units ×
  maxInFlight, bounded at 32).
- Decoded values reported when changed beyond deadband (or on first read, after reconnect,
  every 10 min as keep-alive refresh with the same value to update `lastContact`); quality Good;
  on read failure of a block: its points' quality `Uncertain` for 1 failure, `Bad` after 3
  (values kept), device health from unit status.
- Enum mapping failures (raw value not in map) → property quality Bad with `lastError`
  diagnostic, warning logged once per point per hour.

### 7.4 Commands (`CommandMapper`)

- Template `commands` map capability commands to writes: FC5 (single coil), FC6 (single
  register), FC15/16 (multiple) chosen automatically by table and value width; value
  expressions `true`, numbers, `{args.<name>}` (scaled inversely: raw = (value − offset) /
  scale, range-checked → `InvalidArguments`), `!<property>` (negation of current value).
- `thenAfter` + `thenWrite`: second write after a delay (pulse); the command completes after the
  second write. If the connection drops in between, the command result is `Failed` with
  `ConnectionDown`, the second write is retried on reconnect within `thenAfter + 30 s`, and the
  device reports event `pulseIncomplete` (warning) if it could not be written in that time.
- Read-back: after writes, the affected block is read immediately (priority Normal), so CMD's
  `expectedEffect` verification completes in one round-trip.
- Commands use `IRequestGate` priority from CMD (High/Critical commands jump ahead of polling).

## 8. Connection test

TCP connect, then one read per unit of the first point of the first instance (or FC3 address 0
length 1 if no point): steps "TCP connection", "Unit 1 responds (3 ms)", exceptions reported
with plain-language messages (`modbus.exception.illegal_address` "The device does not have
address 40001 — check the address style (zero-based vs 4xxxx)").

## 9. Diagnostics

| Action | Arguments | Result |
|---|---|---|
| `read` | `{ unitId, table, address, count, dataType?, wordOrder?, byteOrder? }` | Raw registers (hex) and decoded values in all common interpretations (helps find the right data type) |
| `write` | `{ unitId, table, address, value, dataType }` | Requires `config.integrations.operate` + confirmation in UI; audited |
| `scanUnits` | `{ from = 1, to = 247 }` | Unit ids responding to FC3/FC1 at address 0 (≤ 60 s) |
| `plan` | — | Current read plan: blocks per unit/interval, request count per second |
| `stats` | — | Requests, timeouts, exceptions by code, average latency per unit |

## 10. Observability

| EventId | Level | Message |
|---|---|---|
| 5200 | Information | Modbus connection to {Host}:{Port} ({Framing}) established |
| 5201 | Warning | Modbus connection lost: {Reason} |
| 5210 | Warning | Unit {UnitId} exception {Code} for {Table} {Address} (point disabled for 10 min) |
| 5211 | Warning | Unit {UnitId} not responding ({Timeouts} timeouts) |
| 5220 | Information | Read plan: {Blocks} blocks, {RequestsPerSecond} requests/s |
| 5230 | Warning | Pulse second write pending for {Device} |

## 11. Tests

| Id | Layer | Test |
|---|---|---|
| MODB-U-001 | U | MBAP encode/decode, transaction id wrap |
| MODB-U-002 | U | PDU encode/decode for FC 1,2,3,4,5,6,15,16 (spec examples as golden vectors) |
| MODB-U-003 | U | Exception responses mapping (01,02,03,04,05,06,0A,0B) |
| MODB-U-004 | U | RTU-over-TCP framing and CRC-16 (spec example CRC values) |
| MODB-U-005 | U | Pipelining: responses out of order matched; late responses dropped |
| MODB-U-006 | U | Request size limits enforced |
| MODB-U-007 | U | Data types decode/encode: int16…uint64, float32/64 with all word/byte orders, bit, string, BCD |
| MODB-U-008 | U | Scale/offset decimal arithmetic and inverse for writes with range checks |
| MODB-U-009 | U | Enum mapping and unmapped values |
| MODB-U-010 | U | Deadband and keep-alive reporting |
| MODB-U-011 | U | Modicon address style conversion |
| MODB-U-012 | U | Template schema validation; repeat expansion; duplicate keys |
| MODB-U-013 | U | Template validation against type property paths and data types |
| MODB-U-014 | U | Instances: prefix, offset, names, exclude |
| MODB-U-015 | U | Inventory structure: gateway → units → devices; native ids |
| MODB-U-016 | U | Attribute points read once at connect |
| MODB-U-017 | U | Read planner: block merge with gaps, multi-register points never split, size limits |
| MODB-U-018 | U | Planner rebuild on settings/device map change; unsupported points removed and retried |
| MODB-U-019 | U | Quality on block failure (Uncertain then Bad); unit offline after 3 timeouts |
| MODB-U-020 | U | Busy/acknowledge retry |
| MODB-U-021 | U | Command mapping: FC selection, value expressions, toggle negation |
| MODB-U-022 | U | Pulse with second write; retry on reconnect; pulseIncomplete event |
| MODB-U-023 | U | Read-back after write |
| MODB-U-024 | U | Command priority over polling through the gate |
| MODB-U-025 | U | TestAsync steps and plain-language exception messages |
| MODB-U-026 | U | Diagnostics read interpretations; write audited; scanUnits; plan; stats |
| MODB-U-027 | U | Inter-request delay respected |
| MODB-U-028 | U | Built-in templates validate |
| MODB-U-029 | U | Settings validation errors with paths |
| MODB-U-030 | U | Translations coverage; manifest validates |
| MODB-I-001 | I | Against `ModbusTestServer` over real TCP: 3 units, 200 points, polling and changes reported |
| MODB-I-002 | I | Commands with verification end-to-end (CMD + STA) |
| MODB-I-003 | I | Server restart / TCP reset: reconnect, qualities, recovery |
| MODB-I-004 | I | RTU-over-TCP mode against the test server in RTU framing |
| MODB-I-005 | I | Interoperability smoke test against an independent implementation (`pymodbus` server container, BSD licence, test-only) |
| MODB-I-006 | I | Load: 50 units × 100 registers at 1 s with maxInFlight 4 — no backlog (benchmark) |
| MODB-C | C | Conformance suite with `ModbusTestServer` as `IVendorSimulator` |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T4.05 | all | MODB-U-001…MODB-U-030, MODB-I-004, MODB-I-005, conformance (MODB-I-001, -003, -006 redone in T4.05c; MODB-I-002 moved to T1.17b) |
| T4.05c | §11 | MODB-I-001, MODB-I-003, MODB-I-006 (to spec; the T4.05 versions test other things) |
| T1.17b | §11 | MODB-I-002 (end-to-end through the integration runtime) |
