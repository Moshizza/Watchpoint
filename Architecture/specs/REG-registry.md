# REG — Registry (Servers, Devices, Locations, Tags, Links)

| | |
|---|---|
| Spec id | REG |
| Projects | `src/service/Watchpoint.Registry` |
| Milestone | M1 |
| Tasks | T1.07 (§4–§7), T1.08 (§8) |
| Design docs | [Servers and devices](../../docs/modules/servers-and-devices.md) |

## 1. Purpose and scope

The Registry owns the **configuration of what is supervised**: the site, the location tree,
tags, servers, devices, links between devices, and custom field definitions. It answers
fast in-memory queries and reconciles inventories reported by connections.

Not in scope: current values (STA), connection settings (IRT), drafts and publishing (CFG
wraps registry change batches from M3), rights filtering (RGT applies scopes on top of
registry queries).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, STO, MOD, PIP (publishes change records; from T1.09) | PIP (enrichment), STA, IRT, CMD, ALM, RGT, API, SYN, LIC (device units) |

## 3. Project layout

```
Watchpoint.Registry/
├── Contracts/
│   ├── Site.cs, Location.cs, Tag.cs, TagFamily.cs, Server.cs, Device.cs, DeviceLink.cs, CustomFieldDefinition.cs
│   ├── IRegistryReader.cs, IRegistryWriter.cs
│   ├── RegistryChangeBatch.cs, ChangeContext.cs
│   ├── IInventoryReconciler.cs, InventoryReport.cs, ReconciliationResult.cs
│   ├── RegistryQueries.cs        (filter records)
│   └── RegistryErrors.cs
├── Internal/
│   ├── RegistrySnapshot.cs        immutable in-memory model + indexes
│   ├── RegistryReader.cs
│   ├── RegistryWriter.cs          validation + persistence + snapshot rebuild
│   ├── LocationPaths.cs
│   ├── InventoryReconciler.cs
│   └── Storage/RegistryDbContext.cs (+ Postgres subclass, migrations)
├── RegistryModule.cs
└── RegistryLog.cs                 event ids 1500–1599
```

## 4. Domain model

All entities are immutable records in the contract; the writer produces new versions.

```csharp
public sealed record Site(SiteId Id, LocalizedName Name, string TimeZone /* IANA */, long Version);

public enum LocationKind { Organisation, Site, Building, Floor, Room, Zone, Outdoor, Other }

public sealed record Location(LocationId Id, LocationId? ParentId, LocalizedName Name, LocationKind Kind,
    string Path /* "/<id>/<id>/" from root, ids lowercase */, int Depth, string? TimeZone /* override */,
    GeoPoint? Geo, string? Notes, int SortOrder, long Version);

public sealed record TagFamily(TagFamilyId Id, LocalizedName Name, bool Exclusive /* one tag per object */, long Version);
public sealed record Tag(TagId Id, string Key /* ^[a-z0-9][a-z0-9-]{0,63}$, unique */, LocalizedName Name,
    TagFamilyId? FamilyId, string? Color /* #RRGGBB */, long Version);

public sealed record Server(ServerId Id, TypeRef Type, ConnectionId ConnectionId, ServerId? ParentId,
    string? NativeId, LocalizedName? Name /* user override */, string VendorName, LocationId LocationId,
    IReadOnlyList<TagId> Tags, IReadOnlyDictionary<string, WpValue> Attributes, bool Enabled,
    bool Orphaned, long Version)
{ public string DisplayName(string culture); }   // Name?.Resolve(culture) ?? VendorName

public sealed record Device(DeviceId Id, TypeRef Type, ServerId ServerId, DeviceId? ParentId,
    string NativeId, LocalizedName? Name, string VendorName, LocationId LocationId,
    IReadOnlyList<TagId> Tags, IReadOnlyDictionary<string, WpValue> Attributes,
    IReadOnlyDictionary<CustomFieldId, WpValue> CustomFields, IReadOnlySet<CapabilityId> UnsupportedOptionalCapabilities,
    DeviceGeometry? Geometry, bool Enabled, bool Maintenance, bool Orphaned,
    DateTimeOffset CreatedAt, long Version)
{ public string DisplayName(string culture); }

public sealed record DeviceGeometry(GeoPoint? Geo, double? Azimuth /*deg*/, double? FieldOfView /*deg*/, double? Range /*m*/);

public enum LinkKind { CameraViews, MemberOf, Controls, Powers, PartOf }
public sealed record DeviceLink(LinkId Id, LinkKind Kind, DeviceId From, DeviceId To, long Version);

public enum CustomFieldTarget { Device, Server, Location }
public sealed record CustomFieldDefinition(CustomFieldId Id, CustomFieldTarget Target, string Key,
    LocalizedName Name, ParameterSchema Schema /* single-field schema */, long Version);
```

Invariants (validated by the writer, each with an error code in §9):

| # | Invariant |
|---|---|
| R1 | Exactly one `Site`, whose id is the installation's `Site:Id` (HST §4); created at first start from settings or the dev seed (see *Site identity* below) |
| R2 | Location tree: one root (kind `Organisation` or `Site`), no cycles, depth ≤ 10, sibling names unique (NameRules comparison) |
| R3 | `Tag.Key` unique; tag in an exclusive family → an object holds at most one tag of that family |
| R4 | `Server.Type` and `Device.Type` resolve in MOD with role `server` / `device` |
| R5 | `(ServerId, NativeId)` unique among devices of a server; `(ConnectionId, NativeId)` unique among servers |
| R6 | Device parent belongs to the same server; parent type's `AllowedChildTypes` admits the child type; no cycles; depth ≤ 4 |
| R7 | Server parent belongs to the same connection; depth ≤ 3 |
| R8 | Attributes valid for the type (MOD `ValidateAttributes`); custom fields valid for their definitions |
| R9 | Link endpoints exist; `CameraViews.From` has capability `videoSource` (from M6; until then any device); no duplicate (kind, from, to) |
| R10 | Every device/server has a location |

**Site identity (R1).** The host passes `Site:Id` (a v7 uuid generated at first start and written back to
`watchpoint.json`, HST §4) to `AddWatchpointRegistry(SiteId)`. Without a site row the snapshot holds the default
site (`Default Site`, `UTC`) with that id, and the first `UpdateSite` (the dev seed's `site` section, HST §9, or the
API) writes the row with it; the Identity token issuer `urn:watchpoint:site:<Site:Id>` (IDN §6.2) and the
site row therefore name the same id. When a row already exists with another id (installations created before
`Site:Id` existed, or a node whose `watchpoint.json` was edited), **the stored row wins**: it is the id the
installation's data and the other nodes of the site refer to, and a single node's settings file must not rewrite
the shared database. The Registry keeps the row, logs event 1504 once at load naming both ids, and the operator
sets `Site:Id` to the stored id (every node of the site carries the same `Site:Id`).

## 5. Public contract — reading

```csharp
public interface IRegistryReader
{
    long Revision { get; }                                     // increments with every applied batch
    Site Site { get; }
    Result<Location> GetLocation(LocationId id);
    IReadOnlyList<Location> GetLocationTree();                 // ordered by Path, SortOrder
    IReadOnlyList<Location> GetAncestors(LocationId id);       // root first
    Result<Server> GetServer(ServerId id);
    Result<Device> GetDevice(DeviceId id);
    Result<Device> FindDeviceByNativeId(ServerId server, string nativeId);
    IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection);
    IReadOnlyList<Device> GetChildren(DeviceId parent);
    IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both);
    Page<Device> QueryDevices(DeviceQuery query, PageRequest page);
    Page<Server> QueryServers(ServerQuery query, PageRequest page);
    IReadOnlyList<Tag> GetTags();
    IReadOnlyList<TagFamily> GetTagFamilies();
    IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target);
    DeviceEnrichment GetEnrichment(DeviceId id);               // used by PIP (cached, see §7.2)
    event EventHandler<RegistryChangedEventArgs>? Changed;
}

public sealed record DeviceQuery(
    IReadOnlyList<ServerId>? Servers = null, IReadOnlyList<LocationId>? LocationsSubtree = null,
    IReadOnlyList<TypeId>? Types = null /* includes descendants via IsA */, IReadOnlyList<Category>? Categories = null,
    IReadOnlyList<CapabilityId>? Capabilities = null, IReadOnlyList<TagId>? TagsAll = null,
    IReadOnlyList<TagId>? TagsAny = null, string? Text = null /* name, vendor name, native id; case/diacritic-insensitive contains */,
    bool? Enabled = null, bool? Maintenance = null, bool? Orphaned = null, DeviceId? Parent = null,
    bool RootsOnly = false, IReadOnlySet<DeviceId>? Within = null /* RGT visible set */, SortSpec? Sort = null);
// Sort fields: name (display name in culture), type, location, createdAt. Default name asc.

public sealed record DeviceEnrichment(DeviceId Id, string DisplayNameDefault, ServerId ServerId,
    LocationId LocationId, string LocationPath, IReadOnlyList<string> LocationNames /* default culture, root first */,
    IReadOnlyList<string> TagKeys, TypeRef Type, IReadOnlyList<DeviceId> LinkedCameras, bool Enabled, bool Maintenance);
```

## 6. Public contract — writing

```csharp
public interface IRegistryWriter
{
    Task<Result<RegistryApplyResult>> ApplyAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct);
}

public sealed record ChangeContext(UserId? Actor, string Origin /* "api", "integration:<connId>", "seed", "publish:<changeSetId>" */,
    ChangeSetId? ChangeSet, string? Comment);

public sealed record RegistryChangeBatch(IReadOnlyList<RegistryChange> Changes);

public abstract record RegistryChange
{
    public sealed record UpsertLocation(Location Location, long? ExpectedVersion) : RegistryChange;   // Path/Depth computed by writer
    public sealed record DeleteLocation(LocationId Id, LocationId? ReassignTo, long? ExpectedVersion) : RegistryChange;
    public sealed record UpsertTag(Tag Tag, long? ExpectedVersion) : RegistryChange;
    public sealed record DeleteTag(TagId Id) : RegistryChange;
    public sealed record UpsertTagFamily(TagFamily Family, long? ExpectedVersion) : RegistryChange;
    public sealed record UpsertServer(Server Server, long? ExpectedVersion) : RegistryChange;
    public sealed record DeleteServer(ServerId Id, bool DeleteDevices) : RegistryChange;
    public sealed record UpsertDevice(Device Device, long? ExpectedVersion) : RegistryChange;
    public sealed record PatchDevice(DeviceId Id, DevicePatch Patch, long? ExpectedVersion) : RegistryChange;  // bulk edits
    public sealed record DeleteDevice(DeviceId Id, bool DeleteChildren) : RegistryChange;
    public sealed record UpsertLink(DeviceLink Link) : RegistryChange;
    public sealed record DeleteLink(LinkId Id) : RegistryChange;
    public sealed record UpsertCustomField(CustomFieldDefinition Definition, long? ExpectedVersion) : RegistryChange;
    public sealed record UpdateSite(Site Site, long? ExpectedVersion) : RegistryChange;
}

public sealed record DevicePatch(Optional<LocalizedName?> Name, Optional<LocationId> Location,
    Optional<IReadOnlyList<TagId>> TagsSet, IReadOnlyList<TagId>? TagsAdd, IReadOnlyList<TagId>? TagsRemove,
    Optional<bool> Enabled, Optional<bool> Maintenance, Optional<IReadOnlyDictionary<string, WpValue>> AttributesMerge);
// Optional<T> is a Kernel-style "set or not set" wrapper defined in this module (HasValue + Value).

public sealed record RegistryApplyResult(long Revision, IReadOnlyList<EntityChange> Changes);
public sealed record EntityChange(string Entity /* "device" … */, Guid Id, ChangeKind Kind /* Created, Updated, Deleted */);
```

`ApplyAsync` semantics:

1. Validate the whole batch against the **current snapshot plus earlier changes of the same
   batch** (so a batch can create a location and a device in it). Collect all errors
   (Validation error with field errors whose paths are `/changes/<index>/…`).
2. Optimistic concurrency: `ExpectedVersion` (when given) must equal the current version,
   else `common.version.conflict` (args `entity`, `id`, `expected`, `actual`).
3. Persist in **one database transaction** (registry schema). Versions incremented by one
   per modified entity.
4. Build a new snapshot (incremental: copy-on-write of changed indexes), swap atomically,
   increment `Revision`.
5. Publish a `registry.changed` record to the event log (stream `WP_CFG`, PIP §5.3) containing
   `Revision`, `ChangeContext` and the `EntityChange` list; raise `Changed` in-process. The record
   is published through `IRegistryChangePublisher.PublishAsync(RegistryApplyResult, ChangeContext, ct)`,
   implemented by PIP (a no-op when the pipeline module is not registered). `EntityChange` lists
   the entities named by the batch (for a link: its source device). The revision counts the batches
   applied since the node started (not yet persisted).
6. Deleting a location with children or objects: requires `ReassignTo` (objects and child
   locations move there) else `registry.location.not_empty`.
7. Deleting a server: `DeleteDevices=false` and devices exist → `registry.server.not_empty`.
8. Deleting a device with children: `DeleteChildren=false` → `registry.device.has_children`.
   Links of deleted devices are deleted.

Size limit: 10,000 changes per batch (`registry.batch.too_large`).

Writes are serialised by a single async lock per node (writes are rare; correctness first).
In a cluster (M9) the configuration publish path (CFG) is the only writer; REG relies on the
database transaction + version checks.

## 7. Internal design

### 7.1 Snapshot

`RegistrySnapshot` holds immutable dictionaries (`System.Collections.Immutable` or frozen
collections rebuilt incrementally): by id for every entity; `(serverId, nativeId) → deviceId`;
`connectionId → servers`; `parentId → children`; `locationId → devices`, `locationId →
subtree ids` (via Path prefix); `tagId → devices`; `typeId → devices`; links by endpoint.
Text search uses a precomputed normalised search key per device (display names in all
cultures + vendor name + native id; lower-case, diacritics removed).

Expected size: 50,000 devices ≈ 60 MB; start-up load ≤ 3 s for 50,000 devices (REG-I-009).

### 7.2 Enrichment cache

`GetEnrichment` returns a cached `DeviceEnrichment`, invalidated per affected device on
change (location rename invalidates all devices of the subtree). Must be O(1) on hits;
called by PIP on every observation.

### 7.3 Location paths

`Path` = `"/" + string.Join("/", ancestorIds + selfId) + "/"`. Moving a location rewrites the
Path of all descendants in the same transaction (`UPDATE … SET path = @new || substring(path
from length(@old)+1) WHERE path LIKE @old || '%'`).

## 8. Inventory reconciliation (T1.08)

Connections report what their server contains (INT `Describe` / discovery). The reconciler
compares it with the registry.

```csharp
public interface IInventoryReconciler
{
    Task<Result<ReconciliationResult>> ReconcileAsync(ConnectionId connection, InventoryReport report,
        ReconcileOptions options, CancellationToken ct);
    Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredAsync(ConnectionId connection, DiscoveredStatus? status, CancellationToken ct);
    Task<Result<RegistryApplyResult>> ImportAsync(ConnectionId connection, ImportRequest request, ChangeContext ctx, CancellationToken ct);
    Task<Result> SetIgnoredAsync(ConnectionId connection, IReadOnlyList<string> nativeIds, bool ignored, CancellationToken ct);
}

public sealed record InventoryReport(IReadOnlyList<InventoryServer> Servers, IReadOnlyList<InventoryDevice> Devices, bool IsComplete);
public sealed record InventoryServer(string NativeId, string? ParentNativeId, TypeRef Type, string VendorName,
    IReadOnlyDictionary<string, WpValue> VendorAttributes);
public sealed record InventoryDevice(string NativeId, string ServerNativeId, string? ParentNativeId, TypeRef Type,
    string VendorName, IReadOnlyDictionary<string, WpValue> VendorAttributes,
    IReadOnlySet<CapabilityId> UnsupportedOptionalCapabilities,
    IReadOnlyList<InventoryLink> Links /* by native ids */);
public sealed record InventoryLink(LinkKind Kind, string ToNativeId, string? ToServerNativeId);

public sealed record ReconcileOptions(bool AutoImportNew, LocationId DefaultLocation);
public sealed record ReconciliationResult(int Unchanged, int Updated, int Orphaned, int Restored,
    int NewDiscovered, int Imported, IReadOnlyList<ReconcileWarning> Warnings);
```

Algorithm (per connection):

1. Servers: match by `(connectionId, nativeId)`. New → created automatically (servers are
   always imported; default location = connection's location). Existing → update
   `VendorName`, vendor attributes (keys declared by the type as vendor-managed: attributes
   with `x-wp-vendor: true` in the type schema), parent. Missing (only if `IsComplete`) →
   `Orphaned = true`.
2. Devices: match by `(serverId, nativeId)`.
   - Existing: update `VendorName`, vendor attributes, `UnsupportedOptionalCapabilities`,
     type **minor/patch** upgrade (same major) — a major type change produces a warning
     `registry.reconcile.type_major_change` and no change. Never changes user fields (`Name`,
     `Location`, `Tags`, `Enabled`, `Maintenance`, custom fields).
   - Was orphaned and is present again → `Orphaned = false` (counted `Restored`).
   - Missing from a complete report → `Orphaned = true`. **Never deleted automatically.**
   - New: if `AutoImportNew` → created (location = parent's location or `DefaultLocation`);
     else upserted into the **discovered items** list with status `new` (or left `ignored`
     if previously ignored).
3. Links reported by native ids are created/updated for imported devices; unresolved peers
   → warning.
4. All registry changes go through `IRegistryWriter.ApplyAsync` in one batch (Origin
   `integration:<connId>`). Discovered items are stored in the same transaction scope where
   possible (separate table, same schema).
5. Partial reports (`IsComplete=false`, e.g. incremental notifications) never orphan anything.

Discovered items table (§10) keeps `first_seen`, `last_seen`, proposed type, vendor name,
parent, and status `new | ignored | imported`. `ImportAsync` takes selected native ids with
optional name/location/tag overrides and creates devices (children follow their parent
unless excluded).

## 9. Errors

| Code | Kind | Args | Invariant |
|---|---|---|---|
| `registry.site.not_initialised` | Unavailable | — | R1 |
| `registry.location.not_found` / `server.not_found` / `device.not_found` / `tag.not_found` / `link.not_found` / `custom_field.not_found` | NotFound | `id` | |
| `registry.location.cycle` | Validation | | R2 |
| `registry.location.too_deep` | Validation | `max` | R2 |
| `registry.location.duplicate_name` | Validation | `name` | R2 |
| `registry.location.not_empty` | Conflict | `children`, `objects` | §6 |
| `registry.location.root_immutable` | Validation | | Root cannot be moved or deleted |
| `registry.tag.duplicate_key` | Validation | `key` | R3 |
| `registry.tag.family_exclusive` | Validation | `family` | R3 |
| `registry.type.invalid_role` | Validation | `typeId`, `expectedRole` | R4 |
| `registry.device.duplicate_native_id` | Validation | `nativeId` | R5 |
| `registry.server.duplicate_native_id` | Validation | `nativeId` | R5 |
| `registry.device.parent_invalid` | Validation | `reason` (`otherServer`, `typeNotAllowed`, `cycle`, `tooDeep`) | R6 |
| `registry.server.parent_invalid` | Validation | `reason` | R7 |
| `registry.device.has_children` | Conflict | `count` | §6 |
| `registry.server.not_empty` | Conflict | `devices` | §6 |
| `registry.link.invalid` | Validation | `reason` | R9 |
| `registry.batch.too_large` | Validation | `max` | §6 |
| `registry.reconcile.type_major_change` | (warning) | `nativeId`, `from`, `to` | §8 |
| `registry.reconcile.link_unresolved` | (warning) | `nativeId`, `toNativeId` | §8 |

Attribute validation errors come from MOD (`model.value.*`) with paths
`/changes/<i>/device/attributes/<key>`.

**Known limitation (until T1.09b).** The `registry.changed` record is published after the registry
transaction committed, and the revision is counted in memory:

- the revision restarts at 1 when the node restarts;
- if the event log stays unavailable for `Log:AppendTimeout` (30 s) after the commit, the change is
  kept, error 1611 is logged (PIP §12) and no record is published — consumers see a gap in the
  revisions and must read the registry again; a node that stops between commit and publish loses the
  record the same way.

T1.09b replaces this with a transactional outbox: revision and change record are stored in the
registry transaction and published to the log from the outbox, in order.

## 10. Data (`wp_registry` schema)

| Table | Columns (type) | Keys and indexes |
|---|---|---|
| `site` | id uuid, name jsonb, time_zone text, row_version bigint, created_at, updated_at | PK id |
| `locations` | id uuid, parent_id uuid null, name jsonb, kind text, path text, depth smallint, time_zone text null, geo_lat double null, geo_lon double null, notes text null, sort_order int, row_version, created_at, updated_at | PK id; FK parent_id → locations; index path (text_pattern_ops); unique (parent_id, name_key) where name_key = normalised default name |
| `tag_families` | id, name jsonb, exclusive bool, row_version, … | PK id |
| `tags` | id, key text, name jsonb, family_id uuid null, color text null, row_version, … | PK id; unique key; FK family_id |
| `servers` | id, type_id text, type_version text, connection_id uuid, parent_id uuid null, native_id text null, name jsonb null, vendor_name text, location_id uuid, attributes jsonb, enabled bool, orphaned bool, row_version, … | PK id; unique (connection_id, native_id); FK parent_id, location_id |
| `devices` | id, type_id, type_version, server_id uuid, parent_id uuid null, native_id text, name jsonb null, vendor_name text, location_id uuid, attributes jsonb, custom_fields jsonb, unsupported_caps text[], geo_lat, geo_lon, azimuth, fov, range_m (double null), enabled bool, maintenance bool, orphaned bool, row_version, created_at, updated_at | PK id; unique (server_id, native_id); FK server_id, parent_id, location_id; index location_id; index type_id |
| `object_tags` | object_kind text, object_id uuid, tag_id uuid | PK (object_kind, object_id, tag_id); index tag_id |
| `device_links` | id, kind text, from_id uuid, to_id uuid, row_version, … | PK id; unique (kind, from_id, to_id); FK from_id, to_id ON DELETE CASCADE |
| `custom_fields` | id, target text, key text, name jsonb, schema jsonb, row_version, … | PK id; unique (target, key) |
| `discovered_items` | connection_id uuid, native_id text, kind text (`device`), server_native_id text, parent_native_id text null, type_id, type_version, vendor_name, vendor_attributes jsonb, status text, first_seen, last_seen | PK (connection_id, native_id) |

`connection_id` is a plain uuid (owned by IRT, no cross-schema FK).

## 11. API (owned by REG, framework in API spec)

Reads (M1: any authenticated user; M3: permission `operate.devices.view` + scopes via RGT):

| Method and path | Description |
|---|---|
| `GET /api/v1/site` | Site |
| `GET /api/v1/locations` | Whole tree (flat list ordered by path) |
| `GET /api/v1/locations/{id}` | One location with ancestors |
| `GET /api/v1/tags`, `GET /api/v1/tag-families` | |
| `GET /api/v1/servers?connection=&location=&type=&text=&limit=&cursor=` | |
| `GET /api/v1/servers/{id}` | |
| `GET /api/v1/devices?server=&location=&type=&category=&capability=&tag=&anyTag=&text=&enabled=&maintenance=&orphaned=&parent=&roots=&sort=&limit=&cursor=` | `location` = subtree |
| `GET /api/v1/devices/{id}` | Includes `displayName` resolved in the caller's culture |
| `GET /api/v1/devices/{id}/children`, `GET /api/v1/devices/{id}/links` | |
| `GET /api/v1/connections/{id}/discovered?status=` | Discovered items |

Writes (M1–M2: permission `config.registry.edit`, administrators only; from M3 these routes
remain for **quick edits** and internally create a draft + publish through CFG):

| Method and path | Body |
|---|---|
| `POST /api/v1/registry/batch` | `RegistryChangeBatch` JSON (`{"changes":[{"op":"upsertDevice", …}]}`) |
| `PATCH /api/v1/devices/{id}` | JSON Merge Patch of `name`, `locationId`, `tags`, `enabled`, `maintenance`, `attributes` (with `If-Match`) |
| `POST /api/v1/connections/{id}/discovered/import` | `{ "nativeIds": [...], "overrides": {...} }` |
| `POST /api/v1/connections/{id}/discovered/ignore` | `{ "nativeIds": [...], "ignored": true }` |

JSON shape of a device (API):

```json
{
  "id": "0192…", "type": { "id": "ac.door", "version": "1.0.0" }, "serverId": "…", "parentId": null,
  "nativeId": "door-7", "name": { "default": "Main entrance", "translations": {} },
  "vendorName": "DOOR 7", "displayName": "Main entrance",
  "locationId": "…", "tags": ["…"], "attributes": { "heldOpenAfter": "PT60S" },
  "customFields": {}, "geometry": null, "enabled": true, "maintenance": false, "orphaned": false,
  "createdAt": "2026-11-03T09:00:00.000Z", "version": 3
}
```

## 12. Observability

| EventId | Level | Message |
|---|---|---|
| 1501 | Information | Registry loaded: {Locations} locations, {Servers} servers, {Devices} devices in {DurationMs} ms |
| 1502 | Information | Registry batch applied by {Origin}: {Created} created, {Updated} updated, {Deleted} deleted, revision {Revision} |
| 1503 | Warning | Registry batch rejected: {ErrorCount} errors from {Origin} |
| 1504 | Warning | Registry site row {StoredSiteId} differs from Site:Id {ConfiguredSiteId}: keeping the stored row; set Site:Id to the stored id so the token issuer matches the site |
| 1510 | Information | Reconciled connection {ConnectionId}: {Updated} updated, {Orphaned} orphaned, {Restored} restored, {New} new, {Imported} imported |
| 1511 | Warning | Reconcile warning {Code} for {NativeId} |

Metrics (`Watchpoint.Registry`): `watchpoint.registry.devices` (gauge, tags `category`,
`state` = enabled/disabled/orphaned), `watchpoint.registry.apply.duration` (ms),
`watchpoint.registry.revision` (gauge).

## 13. Security

- Write endpoints require configuration permissions; reads filtered by RGT scopes (M3).
- Text search inputs limited to 200 characters; no raw SQL built from inputs (snapshot
  queries in memory).

## 14. Tests

Unit: `tests/service/Watchpoint.Registry.Tests` (snapshot and writer with an in-memory
persistence fake). Integration: `Watchpoint.IntegrationTests/Registry`.

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| REG-U-001 | U | Site required | Empty registry → `Site` access → `registry.site.not_initialised` |
| REG-U-002 | U | Create location tree | Batch: root, building, floor → paths `/r/`, `/r/b/`, `/r/b/f/`; depths 0,1,2 |
| REG-U-003 | U | Location cycle rejected | Move building under its floor → `registry.location.cycle` |
| REG-U-004 | U | Depth limit | 11 levels → `registry.location.too_deep` |
| REG-U-005 | U | Sibling duplicate names | Two "Floor 1" under same parent (one with different case/accents) → duplicate_name |
| REG-U-006 | U | Move location rewrites descendant paths | Move building B under site 2 → all floors/rooms paths updated; devices' subtree queries follow |
| REG-U-007 | U | Delete non-empty location requires reassign | → `not_empty`; with `ReassignTo` → children and devices moved |
| REG-U-008 | U | Root immutable | Delete root → `root_immutable` |
| REG-U-009 | U | Tag key unique and pattern | Duplicate key → error; `Bad Key` → invalid format |
| REG-U-010 | U | Exclusive tag family | Device with two tags of family "Risk" → `family_exclusive` |
| REG-U-011 | U | Server type must have role server | `ac.door` as server type → `type.invalid_role` |
| REG-U-012 | U | Device native id unique per server | Two devices same server same nativeId → duplicate; same nativeId on different servers → ok |
| REG-U-013 | U | Device parent rules | Parent on other server / disallowed child type / cycle / depth 5 → respective reasons |
| REG-U-014 | U | Attributes validated via MOD | `heldOpenAfter: PT2H` (max PT1H) → field error path `/changes/0/device/attributes/heldOpenAfter` |
| REG-U-015 | U | Batch can reference earlier changes | Create location + device in it in one batch → success |
| REG-U-016 | U | All batch errors collected | Batch with 3 invalid changes → one validation error with 3 field errors |
| REG-U-017 | U | ExpectedVersion mismatch | Update device with stale version → `common.version.conflict` with expected/actual |
| REG-U-018 | U | Versions increment by one per modified entity | |
| REG-U-019 | U | Delete server with devices | `DeleteDevices=false` → `server.not_empty`; true → devices and their links deleted |
| REG-U-020 | U | Delete device with children | → `has_children`; with flag → cascade |
| REG-U-021 | U | Link validation | Missing endpoint → `link.invalid`; duplicate → `link.invalid` reason duplicate |
| REG-U-022 | U | Query by location subtree | Devices in building and floors returned for building id |
| REG-U-023 | U | Query by type includes descendants | `vendor.x.door` returned for type `ac.door` |
| REG-U-024 | U | Text search case- and diacritic-insensitive | "entree" finds "Entrée principale" and native id "ENT-1" |
| REG-U-025 | U | Tags all/any filters | |
| REG-U-026 | U | Paging stable with cursor | 250 devices sorted by name, limit 100 → 3 pages, no duplicates/gaps, even when a device is renamed between pages |
| REG-U-027 | U | `Within` restricts results | Visible set of 5 ids → only those |
| REG-U-028 | U | Display name resolution | Name override fr present → fr; no override → vendor name |
| REG-U-029 | U | Enrichment cache invalidation | Rename location → enrichment of devices in subtree updated; others untouched |
| REG-U-030 | U | Batch size limit | 10,001 changes → `batch.too_large` |
| REG-U-031 | U | Reconcile: new server auto-created | Report with server S → created at connection's location |
| REG-U-032 | U | Reconcile: existing device vendor name updated, user name untouched | |
| REG-U-033 | U | Reconcile: vendor attributes updated, user attributes untouched | attribute with `x-wp-vendor` updated; other attribute unchanged |
| REG-U-034 | U | Reconcile: missing device orphaned on complete report | |
| REG-U-035 | U | Reconcile: partial report never orphans | |
| REG-U-036 | U | Reconcile: orphan restored when back | |
| REG-U-037 | U | Reconcile: new devices go to discovered list when not auto-importing | status `new`, `first_seen` set |
| REG-U-038 | U | Reconcile: ignored items stay ignored | |
| REG-U-039 | U | Reconcile: auto-import places devices at parent's location | |
| REG-U-040 | U | Reconcile: minor type upgrade applied; major change warned | |
| REG-U-041 | U | Reconcile: links by native id created; unresolved → warning | |
| REG-U-042 | U | Reconcile: unsupported optional capabilities recorded | |
| REG-U-043 | U | Import selected discovered items with overrides | names/location/tags applied; children follow parent |
| REG-U-044 | U | Reconcile is idempotent | Same report twice → second run `Unchanged = n`, no writes |
| REG-U-045 | U | Reconcile publishes one batch with origin `integration:<id>` | |
| REG-U-046 | U | Created site carries `Site:Id` | No site row; `AddWatchpointRegistry(siteId)` → `Site.Id == siteId`; an `UpdateSite` writes the row with that id |
| REG-U-047 | U | Stored site row with another id wins | Row with id A, `Site:Id` = B → `Site.Id == A`; one warning 1504 naming A and B; no write |
| REG-I-001 | I | Persist and reload | Apply batch; new registry instance loads same snapshot (deep equality) |
| REG-I-002 | I | Transaction atomicity | Failure injected mid-persist → nothing persisted, snapshot unchanged |
| REG-I-003 | I | Unique constraints back up validation | Direct duplicate insert (bypassing validation) → DB rejects |
| REG-I-004 | I | Location move SQL updates descendants | 3-level subtree moved → paths correct in DB |
| REG-I-005 | I | `registry.changed` record published to the log | After apply, consumer on config subject receives record with revision and changes |
| REG-I-006 | I | Concurrent writers: version conflict surfaces | Two writers update same device → one succeeds, one conflict |
| REG-I-007 | I | Discovered items persisted | Reconcile with new items → rows; import → status imported |
| REG-I-008 | I | API device list with filters and paging | Through API test host: filters map to query; cursor works |
| REG-I-009 | I | Load time 50,000 devices | Seed 50,000 devices → snapshot load < 3 s on a developer machine or with `WATCHPOINT_STRICT_TIMING=1` (benchmark run); on shared CI runners (`CI=true`) < 10 s (TST §4.7), and the measured time is written to the test output. Memory < 150 MB is a benchmark measurement, not asserted in CI |
| REG-I-010 | I | PATCH device with If-Match | Correct ETag → 200 new ETag; stale → 412 `common.version.conflict` |

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.07 | §4–§7, §9–§13 (except reconciliation) | REG-U-001…REG-U-030, REG-I-001…REG-I-004, REG-I-006, REG-I-009 |
| T1.08 | §8 | REG-U-031…REG-U-045, REG-I-007 |
| T1.07b | §4 R1 (site identity), §12 (event 1504) | REG-U-046, REG-U-047 |
