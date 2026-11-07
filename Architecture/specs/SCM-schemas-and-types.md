# SCM — JSON Schemas and Standard Type Library

| | |
|---|---|
| Spec id | SCM |
| Projects / folders | `schemas/`, `types/`, `src/service/Watchpoint.Model.Documents`, `tools/wptypes` |
| Milestone | M0 |
| Tasks | T0.06 (§3–§5), T0.07 (§6–§8) |
| Design docs | [Servers and devices](../../docs/modules/servers-and-devices.md), [Integrations §3](../../docs/modules/integrations.md#3-the-manifest) |

## 1. Purpose and scope

Define the **document formats** that describe capabilities, device/server types and
integration packages, the **standard type library v0** used by the first milestones, and a
validator used by the build, by `wptypes` and by the runtime (MOD, IRT).

Not in scope: loading types at runtime (MOD), loading packages (IRT), alarm rule templates
(format defined in ALM; type documents only reference them by id).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, JsonSchema.Net 8.x (MIT; pinned — 9.0+ uses the OSMF EULA, do not upgrade) | MOD, IRT, connectors (types they ship), UI (generated TypeScript types for documents), `wppack` |

## 3. Common rules for all documents

- JSON, UTF-8, no comments, validated against JSON Schema **draft 2020-12** files in
  `schemas/`. Unknown properties are rejected (`additionalProperties: false`) except under
  keys starting with `x-` (extensions, ignored by the runtime).
- Each document has `"$schema"` set to the schema `$id` (e.g.
  `https://schemas.watchpoint.dev/capability/1.json`). The major number in the URL is the
  **format version**; the runtime supports the current and previous major.
- Identifiers follow Kernel patterns ([K §4.2](K-kernel.md#42-string-based-ids)).
- Content versions (`"version"`) are SemVer `MAJOR.MINOR.PATCH`. References to other
  documents use caret ranges (`"^1.2"` = ≥1.2.0 <2.0.0).
- **No labels in documents.** All user-visible texts come from translation bundles with
  **derived keys** (§3.2), so translation is complete and checkable.
- Durations are ISO 8601 (`PT10S`); units are UCUM codes (`Cel`, `%`, `s`, `kW.h`, `m`, `V`).

### 3.1 Semantic severities

Used by enum values, events and state summaries:

`normal`, `normalActive`, `info`, `warning`, `alarm`, `fault`, `offline`, `unknown`.

Overall device states (computed by STA): `alarm`, `fault`, `warning`, `normalActive`,
`normal`, `maintenance`, `disabled`, `offline`, `unknown`. Mapping severity → state is 1:1 for
the common names; `info` maps to `normal`.

### 3.2 Derived translation keys

| Element | Key |
|---|---|
| Capability name / description | `capabilities.<capId>.name`, `capabilities.<capId>.description` |
| Property name | `capabilities.<capId>.properties.<propId>.name` |
| Enum value label | `capabilities.<capId>.properties.<propId>.values.<code>` |
| Command name / confirmation text | `capabilities.<capId>.commands.<cmdId>.name`, `….confirm` |
| Command parameter | `capabilities.<capId>.commands.<cmdId>.parameters.<paramId>.name` |
| Event name / title template | `capabilities.<capId>.events.<evtId>.name`, `….title` (ICU, may use payload fields as arguments) |
| Type name / description | `types.<typeId>.name`, `types.<typeId>.description` |
| Type attribute | `types.<typeId>.attributes.<attrId>.name` |
| Package display name | `packages.<packageId>.name` |
| Connector settings field | `packages.<packageId>.connectors.<serverType>.settings.<fieldPath>.name` / `.help` |

Bundles: `types/i18n/<culture>.json` for the standard library; `i18n/<culture>.json` in each
package. Nested JSON objects (i18next format): key segments are object levels.

## 4. Capability document (`schemas/capability.schema.json`)

```json
{
  "$schema": "https://schemas.watchpoint.dev/capability/1.json",
  "id": "doorPosition",
  "version": "1.0.0",
  "properties": [
    {
      "id": "position",
      "valueType": "enum",
      "enumValues": [
        { "code": "closed",   "severity": "normal" },
        { "code": "open",     "severity": "normalActive" },
        { "code": "heldOpen", "severity": "warning" },
        { "code": "forced",   "severity": "alarm" },
        { "code": "unknown",  "severity": "unknown" }
      ],
      "writable": false,
      "history": { "mode": "everyChange" },
      "staleAfter": "PT10M"
    }
  ],
  "commands": [],
  "events": [
    { "id": "forced",   "severity": "alarm",   "payload": { "type": "object", "properties": {} } },
    { "id": "heldOpen", "severity": "warning", "payload": { "type": "object", "properties": { "openFor": { "type": "duration" } } } }
  ]
}
```

### 4.1 Property fields

| Field | Type | Rules |
|---|---|---|
| `id` | MemberId | Unique in the capability |
| `valueType` | enum | `boolean`, `integer`, `decimal`, `enum`, `string`, `timestamp`, `duration`, `geoPoint`, `object`, `list` |
| `enumValues` | array | Required iff `enum`; ≥ 1 item; `code` MemberId-like `^[a-z][a-zA-Z0-9]*$`, unique; `severity` from §3.1 |
| `unit` | UCUM string | Allowed for `integer`/`decimal` only |
| `range` | `{min?, max?}` | Numbers only; `min ≤ max` |
| `decimals` | int 0–6 | `decimal` only; display precision |
| `maxLength` | int | `string` only, default 1,000 |
| `itemType` | valueType (not list/object) | Required iff `list` |
| `objectSchema` | parameter schema (§4.4) | Required iff `object` |
| `writable` | bool | Default false; `true` implies a command `set<PropId>` exists (validated) |
| `history` | object | `mode`: `everyChange` (default), `deadband` (`absolute` or `percent`, numbers only), `periodic` (`interval`), `none` |
| `staleAfter` | duration | Optional; value becomes `stale` if not refreshed within it |
| `default` | value | Optional initial value before first observation (else `unknown`) |
| `booleanSeverity` | `{ "true": severity, "false": severity }` | `boolean` only; default both `normal` |

### 4.2 Command fields

| Field | Type | Rules |
|---|---|---|
| `id` | MemberId | Unique in the capability |
| `parameters` | parameter schema (§4.4) | Default `{ "type": "object", "properties": {} }` |
| `risk` | enum | `normal` (default), `sensitive`, `critical` |
| `timeout` | duration | Default `PT30S`; max `PT10M` |
| `idempotent` | bool | Default false |
| `batchable` | bool | Default false |
| `riskByArgument` | object | Optional: `{ "<param>": { "<enumValue>": risk } }` raising the risk for given argument values; the highest applicable risk wins |
| `expectedEffect` | object | Optional: `{ "property": "<capId>.<propId>", "equals": <value> | "in": [<values>], "within": duration }`. The property may belong to another capability; the cross-document validator checks that **every type using this capability** also has that property (`scm.command.effect_property_unknown` otherwise) |

### 4.3 Event fields

| Field | Type | Rules |
|---|---|---|
| `id` | MemberId | Unique in the capability |
| `severity` | §3.1 | Default `info` |
| `payload` | parameter schema (§4.4) | Field types allowed incl. `duration`, `timestamp`, `entityRef` |
| `personalData` | array of payload field names | Fields carrying personal data |

### 4.4 Parameter schema (restricted JSON Schema subset)

Used for command parameters, event payloads, object properties, device attributes and
connector settings. Restricted so forms can always be generated.

```json
{
  "type": "object",
  "properties": {
    "mode":     { "type": "string", "enum": ["full", "partial"], "default": "full" },
    "duration": { "type": "duration", "minimum": "PT1S", "maximum": "PT1H", "default": "PT5S" },
    "level":    { "type": "integer", "minimum": 0, "maximum": 100 },
    "host":     { "type": "string", "format": "hostname", "maxLength": 253 },
    "port":     { "type": "integer", "minimum": 1, "maximum": 65535, "default": 443 },
    "useTls":   { "type": "boolean", "default": true },
    "camera":   { "type": "entityRef", "entity": "device", "deviceCategory": "video" },
    "tags":     { "type": "array", "items": { "type": "string" }, "maxItems": 20 }
  },
  "required": ["mode"],
  "x-wp-ui": {
    "groups": [{ "id": "connection", "fields": ["host", "port", "useTls"] }],
    "advanced": ["level"],
    "visibleWhen": { "port": { "field": "useTls", "equals": true } }
  }
}
```

Allowed `type` values: `string`, `integer`, `number`, `boolean`, `duration`, `timestamp`,
`entityRef`, `array` (items of a scalar type), `object` (one nesting level only).
Allowed keywords: `enum`, `default`, `minimum`, `maximum`, `minLength`, `maxLength`,
`pattern` (ECMAScript subset compatible with .NET), `format` (`hostname`, `ipv4`, `ipv6`,
`uri`, `email`), `items`, `maxItems`, `minItems`, `required`, `properties`.
`entityRef` accepts `entity` (`device`, `server`, `location`, `user`, `userGroup`,
`calendar`) and filters `deviceCategory`, `deviceType`, `capability`.
Secrets are **never** in parameter schemas; they are declared separately (§5.3).

## 5. Device / server type document (`schemas/device-type.schema.json`)

```json
{
  "$schema": "https://schemas.watchpoint.dev/device-type/1.json",
  "id": "ac.door",
  "version": "1.0.0",
  "role": "device",
  "category": "accessControl",
  "extends": null,
  "capabilities": [
    { "id": "health", "version": "^1.0" },
    { "id": "doorPosition", "version": "^1.0" },
    { "id": "lockable", "version": "^1.0" },
    { "id": "openable", "version": "^1.0" },
    { "id": "doorMode", "version": "^1.0", "exclude": { "commands": [] , "enumValues": { "mode": ["emergencyClose"] } } },
    { "id": "accessEvents", "version": "^1.0" },
    { "id": "tamper", "version": "^1.0", "optional": true }
  ],
  "attributes": {
    "type": "object",
    "properties": {
      "heldOpenAfter": { "type": "duration", "default": "PT60S", "minimum": "PT5S", "maximum": "PT1H" }
    }
  },
  "allowedChildTypes": ["ac.reader", "ac.lock", "generic.binary-input"],
  "licenceClass": "full",
  "display": {
    "icon": "door",
    "symbol": "ac.door",
    "primaryProperty": "doorPosition.position"
  },
  "stateSummary": {
    "overrides": [
      { "property": "lockable.lockState", "values": ["unlocked"], "state": "normalActive" }
    ]
  },
  "defaultAlarmRules": ["std.ac.door.forced", "std.ac.door.heldOpen", "std.health.offline"],
  "defaultLinks": [{ "kind": "cameraViews", "peerCategory": "video" }]
}
```

### 5.1 Fields

| Field | Rules |
|---|---|
| `role` | `device` or `server` |
| `category` | `accessControl`, `video`, `intrusion`, `fire`, `intercomAudio`, `perimeter`, `buildingManagement`, `smartCity`, `itInfrastructure`, `generic`, `system` |
| `extends` | `null` or `{ "id": TypeId, "version": "^x.y" }`. The child inherits capabilities, attributes, display, overrides, rules, links; it may add capabilities and attributes and override `display`, `licenceClass`, `defaultAlarmRules` (full replacement). Cycles forbidden; max depth 3 |
| `capabilities` | ≥ 1; `health` is mandatory (validator adds an error if missing, except for `role: server` where it is also mandatory); `optional: true` means a device may not support it (connector declares per device) |
| `exclude` | Remove commands, events or enum values from a capability for this type |
| `attributes` | Parameter schema; static per-device settings |
| `allowedChildTypes` | Type ids or category wildcards (`"video.*"`); empty = no children |
| `licenceClass` | `full`, `light`, `free` |
| `display.icon` | Icon name from the UI icon set (Lucide names or `wp-*` custom icons) |
| `display.symbol` | Symbol id (SVG set `types/symbols/<symbol>/<state>.svg`) |
| `display.primaryProperty` | `<capId>.<propId>` that must exist |
| `stateSummary.overrides` | Ordered; first match wins over the default algorithm (STA §5) |
| `defaultAlarmRules` | Ids of alarm rule templates (format in ALM); resolved when ALM exists; unresolved ids are warnings, not errors, in M0–M1 |
| `defaultLinks` | `kind` from link kinds (REG §6.4); `peerCategory` or `peerType` |

### 5.2 Link kinds (fixed list v1)

`cameraViews` (camera → any), `memberOf` (zone → area, reader → access zone), `controls`
(output → any), `powers` (power supply → any), `partOf` (generic).

### 5.3 Integration manifest (`schemas/manifest.schema.json`)

```json
{
  "$schema": "https://schemas.watchpoint.dev/manifest/1.json",
  "id": "watchpoint.integration.simulator",
  "version": "1.0.0",
  "vendor": "Watchpoint",
  "category": "generic",
  "contract": "^1.0",
  "minWatchpointVersion": "1.0.0",
  "platforms": ["win-x64", "linux-x64", "linux-arm64"],
  "nativeCode": false,
  "isolation": "inProcessAllowed",
  "licence": { "featureKey": "int.simulator", "class": "included" },
  "connectors": [
    {
      "class": "Watchpoint.Integration.Simulator::Watchpoint.Integration.Simulator.SimulatorConnector",
      "serverType": "sim.server",
      "supportedVendorVersions": "*",
      "features": ["discovery", "liveObservation", "stateRefresh", "commandExecution", "eventCatchUp", "diagnostics"],
      "settingsSchema": "settings/simulator.settings.json",
      "secretsSchema": { "fields": [] },
      "limits": { "devicesPerConnection": 50000, "commandsPerSecond": 500 }
    }
  ],
  "types": ["types/sim.server.json"],
  "capabilities": [],
  "ruleTemplates": [],
  "symbols": "symbols/",
  "i18n": "i18n/",
  "docs": "docs/",
  "privacy": { "personalData": [] },
  "files": [
    { "path": "lib/Watchpoint.Integration.Simulator.dll", "sha256": "…64 hex…" }
  ]
}
```

| Field | Rules |
|---|---|
| `isolation` | `inProcessAllowed`, `isolatedRecommended`, `isolatedRequired`; must be `isolatedRequired` if `nativeCode` is true |
| `platforms` | Subset of `win-x64`, `win-x86`, `linux-x64`, `linux-arm64` |
| `licence.class` | `included` (no licence needed), `standard`, `advanced` |
| `connectors[].class` | `"<AssemblyName>::<FullTypeName>"`; assembly must be listed in `files` under `lib/` |
| `features` | Subset of: `discovery`, `liveObservation`, `stateRefresh`, `commandExecution`, `eventCatchUp`, `videoProvider`, `timeSync`, `lookup`, `configurationPush`, `diagnostics` |
| `settingsSchema` | Path (relative) to a parameter schema file, or inline schema |
| `secretsSchema.fields` | `[{ "id": "password", "kind": "password" | "apiKey" | "certificate" | "privateKey" | "token", "required": true }]` |
| `files` | Every file of the package except `manifest.json` and `signature.sig`, with SHA-256; the validator checks completeness when given a package folder |

Signature format and verification are specified in IRT §5.

## 6. Standard type library v0 (`types/`)

```
types/
├── capabilities/<capId>.json
├── device-types/<typeId>.json
├── symbols/<symbolId>/<state>.svg    one SVG per overall state (§3.1 states) + "default.svg"
└── i18n/en.json, fr.json, ar.json
```

### 6.1 Capabilities v0

| Capability | Properties (type: values/severities) | Commands | Events |
|---|---|---|---|
| `health` | `connectivity` (enum: online normal, degraded warning, offline offline, unknown unknown); `faults` (list of enum: tamper, batteryLow, powerFail, commFail, hardwareFault, configError, clockDrift, flooding — each severity fault); `lastContact` (timestamp) | `refresh` (normal, idempotent, timeout PT30S) | `wentOffline` (fault), `cameOnline` (info), `faultRaised` {fault: string} (fault), `faultCleared` {fault} (info) |
| `doorPosition` | `position` (as §4 example) | — | `forced` (alarm), `heldOpen` {openFor: duration} (warning), `closedAgain` (info) |
| `lockable` | `lockState` (enum: locked normal, unlocked normalActive, unknown unknown) | `lock`, `unlock` (sensitive, expectedEffect lockState within PT10S) | — |
| `openable` | — | `open` {duration: PT1S–PT60S, default PT5S} (sensitive, idempotent, batchable, expectedEffect doorPosition.position equals open within PT10S — validator allows cross-capability reference **only** to capabilities of the same type, checked at type level) | `opened` (info) |
| `doorMode` | `mode` (enum: normal normal, unlocked normalActive, lockedDown warning, emergencyOpen alarm, emergencyClose alarm) | `setMode` {mode: enum, until?: timestamp} (critical for lockedDown/emergency*; sensitive otherwise — risk per argument is expressed as `"riskByArgument": {"mode": {"lockedDown": "critical", …}}`) | `modeChanged` {from, to} (info) |
| `accessEvents` | `lastAccess` (object: result, credentialRef, holderRef, holderName, direction, reason) | — | `granted` {credentialRef, holderRef, holderName, direction} (info, personalData holderName), `denied` {credentialRef, holderRef, holderName, reason: enum(noRights, outsideSchedule, expired, blocked, unknownCredential, antipassback, other)} (warning) , `duress` (alarm) |
| `armable` | `armState` (enum: disarmed normal, partial normalActive, full normalActive, arming info, exitDelay info, entryDelay warning, alarm alarm, unknown unknown) | `arm` {mode: full|partial} (sensitive), `disarm` (sensitive), `cancelArming` (normal) | `armed` {mode, byRef}, `disarmed` {byRef}, `armFailed` {reasons: array<string>, openZones: array<entityRef>} (warning) |
| `zoneInput` | `state` (enum: normal normal, active alarm, tamper alarm, short fault, openCircuit fault, masked fault, fault fault) | — | `activated` (alarm), `restored` (info) |
| `bypassable` | `bypassed` (boolean; true severity normalActive — booleans map: `"booleanSeverity": {"true": "normalActive", "false": "normal"}`) | `bypass`, `unbypass` (sensitive) | — |
| `binaryValue` | `value` (boolean, severities configurable per device through attributes `trueSeverity`, `falseSeverity`, labels via device attributes `trueLabel`, `falseLabel` (LocalizedName)) | — | `changed` |
| `switchable` | `on` (boolean) | `switchOn`, `switchOff`, `toggle` (normal, expectedEffect) | — |
| `pulse` | — | `pulse` {duration PT0.1S–PT60S} (normal) | — |
| `measurement` | `value` (decimal; unit, range and thresholds from device attributes `unit`, `min`, `max`, `warningAbove`, `alarmAbove`, `warningBelow`, `alarmBelow`) | — | `thresholdCrossed` {threshold: enum, value} |
| `tamper` | `tampered` (boolean: true alarm) | — | `tamper` (alarm), `tamperRestored` (info) |
| `battery` | `level` (decimal %, 0–100), `low` (boolean: true warning) | — | `low` (warning), `restored` (info) |

Boolean severities: properties of type `boolean` may declare
`"booleanSeverity": { "true": <severity>, "false": <severity> }` (default normal/normal).
Per-argument risk: commands may declare `riskByArgument` as shown.

### 6.2 Device types v0

| Type | Role | Category | Capabilities | Licence | Notes |
|---|---|---|---|---|---|
| `generic.server` | server | generic | health | free | Default server type for packages without their own |
| `ac.door` | device | accessControl | health, doorPosition, lockable, openable, doorMode, accessEvents, tamper (optional) | full | Attributes: `heldOpenAfter` |
| `ac.reader` | device | accessControl | health, accessEvents, tamper (optional) | free | Child of a door, or standalone (`light` when standalone is decided in LIC) |
| `intrusion.area` | device | intrusion | health, armable | full | |
| `intrusion.zone` | device | intrusion | health, zoneInput, bypassable, tamper (optional) | light | |
| `generic.binary-input` | device | generic | health, binaryValue | light | Attributes: `trueSeverity`, `falseSeverity`, `trueLabel`, `falseLabel` |
| `generic.binary-output` | device | generic | health, binaryValue, switchable, pulse (optional) | light | |
| `generic.analog-input` | device | generic | health, measurement | light | Attributes: `unit`, `min`, `max`, thresholds, `hysteresis` |

More types are added by later milestones through the same process (SCM is a living
library; each addition follows §8 and the governance rules in the design docs).

### 6.3 Symbols v0

For each type, SVGs for states `alarm`, `fault`, `warning`, `normalActive`, `normal`,
`maintenance`, `disabled`, `offline`, `unknown`, plus `default.svg`. 48×48 viewBox, no
scripts, no external references, no embedded raster images (validated). Colours use CSS
variables (`var(--wp-state-alarm)`) so themes apply.

## 7. `Watchpoint.Model.Documents` (library)

```csharp
public sealed record CapabilityDocument(CapabilityId Id, SemVersion Version,
    IReadOnlyList<PropertyDefinitionDoc> Properties, IReadOnlyList<CommandDefinitionDoc> Commands,
    IReadOnlyList<EventDefinitionDoc> Events);
public sealed record DeviceTypeDocument(TypeId Id, SemVersion Version, TypeRole Role, Category Category,
    TypeReference? Extends, IReadOnlyList<CapabilityUse> Capabilities, ParameterSchema Attributes,
    IReadOnlyList<string> AllowedChildTypes, LicenceClass LicenceClass, TypeDisplay Display,
    StateSummaryRules StateSummary, IReadOnlyList<string> DefaultAlarmRules, IReadOnlyList<DefaultLink> DefaultLinks);
public sealed record ManifestDocument(...);          // mirrors §5.3
public sealed record ParameterSchema(...);           // parsed restricted schema, with Validate(JsonElement) → IReadOnlyList<FieldError>

public interface IDocumentReader
{
    Result<CapabilityDocument> ReadCapability(ReadOnlySpan<byte> utf8, string sourcePath);
    Result<DeviceTypeDocument> ReadDeviceType(ReadOnlySpan<byte> utf8, string sourcePath);
    Result<ManifestDocument>   ReadManifest(ReadOnlySpan<byte> utf8, string sourcePath);
}

public interface ILibraryValidator
{
    // Cross-document checks over a set (standard library + packages):
    LibraryValidationReport Validate(LibrarySet set, LibraryValidationOptions options);
}
public sealed record LibraryValidationReport(IReadOnlyList<DocumentProblem> Problems)
{ public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error); }
public sealed record DocumentProblem(ProblemSeverity Severity, string SourcePath, string JsonPath,
    ErrorCode Code, IReadOnlyDictionary<string, object?> Args);
```

`SemVersion` and `SemVersionRange` are small types in this library (no external SemVer
dependency needed; supports `MAJOR.MINOR.PATCH`, `^x.y`, `*`).

Cross-document checks (`ILibraryValidator`):

| Code | Check |
|---|---|
| `scm.capability.missing` | Type references a capability not in the set |
| `scm.capability.version_unsatisfied` | No capability version matches the range |
| `scm.type.extends_cycle` / `scm.type.extends_too_deep` | |
| `scm.type.health_missing` | |
| `scm.type.primary_property_unknown` | |
| `scm.type.override_property_unknown` / `override_value_unknown` | |
| `scm.type.exclude_unknown` | Excluded member/value does not exist |
| `scm.command.effect_property_unknown` | `expectedEffect.property` not found in the type |
| `scm.property.writable_without_setter` | `writable: true` without `set<Prop>` command |
| `scm.i18n.key_missing` | Derived key missing in a culture (error for `en`, warning for `fr`/`ar` unless `--strict-i18n`) |
| `scm.symbol.missing` / `scm.symbol.unsafe` | SVG missing for a state / contains script, external ref, raster |
| `scm.type.duplicate` / `scm.capability.duplicate` | Same id+version twice |
| `scm.manifest.file_hash_mismatch` / `file_unlisted` / `file_missing` | Package folder checks |
| `scm.manifest.native_requires_isolation` | |
| `scm.manifest.connector_assembly_unlisted` | |
| `scm.rule.template_unresolved` | Warning only until ALM exists |

## 8. `wptypes` CLI (`tools/wptypes`)

```
wptypes validate <folder> [--with <folder>...] [--strict-i18n] [--format text|json]
    Validates a library folder (standard library or a package), optionally together with
    other folders it depends on (a package is validated with the standard library).
    Exit code 0 = no errors, 1 = errors, 2 = usage/IO error.
wptypes doc <folder> --out <file.md>
    Generates a Markdown catalogue (capabilities, types, values, commands, events).
wptypes gen-ts <folder> --out <dir>
    Generates TypeScript types for the documents (used by UI packages).
```

CI runs `wptypes validate types --strict-i18n` and on every package folder.

## 9. Observability, security

- Validator never executes content; SVG checks reject `<script>`, `on*` attributes,
  `href`/`xlink:href` to external resources, `<image>`, `<foreignObject>`.
- Input size limits: documents ≤ 1 MB, SVG ≤ 200 KB.

## 10. Tests (`tests/service/Watchpoint.Model.Documents.Tests`, `tests/tools/Wptypes.Tests`)

| Id | Test | Given / When / Then |
|---|---|---|
| SCM-U-001 | Valid capability parses | §4 example → success, 1 property, 2 events |
| SCM-U-002 | Unknown top-level property rejected | add `"foo": 1` → problem at `/foo` |
| SCM-U-003 | `x-` extensions accepted | add `"x-note": "a"` → success |
| SCM-U-004 | Enum property without values rejected | → `/properties/0/enumValues` required |
| SCM-U-005 | Duplicate enum codes rejected | two `open` → `common.value.duplicate` |
| SCM-U-006 | Unit on non-number rejected | `string` with `unit` → problem |
| SCM-U-007 | Range min > max rejected | |
| SCM-U-008 | Deadband on non-number rejected | |
| SCM-U-009 | Command timeout over PT10M rejected | |
| SCM-U-010 | Invalid duration format rejected | `"10s"` → invalid format |
| SCM-U-011 | Parameter schema: unsupported keyword rejected | `oneOf` → problem |
| SCM-U-012 | Parameter schema: nested object depth 2 rejected | |
| SCM-U-013 | Parameter schema validates values | schema of §4.4 with `{mode:"x"}` → `/mode` not allowed; missing `mode` → required |
| SCM-U-014 | Parameter schema defaults applied | `{}` with defaults → filled object (`ApplyDefaults`) |
| SCM-U-015 | `visibleWhen` referencing unknown field rejected | |
| SCM-U-016 | Device type example parses | §5 example → success |
| SCM-U-017 | Device type role/category invalid rejected | |
| SCM-U-018 | Manifest example parses | §5.3 example → success |
| SCM-U-019 | Manifest: native code without isolatedRequired rejected | → `scm.manifest.native_requires_isolation` |
| SCM-U-020 | Manifest: class format invalid rejected | `"Foo.Bar"` (no `::`) → invalid format |
| SCM-U-021 | Library: missing capability reported | type uses `foo` → `scm.capability.missing` |
| SCM-U-022 | Library: version range unsatisfied | capability 1.0.0, type wants `^2.0` → error |
| SCM-U-023 | Library: extends merges capabilities | child extends `ac.door` adding `battery` → resolved type has both |
| SCM-U-024 | Library: extends cycle detected | A→B→A → `scm.type.extends_cycle` |
| SCM-U-025 | Library: depth > 3 rejected | |
| SCM-U-026 | Library: health missing | |
| SCM-U-027 | Library: primary property unknown | |
| SCM-U-028 | Library: override value unknown | `lockState: ["open"]` → error |
| SCM-U-029 | Library: exclude unknown member | |
| SCM-U-030 | Library: expected effect unknown property | |
| SCM-U-031 | Library: writable without setter | |
| SCM-U-032 | i18n: missing `en` key is error, missing `fr` is warning | |
| SCM-U-033 | i18n strict: missing `ar` key is error with `--strict-i18n` | |
| SCM-U-034 | Symbols: missing state SVG reported | |
| SCM-U-035 | Symbols: SVG with `<script>` / `onload` / external href / `<image>` rejected | four cases → `scm.symbol.unsafe` |
| SCM-U-036 | Package folder: unlisted file, missing file, hash mismatch | three cases → respective codes |
| SCM-U-037 | **Standard library v0 is valid** | `wptypes validate types --strict-i18n` → exit 0 (this test runs the real `types/` folder) |
| SCM-U-038 | `wptypes` exit codes | errors → 1; bad path → 2; JSON output parses and lists problems |
| SCM-U-039 | `wptypes doc` output stable | snapshot test of generated Markdown for a small fixture library |
| SCM-U-040 | `wptypes gen-ts` compiles | generated TypeScript compiled with `tsc --noEmit` in the test (Node available in CI) |

## 11. Tasks

| Task | Sections | Tests |
|---|---|---|
| T0.06 | §3, §4, §5 (schemas + document parsing in `Watchpoint.Model.Documents`) | SCM-U-001…SCM-U-020 |
| T0.07 | §6, §7 (cross-document validator), §8 (`wptypes`) | SCM-U-021…SCM-U-040 |
