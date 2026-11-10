# C-MQTT — MQTT Connector

| | |
|---|---|
| Spec id | C-MQTT |
| Projects | `src/integrations/Watchpoint.Integration.Mqtt` (package), tests `tests/integrations/Watchpoint.Integration.Mqtt.Tests` |
| Package id | `watchpoint.integration.mqtt` (builtin, licence class `standard`) |
| Milestone | M4 |
| Tasks | T4.06 |
| Event id range | 5300–5399 |
| Reference | OASIS MQTT 3.1.1 and MQTT 5.0 standards (public) |

## 1. Purpose and scope

Subscribe to an MQTT broker and turn messages into devices, properties and events using
**topic mappings**; publish messages for commands. Typical uses: IoT sensors (temperature,
leak, air quality, occupancy), building gateways, edge devices, custom systems publishing their
status.

Supported: MQTT 3.1.1 and 5.0 client, TCP and TLS (system trust, custom CA, pin), username/
password and client certificate authentication, QoS 0/1/2 subscriptions, retained messages,
persistent sessions, last-will based availability, JSON / plain / number / boolean payloads,
automatic device creation from topic wildcards.

Not in scope v1: Sparkplug B (planned as a separate mapping kind later), WebSockets transport,
acting as a broker.

## 2. Dependencies

Abstractions, SDK, **MQTTnet 4.x** (MIT) client. `System.Text.Json` with JSON Pointer /
restricted JSONPath evaluator implemented in the package (`$.a.b[0]` subset). Tests: MQTTnet
in-process server and an Eclipse Mosquitto container (EPL-2.0/EDL-1.0, test-only, not
distributed).

## 3. Layout

```
Watchpoint.Integration.Mqtt/
├── manifest.json, settings/mqtt.settings.json
├── types/mqtt.broker.json (server), mqtt.device.json (generic device for "custom" mappings)
├── i18n/, docs/
└── src/
    ├── MqttConnector.cs, MqttSession.cs (MQTTnet client wrapper, reconnect, subscriptions)
    ├── Mapping/TopicPattern.cs (+ captures), MappingRule.cs, PayloadDecoder.cs, JsonPathLite.cs, ValueMapper.cs
    ├── Inventory/DynamicInventory.cs (devices from captures)
    ├── Commands/PublishMapper.cs
    ├── MqttDiagnostics.cs, MqttLog.cs
```

## 4. Settings (`settings/mqtt.settings.json`)

| Field | Type | Default | Rules |
|---|---|---|---|
| `host`, `port` | string, integer | required, 1883 (8883 with TLS) | |
| `protocolVersion` | enum `3.1.1`, `5.0` | `5.0` | |
| `tls.enabled` | boolean | true | plain TCP requires `allowPlain: true` + warning in the wizard |
| `tls.mode` | `system`, `customCa`, `pin` | system | via `ISecureHttp`-equivalent TLS options helper (`SecureSockets.CreateSslOptions`) |
| `clientId` | string ≤ 64 | `watchpoint-<connectionId short>` | unique per broker; cluster failover reuses it (M9) |
| `cleanSession` | boolean | false | persistent session so QoS 1/2 messages queued at the broker during reconnects are not lost |
| `keepAlive` | duration | PT30S | |
| `auth.username` | string | null | password in secret `password` |
| `auth.clientCertificate` | boolean | false | certificate + key in secrets `clientCertificate`, `clientKey` |
| `availability` | `{ topic, onlinePayload, offlinePayload }` | null | for the server health (e.g. a gateway's LWT) |
| `mappings` | array (≤ 2,000) of mapping rules (§5) | required ≥ 1 | |
| `maxMessagesPerSecond` | integer | 5,000 | above → messages coalesced per topic (latest) and warning |
| `maxDevices` | integer ≤ 50,000 | 10,000 | dynamic device creation cap |

Secrets: `password` (password), `clientCertificate` (certificate), `clientKey` (privateKey).

## 5. Mapping rules

```json
{
  "id": "sensors-temperature",
  "topic": "site/{building}/{deviceId}/temperature",
  "qos": 1,
  "device": {
    "nativeId": "{deviceId}",
    "type": "generic.analog-input",
    "name": "{deviceId} temperature",
    "parent": null,
    "attributes": { "unit": "°C", "warningAbove": 30, "alarmAbove": 40 },
    "locationHint": "{building}"
  },
  "payload": { "format": "json", "path": "$.value", "timestampPath": "$.ts", "timestampFormat": "unixMs" },
  "property": "measurement.value",
  "scale": 1, "offset": 0
}
```

| Field | Rules |
|---|---|
| `topic` | MQTT filter with **named captures** `{name}` matching one level (`+`) and optional final `{name*}` matching `#`. Converted to a subscription filter (`site/+/+/temperature`); captures extracted on receive |
| `device.nativeId` | template over captures (required); devices are created dynamically on first message (§6.1) |
| `device.type` | standard or package type id; `mqtt.device` for custom mappings |
| `property` / `event` / `health` | exactly one target: a property path; an event path (each message = one event, `payload.eventFields` maps JSON paths → event payload fields); or `health` (`payload.map` value → connectivity) |
| `payload.format` | `json`, `text`, `number`, `boolean` (`true/false/1/0/on/off/open/closed` configurable by `payload.trueValues`/`falseValues`) |
| `payload.path` | JSON path subset: `$`, `.name`, `['name']`, `[index]` |
| `payload.map` | object raw → value (enum mapping, booleans to enums…) |
| `timestampPath` / `timestampFormat` | `iso8601`, `unixS`, `unixMs`; absent → receive time |
| `retainedAsInitial` | boolean, default true — retained messages give initial values on connect |
| `staleAfter` | duration — if no message for this long, the property quality becomes `Uncertain` (devices that publish periodically) |
| `deviceOfflineAfter` | duration — no message on any rule for the device → device offline |

Rules are validated (topic syntax, captures used in templates exist, target exclusivity,
JSON path syntax, type/property compatibility against `knownTypes.json` as in C-MODBUS §5.2).
Several rules may target the same device (different properties).

### 5.1 Commands

```json
{ "id": "relay-commands", "device": { "nativeId": "{deviceId}" , "match": "relays/{deviceId}/state" },
  "commands": {
    "switchable.switchOn":  { "topic": "relays/{deviceId}/set", "payload": "{\"on\":true}", "qos": 1, "retain": false },
    "switchable.switchOff": { "topic": "relays/{deviceId}/set", "payload": "{\"on\":false}", "qos": 1 },
    "pulse.pulse":          { "topic": "relays/{deviceId}/pulse", "payload": "{\"ms\":{args.duration.ms}}" }
  } }
```

Command mapping placeholders: captures of the device (stored as device attributes `mqtt.captures`
at creation), `{args.<name>}` with formatters (`.ms`, `.s`, JSON-escaped strings). The command
completes `Succeeded` when the broker acknowledges the publish (QoS 1/2) or immediately (QoS 0);
effect verification is CMD's job through the state property (expectedEffect). MQTT 5:
optional request/response (`responseTopic` + correlation data) — `"awaitResponse": { "path":
"$.ok", "timeout": "PT5S" }` maps the reply to success/failure.

## 6. Behaviour

### 6.1 Dynamic inventory

- Static part: server `mqtt.broker` (native id `broker`).
- Devices appear when a message matches a rule and its `nativeId` is new: the connector adds it
  to its in-memory inventory (persisted in `IConnectionStore` key `inventory`, ≤ 1 MB → beyond
  that, compressed list of native ids + rule ids; attributes rebuilt from rules) and calls
  `ReportInventoryChanged` (debounced 2 s, batched). Import follows the connection's auto-import
  setting (REG/IRT): with auto-import off, new devices show as *discovered* in the UI.
- Messages for not-yet-imported devices are dropped by the sink (S3) — after import, retained
  messages provide initial values on the next reconnect or the "refresh" command
  (re-subscribe to the rule's topic to receive retained values again).
- `locationHint` is passed as a REG location suggestion attribute (`suggestedLocation`) used by
  the discovery review UI (UIF §7.2) to pre-fill locations by name.
- `maxDevices` reached → new devices ignored, warning 5330 and connection degraded.

### 6.2 Receiving

- MQTTnet `ApplicationMessageReceivedAsync` → match rule(s) (topic patterns compiled into a
  trie by level for O(levels) matching with 2,000 rules) → decode → report. Decoding errors →
  counted per rule, `lastError` diagnostic, debug log rate-limited.
- Rate limit: above `maxMessagesPerSecond`, messages coalesced per topic in a 1 s window
  (latest wins) for property rules; event rules are never coalesced (beyond 3× the limit, events
  are dropped and counted, connection degraded with reason `flooding`).
- QoS 1/2: the connector acknowledges messages **after** the sink has accepted them (MQTTnet
  manual acknowledgement), so a node crash before the event log write leads to redelivery by the
  broker (persistent session) — at-least-once; PIP deduplicates nothing, so duplicates of events
  are possible and documented (event payload carries `mqttPacketId` when available for
  troubleshooting).

### 6.3 Health

- Connection status from MQTTnet connect/disconnect; reconnect with `ConnectorBase` backoff.
- `availability` topic maps to server health; when offline, all devices `unknown` with reason
  `gatewayOffline`.
- `staleAfter` / `deviceOfflineAfter` timers evaluated every 5 s.

## 7. Connection test

Connect with the given settings (separate client id suffix `-test`, clean session), subscribe
to every rule filter for 5 s, report: "Broker connection (TLS 1.3)", "Authentication",
"Subscriptions granted (QoS)", "Messages received: 42 on 3 rules; unmatched topics: …".
Authorisation failures (MQTT 5 reason codes / 3.1.1 return codes) translated.

## 8. Diagnostics

| Action | Arguments | Result |
|---|---|---|
| `sniff` | `{ filter = "#", duration = PT10S, max = 200 }` | Topics and payload samples (truncated 1 KB) — helps writing mappings; requires `config.integrations.operate` |
| `testMapping` | `{ rule (JSON), topic, payload }` | Decoded result without publishing anything |
| `publish` | `{ topic, payload, qos, retain }` | Publish a message (audited) |
| `stats` | — | Messages per rule, decode errors, coalesced, dropped, devices created |

## 9. Observability

| EventId | Level | Message |
|---|---|---|
| 5300 | Information | Connected to MQTT broker {Host}:{Port} (MQTT {Version}, session present {SessionPresent}) |
| 5301 | Warning | MQTT disconnected: {Reason} |
| 5310 | Debug | Rule {RuleId} decode error on {Topic}: {Error} |
| 5320 | Information | {Count} new devices discovered |
| 5330 | Warning | Device limit {Max} reached; new devices ignored |
| 5340 | Warning | Message rate {Rate}/s above limit; coalescing |

## 10. Tests

| Id | Layer | Test |
|---|---|---|
| MQTT-U-001 | U | Topic pattern parsing, captures, conversion to filters; invalid patterns |
| MQTT-U-002 | U | Trie matching with overlapping rules (multiple rules match one topic) |
| MQTT-U-003 | U | Payload decoders: json/text/number/boolean with true/false values |
| MQTT-U-004 | U | JSON path subset incl. arrays and quoted names; errors |
| MQTT-U-005 | U | Value mapping (map, scale/offset), timestamps formats |
| MQTT-U-006 | U | Event rules with eventFields; health rules |
| MQTT-U-007 | U | Rule validation (captures in templates, exclusivity, type/property compatibility) |
| MQTT-U-008 | U | Dynamic inventory: creation, debounce, persistence in store, rebuild after restart |
| MQTT-U-009 | U | maxDevices cap |
| MQTT-U-010 | U | locationHint → suggestedLocation attribute |
| MQTT-U-011 | U | Retained messages as initial values; refresh re-subscribes |
| MQTT-U-012 | U | staleAfter → Uncertain; deviceOfflineAfter → offline |
| MQTT-U-013 | U | Availability topic → server health and devices unknown |
| MQTT-U-014 | U | Rate limiting: property coalescing, event dropping beyond 3×, degraded reason |
| MQTT-U-015 | U | Manual acknowledgement after sink acceptance (QoS 1/2) |
| MQTT-U-016 | U | Command mapping: placeholders, formatters, QoS ack completes command |
| MQTT-U-017 | U | MQTT 5 request/response with correlation and timeout |
| MQTT-U-018 | U | TLS modes and client certificate from secrets; plain TCP requires allowPlain |
| MQTT-U-019 | U | TestAsync steps and translated reason codes (3.1.1 and 5.0) |
| MQTT-U-020 | U | Diagnostics sniff/testMapping/publish/stats; publish audited |
| MQTT-U-021 | U | Settings validation errors with paths |
| MQTT-U-022 | U | Translations coverage; manifest validates |
| MQTT-I-001 | I | Mosquitto container (TLS + username/password): 500 sensors publishing every 5 s → devices discovered, values live |
| MQTT-I-002 | I | Broker restart: persistent session delivers QoS 1 messages published meanwhile |
| MQTT-I-003 | I | Node crash before sink flush (kill test host) → message redelivered (at-least-once) |
| MQTT-I-004 | I | Commands to a simulated relay (test publisher replying state) with CMD verification (deferred to T1.17b as CMD verification is not yet implemented) |
| MQTT-I-005 | I | MQTT 3.1.1 broker mode compatibility |
| MQTT-I-006 | I | Load: 10,000 messages/s across 2,000 topics — latency to state p99 < 200 ms (benchmark) |
| MQTT-C | C | Conformance suite with MQTTnet in-process server as `IVendorSimulator` |

## 11. Tasks

| Task | Sections | Tests |
|---|---|---|
| T4.06 | all | MQTT-U-001…MQTT-U-022, MQTT-I-001, MQTT-I-002, MQTT-I-003, MQTT-I-005, MQTT-I-006, conformance |
| T1.17b | §5.1, §10 | MQTT-I-004 (alongside SIM-I-001, SIM-I-004, SIM-I-005) |
