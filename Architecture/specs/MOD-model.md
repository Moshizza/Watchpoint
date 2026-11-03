# MOD — Model (Type Catalogue)

| | |
|---|---|
| Spec id | MOD |
| Projects | `src/service/Watchpoint.Model` (uses `Watchpoint.Model.Documents` from SCM) |
| Milestone | M1 |
| Tasks | T1.06 |
| Design docs | [Servers and devices §5–§7](../../docs/modules/servers-and-devices.md#5-device-types) |

## 1. Purpose and scope

The type catalogue is the runtime authority on **what devices are**: it loads capability
and type documents (standard library + integration packages), resolves inheritance and
exclusions into flat **type definitions**, validates property values, command arguments and
event payloads, computes **overall states**, and serves type translations and symbols.

Not in scope: device instances (REG), current values (STA), package loading (IRT calls
MOD to register documents).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K (incl. `WpValue`, K §10), SCM (`Watchpoint.Model.Documents`), STO (document persistence) | REG, PIP, STA, CMD, ALM, EXP, IRT, API, SYN, AUT, SCR |

## 3. Project layout

```
Watchpoint.Model/
├── Contracts/
│   ├── ITypeCatalog.cs
│   ├── DeviceTypeDefinition.cs, CapabilityDefinition.cs
│   ├── PropertyDefinition.cs, CommandDefinition.cs, EventDefinition.cs
│   ├── TypeRef.cs                          (TypeId + SemVersion)
│   ├── IValueValidator.cs
│   ├── IStateSummaryEvaluator.cs, OverallState.cs, Severity.cs
│   ├── ITypeResources.cs                   translations + symbols
│   └── ModelErrors.cs
├── Internal/
│   ├── TypeCatalog.cs                      immutable snapshot + atomic swap
│   ├── TypeResolver.cs                     extends/excludes flattening
│   ├── ValueValidator.cs
│   ├── UnitConverter.cs
│   ├── StateSummaryEvaluator.cs
│   ├── StandardLibraryLoader.cs            embedded resources from /types
│   └── Storage/ModelDbContext.cs (+ Postgres subclass, migrations)
├── ModelModule.cs
└── ModelLog.cs                             event ids 1400–1499
```

The standard library folder `types/` is embedded into `Watchpoint.Model.dll` as resources at
build time (MSBuild `EmbeddedResource` with `LogicalName` = relative path).

## 4. Public contract

### 4.1 Definitions (flattened, immutable)

```csharp
public sealed record TypeRef(TypeId Id, SemVersion Version);

public sealed record DeviceTypeDefinition
{
    public required TypeRef Ref { get; init; }
    public required TypeRole Role { get; init; }                 // Device | Server
    public required Category Category { get; init; }
    public required string Source { get; init; }                 // "standard" or package id
    public required IReadOnlyList<TypeRef> Lineage { get; init; } // self first, then ancestors
    public required IReadOnlyDictionary<CapabilityId, CapabilityUseDefinition> Capabilities { get; init; }
    public required IReadOnlyDictionary<PropertyPath, PropertyDefinition> Properties { get; init; }  // all, flattened
    public required IReadOnlyDictionary<CommandPath, CommandDefinition> Commands { get; init; }
    public required IReadOnlyDictionary<EventPath, EventDefinition> Events { get; init; }
    public required ParameterSchema Attributes { get; init; }
    public required IReadOnlyList<string> AllowedChildTypes { get; init; }
    public required LicenceClass LicenceClass { get; init; }
    public required TypeDisplay Display { get; init; }
    public required IReadOnlyList<StateOverride> StateOverrides { get; init; }
    public required IReadOnlyList<string> DefaultAlarmRules { get; init; }
    public required IReadOnlyList<DefaultLink> DefaultLinks { get; init; }
    public bool Has(CapabilityId capability);
    public bool IsA(TypeId ancestorOrSelf);                     // lineage check
}

public sealed record CapabilityUseDefinition(CapabilityId Id, SemVersion Version, bool Optional);

public sealed record PropertyDefinition(PropertyPath Path, ValueType ValueType,
    IReadOnlyList<EnumValueDefinition> EnumValues, string? Unit, NumericRange? Range, int? Decimals,
    int? MaxLength, ValueType? ItemType, ParameterSchema? ObjectSchema, bool Writable,
    HistoryPolicy History, TimeSpan? StaleAfter, WpValue? Default, BooleanSeverity? BooleanSeverity);

public sealed record EnumValueDefinition(string Code, Severity Severity);

public sealed record CommandDefinition(CommandPath Path, ParameterSchema Parameters, Risk Risk,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, Risk>> RiskByArgument,
    TimeSpan Timeout, bool Idempotent, bool Batchable, ExpectedEffect? ExpectedEffect);

public sealed record EventDefinition(EventPath Path, Severity Severity, ParameterSchema Payload,
    IReadOnlyList<string> PersonalDataFields);

// CommandPath / EventPath: same shape as PropertyPath ("openable.open", "doorPosition.forced")
public enum Severity { Unknown = 0, Normal = 1, Info = 1, NormalActive = 2, Warning = 3, Offline = 4, Fault = 5, Alarm = 6 }
public enum OverallState { Unknown, Normal, NormalActive, Warning, Offline, Fault, Alarm, Maintenance, Disabled }
public enum Risk { Normal, Sensitive, Critical }
```

### 4.2 Catalogue

```csharp
public interface ITypeCatalog
{
    long Revision { get; }                                        // increments on every change
    Result<DeviceTypeDefinition> Get(TypeRef type);
    Result<DeviceTypeDefinition> GetLatest(TypeId id, int major);  // highest version with that major
    IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query);     // filters: role, category, source, capability
    Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version);

    // Registration (IRT calls these when activating/removing a package version):
    Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion,
        PackageTypeDocuments documents, CancellationToken ct);
    Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct);

    event EventHandler<CatalogChangedEventArgs>? Changed;          // raised after atomic swap
}

public sealed record PackageTypeDocuments(IReadOnlyList<CapabilityDocument> Capabilities,
    IReadOnlyList<DeviceTypeDocument> Types, IReadOnlyDictionary<string, JsonDocument> Translations,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Symbols);   // key "<symbolId>/<state>.svg"
public sealed record CatalogChange(IReadOnlyList<TypeRef> Added, IReadOnlyList<CapabilityId> AddedCapabilities);
```

### 4.3 Values

```csharp
public interface IValueValidator
{
    Result<WpValue> ValidateProperty(PropertyDefinition property, WpValue value, string? reportedUnit = null);
    Result<IReadOnlyDictionary<string, WpValue>> ValidateArguments(CommandDefinition command, JsonElement arguments); // applies defaults
    Result<IReadOnlyDictionary<string, WpValue>> ValidatePayload(EventDefinition evt, IReadOnlyDictionary<string, WpValue> payload);
    Result<IReadOnlyDictionary<string, WpValue>> ValidateAttributes(DeviceTypeDefinition type, JsonElement attributes);
}
```

Validation rules:

| Value type | Accepted input | Normalisation |
|---|---|---|
| boolean | `Bool` | — |
| integer | `Int`; `Decimal` with zero fraction | to `Int`; range check |
| decimal | `Int`, `Decimal` | to `Decimal`; unit conversion (§5.3); range check; rounding to `Decimals` is **display only** (not stored) |
| enum | `Enum(code)` or `String` equal to a code | to `Enum`; unknown code → `model.value.enum_unknown` |
| string | `String` | NFC; length ≤ `MaxLength` |
| timestamp | `Timestamp` | UTC, ms precision |
| duration | `Duration` | — |
| geoPoint | `Geo(lat, lon, alt?)` | lat −90..90, lon −180..180 |
| list | `List` of `ItemType` | each item validated; max 1,000 items |
| object | `Object(JsonElement)` | validated against `ObjectSchema`; max 16 KB serialised |

### 4.4 Overall state

```csharp
public interface IStateSummaryEvaluator
{
    StateSummary Evaluate(DeviceTypeDefinition type, IDeviceStateView view, DeviceFlags flags,
                          IReadOnlyDictionary<string, WpValue> attributes, StateSummary? previous);
}
public interface IDeviceStateView
{
    bool TryGet(PropertyPath path, out PropertyValueView value);   // value + quality
}
public readonly record struct PropertyValueView(WpValue? Value, Quality Quality);
public enum Quality { Good, Uncertain, Bad, Stale }
public readonly record struct DeviceFlags(bool Disabled, bool Maintenance);
public sealed record StateSummary(OverallState State, OverallState UnderlyingState,
    PropertyPath? Cause, IReadOnlyDictionary<PropertyPath, Severity> MeasurementSeverities);
```

### 4.5 Resources

```csharp
public interface ITypeResources
{
    // Merged bundle for a culture: standard + all registered packages, nested i18next JSON.
    ReadOnlyMemory<byte> GetTranslationBundle(string culture, out string etag);
    Result<ReadOnlyMemory<byte>> GetSymbol(string symbolId, OverallState state);   // falls back to default.svg
}
```

## 5. Internal design

### 5.1 Loading and resolution

1. Start-up (phase `Catalogue`, order 0): `StandardLibraryLoader` reads embedded documents,
   parses with `IDocumentReader`, validates with `ILibraryValidator` (strict i18n off for
   `fr`/`ar` warnings). Errors in the standard library are fatal (product defect).
2. Previously registered package documents are **not** reloaded from the database at start;
   IRT re-registers active packages at its own start (phase `Catalogue`, order 10). The
   database copy (`wp_model.documents`) is used only to resolve **historical** types
   (devices still referencing a removed package version, history display).
3. `TypeResolver` flattens each type:
   - start from the root ancestor; apply each descendant in order;
   - capabilities: union by id; a descendant may raise the version range but not lower it;
   - excludes (`commands`, `events`, `enumValues`) applied after union; excluding an enum
     value that is the property's `default` → error `model.type.exclude_default`;
   - attributes: merged schemas, descendant properties override same-name properties;
   - display, licenceClass, defaultAlarmRules: descendant replaces if present;
   - overrides and defaultLinks: concatenated (descendant first).
4. The catalogue is an **immutable snapshot**; registration builds a new snapshot and swaps it
   atomically (`Interlocked.Exchange`), increments `Revision`, raises `Changed`.

### 5.2 Package registration rules

- Package-defined type ids must start with `vendor.`; package-defined capability ids must start
  with `vendor` followed by a dot segment (`vendor.saltoDnd`) — otherwise
  `model.package.reserved_namespace`. Packages may reference standard types and capabilities.
- Same `(id, version)` registered by two different sources → `model.document.conflict`.
- Re-registering the same package version with identical document hashes is a no-op; with
  different content → `model.document.conflict` (versions are immutable).
- Unregistering a package version keeps its types resolvable through the persisted copy
  (`Get` still works with `Source` marked `"<package>@<version> (removed)"`), so existing
  devices keep working until migrated.

### 5.3 Units

`UnitConverter` supports conversion between compatible UCUM units for these dimensions:
temperature (`Cel`, `[degF]`, `K`), length (`m`, `cm`, `mm`, `km`), energy (`W.h`, `kW.h`,
`MW.h`, `J`, `MJ`), power (`W`, `kW`, `MW`), pressure (`Pa`, `kPa`, `bar`, `mbar`), volume
(`L`, `m3`), flow (`L/s`, `m3/h`), time (`s`, `min`, `h`), ratio (`%`, `1`). A connector may
report a value with another compatible unit; MOD converts to the property's unit. Incompatible
or unknown → `model.value.unit_incompatible`.

### 5.4 Overall state algorithm (`StateSummaryEvaluator`)

Inputs: type, current property values with quality, flags, device attributes, previous summary.

1. Compute `underlying`:
   1. If `health.connectivity` is `offline` → `Offline` (cause `health.connectivity`).
   2. Else, for each `StateOverride` in order: if the property value (quality Good or
      Uncertain) is in `values` → that state; stop.
   3. Else take the **worst** severity among:
      - enum properties: severity of the current code;
      - boolean properties with `BooleanSeverity`;
      - `health.faults` non-empty → `Fault`;
      - measurements: severity from attribute thresholds (`alarmAbove`, `warningAbove`,
        `warningBelow`, `alarmBelow`) with hysteresis `attributes.hysteresis` (default 0):
        once in a band, a value must cross back by more than the hysteresis to leave it
        (previous band from `previous.MeasurementSeverities`);
      ignoring values with quality `Bad` or `Stale`.
   4. Map severity to state (`Alarm`, `Fault`, `Warning`, `NormalActive`, `Normal`); if no
      value contributed (all unknown/stale) → `Unknown`.
   5. If `health.connectivity` is `unknown` and step 3 found no Good value → `Unknown`.
2. Displayed `State`: `Disabled` if flags.Disabled; else `Maintenance` if flags.Maintenance;
   else `underlying`.
3. `Cause` = the property that determined the state (first in path order on ties).

The evaluator is pure and allocation-light (called on every property change by STA).

## 6. Data (`wp_model` schema)

```sql
CREATE TABLE wp_model.documents (
  kind          text        NOT NULL,   -- 'capability' | 'type'
  id            text        NOT NULL,
  version       text        NOT NULL,
  source        text        NOT NULL,   -- 'standard' or package id
  source_version text       NOT NULL,   -- package version or product version
  content       jsonb       NOT NULL,
  sha256        char(64)    NOT NULL,
  registered_at timestamptz NOT NULL,
  removed_at    timestamptz NULL,
  PRIMARY KEY (kind, id, version)
);
```

Rows are inserted on first registration and never updated except `removed_at`.

## 7. API

MOD owns these endpoints (implemented through the API framework, see API §6):

| Method and path | Permission | Response |
|---|---|---|
| `GET /api/v1/types?role=&category=&capability=&source=` | authenticated | List of type summaries (ref, role, category, source, icon, capabilities) |
| `GET /api/v1/types/{id}/versions/{version}` | authenticated | Full flattened definition (JSON form of §4.1) |
| `GET /api/v1/capabilities/{id}/versions/{version}` | authenticated | Capability definition |
| `GET /api/v1/i18n/types/{culture}` | anonymous (no secrets inside) | Merged translation bundle; `ETag`; `Cache-Control: no-cache` (revalidate) |
| `GET /api/v1/symbols/{symbolId}/{state}.svg` | authenticated | SVG with `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`, `X-Content-Type-Options: nosniff` |

## 8. Settings

None (behaviour fixed by documents).

## 9. Errors

| Code | Kind | When |
|---|---|---|
| `model.type.not_found` | NotFound | Unknown type ref (args `typeId`, `version`) |
| `model.capability.not_found` | NotFound | |
| `model.document.invalid` | Validation | Document failed SCM validation (field errors from the validator) |
| `model.document.conflict` | Conflict | Same id+version with different content or source |
| `model.package.reserved_namespace` | Validation | Package defines ids outside `vendor.` |
| `model.type.exclude_default` | Validation | Excluded enum value is a property default |
| `model.value.type_mismatch` | Validation | Wrong value kind for the property (args `expected`, `actual`) |
| `model.value.enum_unknown` | Validation | args `code`, `allowed` |
| `model.value.out_of_range` | Validation | args `min`, `max` |
| `model.value.too_long` | Validation | |
| `model.value.unit_incompatible` | Validation | args `unit`, `expected` |
| `model.property.not_found` | NotFound | Path not in type |
| `model.command.not_found` | NotFound | |
| `model.event.not_found` | NotFound | |

## 10. Observability

| EventId | Level | Message |
|---|---|---|
| 1401 | Information | Standard library loaded: {Capabilities} capabilities, {Types} types in {DurationMs} ms |
| 1402 | Information | Package {Package}@{Version} registered: {Types} types, {Capabilities} capabilities |
| 1403 | Information | Package {Package}@{Version} unregistered |
| 1404 | Warning | Document warning {Code} at {Source}{Path} |
| 1405 | Critical | Standard library invalid: {Problems} |

Metrics: `watchpoint.model.catalog.revision` (gauge), `watchpoint.model.types` (gauge, tag
`source`).

## 11. Security

- Symbols are served with strict CSP and were validated as safe at load (SCM §9).
- Package documents are only registered by IRT after signature verification.

## 12. Tests

Unit: `tests/service/Watchpoint.Model.Tests`. Fixtures: a small library folder in
`tests/service/Watchpoint.Model.Tests/Fixtures`.

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| MOD-U-001 | U | Standard library loads | Embedded v0 → types `ac.door`…`generic.analog-input` present |
| MOD-U-002 | U | Invalid standard library is fatal | Fixture with missing capability → start-up exception and log 1405 |
| MOD-U-003 | U | Flattening: capabilities union | `vendor.x.door` extends `ac.door` + `battery` → both sets present |
| MOD-U-004 | U | Flattening: descendant cannot lower version range | Parent `^1.2`, child `^1.0` → resolved `^1.2` |
| MOD-U-005 | U | Excludes remove commands and enum values | Exclude `doorMode` value `emergencyClose` → absent from enum values |
| MOD-U-006 | U | Exclude default value rejected | → `model.type.exclude_default` |
| MOD-U-007 | U | Attributes merge with override | Parent `heldOpenAfter` default 60 s, child default 30 s → 30 s |
| MOD-U-008 | U | Display replaced, overrides concatenated | Child overrides first in list |
| MOD-U-009 | U | `IsA` via lineage | child `IsA("ac.door")` true |
| MOD-U-010 | U | `GetLatest` per major | versions 1.0.0, 1.2.0, 2.0.0 → latest(1) = 1.2.0 |
| MOD-U-011 | U | Registration swaps snapshot atomically | Reader holding old snapshot unaffected; `Revision` +1; `Changed` raised once |
| MOD-U-012 | U | Reserved namespace enforced | Package type `ac.mydoor` → `model.package.reserved_namespace` |
| MOD-U-013 | U | Conflict detection | Same id+version different content → `model.document.conflict` |
| MOD-U-014 | U | Idempotent re-registration | Same content → no change, no event |
| MOD-U-015 | U | Unregister keeps type resolvable | After unregister, `Get` works, source marked removed (DB copy in test double) |
| MOD-U-016 | U | Validate boolean / integer / decimal | Matrix of valid and invalid inputs with expected codes |
| MOD-U-017 | U | Integer accepts integral decimal | `Decimal(5.0)` → `Int(5)`; `5.5` → type mismatch |
| MOD-U-018 | U | Enum from string | `String("open")` → `Enum(open)`; `"opened"` → `model.value.enum_unknown` with allowed list |
| MOD-U-019 | U | String NFC and max length | Decomposed input normalised; over max → too long |
| MOD-U-020 | U | List validation | Items of wrong type → error path `/2`; 1,001 items → too long |
| MOD-U-021 | U | Object validation against schema | Missing required field → field error |
| MOD-U-022 | U | Unit conversion | 77 `[degF]` reported for `Cel` property → 25 °C; `kW.h` → `W.h` ×1000 |
| MOD-U-023 | U | Incompatible unit | `m` reported for `Cel` → `model.value.unit_incompatible` |
| MOD-U-024 | U | Command arguments defaults applied | `openable.open` with `{}` → duration PT5S |
| MOD-U-025 | U | Command argument invalid | duration PT2H → out of range |
| MOD-U-026 | U | Risk by argument | `doorMode.setMode` with mode `lockedDown` → effective risk Critical (helper `EffectiveRisk`) |
| MOD-U-027 | U | Event payload validation | `accessEvents.denied` with reason `bogus` → enum unknown |
| MOD-U-028 | U | State: offline wins | connectivity offline + door forced → `Offline` |
| MOD-U-029 | U | State: override first | lockState unlocked override → `NormalActive` even if position closed |
| MOD-U-030 | U | State: worst severity | position heldOpen (warning) + tamper true (alarm) → `Alarm`, cause `tamper.tampered` |
| MOD-U-031 | U | State: faults → Fault | faults [batteryLow] and others normal → `Fault` |
| MOD-U-032 | U | State: measurement thresholds with hysteresis | alarmAbove 30, hysteresis 1: 30.5 → Alarm; 29.5 → still Alarm; 28.9 → Normal |
| MOD-U-033 | U | State: stale and bad ignored; all unknown → Unknown | |
| MOD-U-034 | U | State: disabled and maintenance flags | Disabled → `Disabled` with underlying computed; Maintenance → `Maintenance` |
| MOD-U-035 | U | Translation bundle merge and ETag | std + package bundles merged (package keys under `types.vendor…`); ETag changes after registration |
| MOD-I-001 | I | Documents persisted on registration | Register package → rows in `wp_model.documents` with sha256 |
| MOD-I-002 | I | Historical type resolution after restart | Register, unregister, restart catalogue → `Get` returns removed type from DB |
| MOD-I-003 | I | Symbol endpoint headers | GET symbol → `image/svg+xml`, CSP and nosniff headers (through API test host) |
| MOD-I-004 | I | i18n endpoint caching | GET with `If-None-Match` current ETag → 304 |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.06 | All (API endpoints wired once API framework exists — T1.19 adds MOD-I-003, MOD-I-004 to CI) | MOD-U-001…MOD-U-035, MOD-I-001…MOD-I-004 |
