# CFG — Configuration (Drafts, Validation, Publish, Versions)

| | |
|---|---|
| Spec id | CFG |
| Projects | `src/service/Watchpoint.Configuration` |
| Milestone | M3 |
| Tasks | T3.01 (§4–§6), T3.02 (§7–§8), T3.03 (§9–§10) |
| Design docs | [Configuration management](../../docs/modules/configuration-management.md) |

## 1. Purpose and scope

One safe way to change configuration across all modules: **drafts** of changes, **continuous
validation** with plain-language problems, **atomic publish** of a change set across modules,
**versions** with diff and **rollback**, optional **approval** and **scheduled publish**,
**optimistic concurrency** with merge, **soft locks**, **import/export**.

Modules keep owning their data; CFG orchestrates through a registry of **configurable object
types** that each module implements.

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, STO (configuration database transaction), PIP (`config.published` on WP_CFG), RGT (permissions per object type), SCH (scheduled publish), AUD (audit of publishes) | REG, IRT, ALM, SCH, RGT, IDN (users), NOT, SYN, AUT, SCR, HIS settings… (every module with configuration), UIF |

## 3. Layout

```
Watchpoint.Configuration/
├── Contracts/
│   ├── IConfigurableObjectType.cs, ConfigObjectRef.cs, ConfigDocument.cs
│   ├── ConfigModel.cs (refs, documents, Draft, DraftEntry, Problem, ChangeSet), Ports.cs (authorizer, licence hook, approval policy, publish seam, draft repository, document source)
│   ├── IDraftService.cs, IPublishService.cs, IVersionService.cs, ILockService.cs, IImportExportService.cs
│   └── ConfigurationErrors.cs
├── Adapters/DocumentObjectType.cs, ModuleObjectTypes.cs   object types of the existing modules (REG, IRT, SCH, IDN, HIS): schema + references
├── Internal/
│   ├── ObjectTypeRegistry.cs
│   ├── Validation/DocumentSchemaValidator.cs, DraftValidator.cs, DraftView.cs, JsonPatch.cs   (pure; reference checks in DraftValidator)
│   ├── Drafts/DraftService.cs, InMemoryDraftRepository.cs, DraftSecrets.cs, ValidationJob.cs, QuickEdit.cs
│   ├── Publish/PublishOrchestrator.cs, ApprovalWorkflow.cs, ScheduledPublisher.cs
│   ├── Versions/VersionStore.cs, InverseBuilder.cs, JsonDiff.cs, ThreeWayMerge.cs
│   ├── Locks/SoftLockService.cs
│   ├── ImportExport/BundleExporter.cs, BundleImporter.cs, TabularImporter.cs (CSV/XLSX)
│   └── QuickEdit.cs
├── ConfigurationModule.cs
└── ConfigurationLog.cs              event ids 1300–1399
```

## 4. Configurable object types (T3.01)

```csharp
public readonly record struct ConfigObjectRef(string Type /* "registry.device" */, Guid Id);

public sealed record ConfigDocument(ConfigObjectRef Ref, long Version, JsonElement Content);   // Content = module's JSON document of the object

public interface IConfigurableObjectType
{
    string Type { get; }                                   // "<module>.<object>", e.g. "alarms.rule"
    string Module { get; }
    PermissionCode PermissionFor(ConfigAccess access);     // View | Edit | Publish
    JsonElement? Schema { get; }                           // document schema (DocumentSchema subset, §5): replaces DocumentTypeInfo
    bool SupportsSoftLock { get; }                         // large documents: synoptics, scripts, procedures
    bool Tabular { get; }                                  // CSV/XLSX import/export supported (TabularMapping: T3.03)

    Task<ConfigDocument?> LoadAsync(Guid id, CancellationToken ct);
    IAsyncEnumerable<ConfigDocument> ListAsync(ConfigListQuery query, CancellationToken ct);

    // Validate one entry in the context of the whole draft (other entries visible through `draft`): module rules beyond the schema.
    // Deterministic; problems are data (code, args, path), never message text; CFG adds the object reference.
    ValueTask<IReadOnlyList<Problem>> ValidateAsync(DraftEntry entry, IDraftView draft, CancellationToken ct);

    // References this document makes to other objects (for delete checks and import mapping).
    IReadOnlyList<ConfigObjectRef> GetReferences(JsonElement content);

    // --- T3.02 (publish): not in the contract of T3.01. ---
    // Transactional apply: write within the given transaction; return new versions. No in-memory changes yet.
    Task<IReadOnlyList<AppliedEntry>> PrepareApplyAsync(IReadOnlyList<DraftEntry> entries, DbConnection connection,
        DbTransaction transaction, ChangeContext context, CancellationToken ct);
    // After commit: update in-memory snapshots, publish module records if any. Must not fail (log only).
    ValueTask CompleteApplyAsync(IReadOnlyList<AppliedEntry> applied, ChangeContext context, CancellationToken ct);
    // Other nodes (M9) and self-healing: reload the listed objects from the database into memory.
    ValueTask ReloadAsync(IReadOnlyList<ConfigObjectRef> objects, CancellationToken ct);
}

public sealed record AppliedEntry(ConfigObjectRef Ref, EntryOperation Operation, long NewVersion, JsonElement? Before, JsonElement? After);
```

Modules register their object types at start-up (phase `Catalogue`) with `IConfigObjectTypeRegistry.Register`; registration order is
the order publishing applies them in. T3.01 describes the types of the modules that exist (REG, IRT, SCH, IDN, HIS) in `Adapters/`: a
`DocumentObjectType` is an object type declared by an `ObjectTypeDefinition` (schema, references by JSON pointer, optional module rule)
that reads live documents through an `IConfigDocumentSource`; each module's service becomes that source (or implements the type itself)
when it refactors its write path in T3.02. The ALM and RGT types are described in their own tasks (T3.04 for rights). v1 object types:

| Module | Types |
|---|---|
| REG | `registry.site`, `registry.location`, `registry.tag`, `registry.tagFamily`, `registry.server`, `registry.device`, `registry.link`, `registry.customField` |
| IRT | `integrations.connection` (secrets handled as write-only fields, §6.3) |
| ALM | `alarms.rule`, `alarms.override`, `alarms.procedure`, `alarms.settings` |
| SCH | `scheduling.calendar`, `scheduling.holidaySet` |
| IDN / RGT | `identity.user`, `identity.provider`, `rights.userGroup`, `rights.grants` (one document per user: all grants) |
| HIS | `history.settings`, `history.policyOverride` |
| later | `notifications.*` (M5), `synoptics.synoptic` (M6), `automations.automation` (M8), `scripts.script` (M8), `federation.link` (M9)… |

Modules that previously applied changes directly (REG `ApplyAsync`, IRT `CreateAsync`, ALM rule
CRUD) refactor their write path into `PrepareApply` + `CompleteApply`; their direct REST endpoints
become **quick edits** (§6.4).

## 5. Drafts (T3.01)

```csharp
public enum EntryOperation { Create, Update, Delete }

public sealed record DraftEntry(Guid EntryId, ConfigObjectRef Ref, EntryOperation Operation,
    long? BaseVersion /* version the edit started from; null for Create */, JsonElement? Content /* full new document */,
    DateTimeOffset UpdatedAt);

public sealed record Draft(DraftId Id, LocalizedName Name, UserId Owner, IReadOnlyList<UserId> Collaborators,
    DraftStatus Status, IReadOnlyList<DraftEntry> Entries, IReadOnlyList<Problem> Problems,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishAt, ApprovalInfo? Approval, long Version);
public enum DraftStatus { Open, AwaitingApproval, Scheduled, Publishing, Published, Discarded, Failed }

public sealed record Problem(ProblemSeverity Severity /* Error | Warning */, string Code, IReadOnlyDictionary<string, object?> Args,
    ConfigObjectRef? Object, string? Path /* JSON pointer in the document */, IReadOnlyList<FixSuggestion> Fixes);
public sealed record FixSuggestion(string Code, IReadOnlyDictionary<string, object?> Args, JsonElement? Patch /* JSON Patch to apply */);
```

- A user may have several open drafts; a draft has an owner and optional collaborators
  (same permissions required: adding a collaborator needs the edit permission of every type in the draft; a collaborator's
  edits are checked with the collaborator's own rights; only the owner adds collaborators or discards). A user who is neither owner
  nor collaborator gets `config.draft.not_found`; a discarded draft is gone for everyone.
- **Optimistic concurrency.** `Draft.Version` increases with every change (entry, collaborator, status). Every change names the
  version the editor read; if the stored version differs nothing changes and the answer is `common.version.conflict` (412, args
  `expected`, `actual`); the editor reloads and retries. The store replaces a draft only if it still has the expected version
  (compare and swap), so concurrent changes with the same expected version give exactly one success. Validation results are
  stored without changing the version, and only if the version they were computed for is still current.
- **Change sets.** A draft is isolated from the live configuration: editing, validating, overlay reads and `BuildChangeSetAsync`
  never write live data. `BuildChangeSetAsync` validates the draft, refuses one with errors (`config.publish.invalid`, `errors`) or one
  that is not open, and freezes the entries as a `ChangeSet(id, draftId, draftVersion, entries)`: creates and updates in catalogue
  order (then object id), deletes after them in reverse catalogue order. Modules apply it with change context origin
  `publish:<changeSetId>` and `ChangeSet` (REG §6); publishing itself is T3.02 (`IPublishService`).
- Entry rules: replacing the entry of an object keeps its `EntryId` and `BaseVersion`; a create that is deleted cancels out; a delete
  followed by a create is an update. `config.entry.invalid` (args `reason`: `unknown_type`, `content_required`, `content_not_allowed`,
  `already_exists`, `not_found`) for malformed entries.
- **Validation is pure and deterministic**: a function of the entries and the live configuration; problems are data (`Code`, `Args`,
  `Path` = JSON pointer, `Object`, `Fixes`), never message text or exception text; they are ordered by object type, object id, path,
  code, and `Problem` compares by value. Checks, in order: type known; operation/content consistency; document schema (below);
  the module's `ValidateAsync`; references (`config.reference.missing`, args `type`, `id`, `deletedInDraft`); for deletes the
  live referrers the draft does not touch (`config.reference.in_use`, args `count`, `referrers` (first 10 as `type:id`)); outdated
  base version (`config.entry.outdated`, warning, args `baseVersion`, `liveVersion`); then, for the whole draft, the licence hook
  (`config.licence.quota_exceeded`, args `allowed`, `projected`, `unit`; no object).
- **DocumentSchema** (the JSON Schema subset of `IConfigurableObjectType.Schema`): `type`, `properties`, `required`,
  `additionalProperties: false`, `items`, `enum`, `minimum`, `maximum`, `minLength`, `maxLength`, `pattern`, `format: uuid`,
  `default`. Problems: wrong type or format → `common.value.invalid_format` (`expected`); missing → `common.value.required` at the
  pointer of the missing field; out of range → `common.value.out_of_range` (`min`, `max`); length → `common.value.too_short` /
  `too_long`; not in enum or unknown field → `common.value.not_allowed` (`allowed`).
- **Fix suggestions** carry a JSON Patch (RFC 6902 `add`, `remove`, `replace`): a missing field with a default → `config.fix.set_default`
  (add); a number out of range → `config.fix.set_nearest` (replace with the nearest bound); an unknown field → `config.fix.remove_field`.
  Applying a patch to the entry's content makes its problem go away. Modules add their own fixes.
- On every change the changed entry, the entries that refer to it and all delete entries are checked again (others keep their
  problems); drafts with more than `ValidationChunkSize` (100) entries are validated as a background job with progress
  (`IValidationJob`: `Total`, `Done`, `Completion`).
- Adding/updating an entry for an object already in the draft replaces the entry (keeping the
  original `BaseVersion`).
- Draft size limit: `MaxDraftEntries` (20,000; bulk imports may create large drafts): adding the next entry is `config.draft.too_large`.
- Validation runs after every change for the changed entries **and** entries that reference them
  (ReferenceChecker), within 1 s for typical drafts (≤ 100 entries); larger drafts validate in a
  background job with progress.
- Generic checks done by CFG for every entry: document schema (type info), permission to edit
  this object type and object scope (RGT, e.g. can edit devices of Building A only), references to
  objects deleted in the draft or missing → `config.reference.missing`, deleted objects still
  referenced by live objects not in the draft → `config.reference.in_use` (lists referrers).
- Licence check hook (LIC, M7): projected device units after publish → error if over quota.

## 6. Editing paths (T3.01)

### 6.1 Draft API (configuration side of the desktop client)

| Method and path | Description |
|---|---|
| `GET /api/v1/drafts?mine=true` / `POST /api/v1/drafts` `{ name }` | List / create |
| `GET /api/v1/drafts/{id}` | Draft with entries summary and problems |
| `PUT /api/v1/drafts/{id}/entries` | Body `{ ref, operation, baseVersion?, content? }` → entry + problems |
| `DELETE /api/v1/drafts/{id}/entries/{entryId}` | Revert one object |
| `GET /api/v1/drafts/{id}/objects/{type}/{objectId}` | The object as seen in the draft (draft content or live) — editors read through this so they show draft state |
| `GET /api/v1/drafts/{id}/diff` | Per-entry diff vs live (§7.4 format) |
| `POST /api/v1/drafts/{id}/validate` | Force full validation (job for large drafts) |
| `POST /api/v1/drafts/{id}/publish` `{ comment, publishAt? }` | Publish now / schedule / request approval (depending on settings) |
| `POST /api/v1/drafts/{id}/approve` / `reject` `{ reason }` | Approvers |
| `DELETE /api/v1/drafts/{id}` | Discard |

All draft endpoints are `ConfigurationEndpoint()` (desktop only) with the object types' permissions.

Live topic `drafts` (registered through API `ILiveTopic`): filter `{ "ids": [...] }` (drafts the
user owns or collaborates on, or awaiting the user's approval); snapshot = draft summary
`{ id, name, status, entryCount, errors, warnings, version }`; deltas on entry changes,
validation completion, status changes (publishing, published with version, failed).

### 6.2 Reading live configuration

Module read endpoints (REG, ALM…) stay unchanged and return live objects. The UI shows draft
overlays by reading through `/drafts/{id}/objects/...` when a draft is active.

### 6.3 Secrets in drafts

Write-only fields (connection passwords, provider secrets) are submitted in the entry content as
`{ "$secret": "new value" }`; CFG stores them **encrypted in the draft** (SEC vault, owner
`Configuration`, entity = draft id) and replaces them by `{ "$secretRef": "<id>" }`; at publish
the owning module moves the value to its own secret (owner = module/entity) and the draft secret
is deleted. Draft secrets are deleted when the draft is discarded or published. Sending `{ "$secretRef": "<id>" }` back keeps the secret;
replacing an entry without it deletes the secret; a `$secret` that is not text is `config.secret.invalid` and nothing is stored.

### 6.4 Quick edits

Direct module endpoints (e.g. `PATCH /devices/{id}`, `POST /alarm-rules`) and UI "apply now"
actions call `QuickEdit.ApplyAsync(entries, actor, comment)`: create a hidden draft, validate,
publish immediately (approval rules still apply — if approval is required for the type, the quick
edit returns `202` with the draft id awaiting approval; errors in the changes return `422` with the problems and publish nothing). The hidden
draft is not listed; editing and publishing permissions of every touched type are required.

## 7. Publish and versions (T3.02)

### 7.1 Publish algorithm

1. Lock the draft (status `Publishing`); take the **publish lock** (one publish at a time per
   site: `pg_advisory_xact_lock` in the configuration database).
2. Re-validate the whole draft against current live state; any error → status back to `Open`,
   return problems (`config.publish.invalid`).
3. Concurrency check: each `Update`/`Delete` entry's `BaseVersion` equals the live version; else
   `config.publish.conflict` with the list of conflicting objects (UI offers merge §8).
4. Open one connection + transaction on the configuration database; for each module in
   module-catalogue order, call `PrepareApplyAsync` with its entries; build the **inverse change
   set** (from `Before` documents) and the diff.
5. Insert `wp_config.versions` row (next version number, author, comment, draft id, summary,
   inverse JSON, diff JSON) and `wp_config.version_entries` rows; commit.
6. After commit: `CompleteApplyAsync` per module (in-memory swaps), append `config.published`
   to `WP_CFG` `{ version, draftId, changes: [{ type, id, operation, newVersion }] }`, audit record,
   system event `configuration.published`, draft status `Published`.
7. Failure in step 4–5 → rollback transaction, draft `Failed` with error, nothing applied.

Atomicity relies on all configuration stores being in the **same database** (STO default; the
store mapping keeps Configuration in one database by design).

### 7.2 Propagation to other nodes (M9)

Nodes other than the publisher consume `config.published` from WP_CFG and call each module's
`ReloadAsync(changes)` (part of `IConfigurableObjectType`, default: reload the listed objects from
the database) — same in-memory effect as `CompleteApplyAsync`.

### 7.3 Versions and rollback

```csharp
public interface IVersionService
{
    Task<Page<ConfigVersion>> ListAsync(VersionQuery query, PageRequest page, CancellationToken ct);
    Task<Result<ConfigVersionDetail>> GetAsync(long version, CancellationToken ct);      // entries + diff
    Task<Result<DraftId>> CreateRollbackDraftAsync(long version, UserId actor, CancellationToken ct);  // inverse of version v (and of later versions if requested)
    Task<Result<JsonElement>> GetObjectAtVersionAsync(ConfigObjectRef obj, long version, CancellationToken ct);
}
```

- Rollback creates a **draft** containing the inverse entries of the chosen version (or of all
  versions after a point — "restore to version v"); it is validated (objects may have changed
  since) and published like any draft.
- Versions are kept forever by default (they are small); `Configuration:VersionRetention` can
  limit to N days (default unlimited).

### 7.4 Diff format

Per entry: `{ ref, operation, changes: [{ path, before, after }] }` computed by a JSON structural
diff (objects by key, arrays by index or by `id` field when items have one); the UI renders it
with translated field labels from the object type's schema.

## 8. Concurrency and merge (T3.02)

- Conflict at publish (or when the user opens an object in a draft whose base is outdated —
  warning `config.entry.outdated`): `ThreeWayMerge(base, mine, theirs)` computes a merged document
  and a list of conflicting paths (same path changed differently). Non-conflicting changes merge
  automatically; conflicting paths are presented to the user ("keep mine / keep theirs" per
  field). Resolving updates the entry with `BaseVersion = theirs.Version`.
- Endpoint `POST /api/v1/drafts/{id}/entries/{entryId}/rebase` → `{ merged, conflicts[] }`;
  `PUT …/entries` with the resolved content.

## 9. Soft locks (T3.03)

```csharp
public interface ILockService
{
    Task<Result<SoftLock>> AcquireAsync(ConfigObjectRef obj, UserId user, CancellationToken ct);   // 30 min, renewable
    Task RenewAsync(Guid lockId, CancellationToken ct);
    Task ReleaseAsync(Guid lockId, CancellationToken ct);
    SoftLock? Get(ConfigObjectRef obj);
    Task<Result> BreakAsync(ConfigObjectRef obj, UserId admin, string reason, CancellationToken ct);  // permission config.locks.break
}
```

Only for types with `SupportsSoftLock`. A lock does not prevent others from viewing or from
adding the object to their drafts in read-only mode; editors refuse edits without the lock
(`config.lock.held_by`, args `user`, `since`). Live topic `config` broadcasts lock changes.

## 10. Import and export (T3.03)

### 10.1 Bundle (JSON)

```json
{ "format": "watchpoint.config-bundle", "version": 1, "exportedAt": "…", "site": { "id": "…", "name": "…" },
  "objects": [ { "type": "registry.location", "id": "…", "version": 3, "content": { … } }, … ] }
```

- Export: selection by types and ids, or "everything under location X" (with dependencies:
  devices, links, rules scoped there, synoptics…). Secrets are never exported (fields exported as
  `{ "$secretRequired": true }`).
- Import: creates a draft. Modes: **same site** (ids kept: updates existing objects) or **other
  site** (new ids generated; references re-mapped; name-based matching for objects that must
  exist in the target (device types, calendars by key, tags by key) with a mapping report). A
  dry-run report lists creates, updates, conflicts, unmapped references, secrets to provide.

### 10.2 Tabular (CSV/XLSX)

For `Tabular` types (devices, locations, users, tags): export with columns defined by the type's
`TabularMapping` (localised headers + a hidden header row with field keys), import back with
validation per row (problems refer to row numbers). XLSX via ClosedXML; CSV UTF-8 with BOM, `;`
or `,` delimiter auto-detected.

## 11. Data (`wp_config`)

| Table | Columns |
|---|---|
| `drafts` | id, name jsonb, owner, collaborators uuid[], status, publish_at, approval jsonb, created_at, updated_at, row_version |
| `draft_entries` | id, draft_id, type, object_id, operation, base_version, content jsonb, updated_at — unique (draft_id, type, object_id) |
| `draft_problems` | draft_id, entry_id null, severity, code, args jsonb, path, fixes jsonb (rewritten on each validation) |
| `versions` | version bigint PK (sequence), published_at, author, comment, draft_id, summary jsonb (counts per type/op), inverse jsonb, diff jsonb |
| `version_entries` | version, type, object_id, operation, new_version, before jsonb, after jsonb — PK (version, type, object_id) |
| `locks` | id, type, object_id (unique pair), user_id, acquired_at, expires_at |
| `settings` | approval rules (types requiring approval), version retention |

## 12. Settings, errors, observability

Settings: `Configuration:ApprovalRequiredFor` (list of object types; default empty),
`Configuration:VersionRetention`, `Configuration:MaxDraftEntries` (20,000).

Errors: `config.draft.not_found`, `config.draft.not_open`, `config.draft.too_large`,
`config.entry.invalid`, `config.entry.outdated` (warning), `config.reference.missing`,
`config.reference.in_use`, `config.publish.invalid`, `config.publish.conflict`,
`config.publish.in_progress`, `config.approval.required`, `config.approval.self_forbidden`,
`config.lock.held_by`, `config.lock.not_supported`, `config.import.invalid_bundle`,
`config.import.unmapped_reference`, `config.version.not_found`, `config.licence.quota_exceeded`, `config.secret.invalid`, the fix codes
`config.fix.remove_field`, `config.fix.set_default`, `config.fix.set_nearest`, and `api.client_kind.not_allowed` (§6.1, API §5.3:
`ConfigurationClientRule`). Problems also use the `common.value.*` codes of the schema checks. Every code has a text `errors.<code>`
in en, fr and ar (CFG-U-018).

| EventId | Level | Message |
|---|---|---|
| 1301 | Information | Draft {DraftId} published as version {Version} by {UserId}: {Summary} |
| 1302 | Warning | Publish of draft {DraftId} rejected: {Code} |
| 1303 | Error | Publish of draft {DraftId} failed and was rolled back: {Error} |
| 1304 | Error | CompleteApply failed in module {Module} for version {Version} (in-memory state will be reloaded) |
| 1310 | Information | Rollback draft {DraftId} created from version {Version} |

On `CompleteApply` failure the module is asked to reload the affected objects from the database
(self-healing), and a system event is raised.

Metrics: `watchpoint.config.publish.duration`, `watchpoint.config.publishes` (counter by outcome),
`watchpoint.config.drafts.open` (gauge).

## 13. Tests

| Id | Layer | Test |
|---|---|---|
| CFG-U-001 | U | Object type registration and lookup |
| CFG-U-002 | U | Create draft, add/replace/remove entries keeping BaseVersion; two editors with the same version: one wins, the other gets `common.version.conflict` (also under concurrency) |
| CFG-U-003 | U | Draft size limit |
| CFG-U-004 | U | Schema validation errors as problems with JSON pointer paths |
| CFG-U-005 | U | Module validator problems aggregated with object refs |
| CFG-U-006 | U | Permission per object type and object scope (RGT fake) |
| CFG-U-007 | U | Reference to object deleted in the same draft → missing |
| CFG-U-008 | U | Deleting live object still referenced → in_use with referrers |
| CFG-U-009 | U | Re-validation of dependent entries on change |
| CFG-U-010 | U | Large draft validation runs as job with progress |
| CFG-U-011 | U | Fix suggestions carry JSON patches; applying patch fixes the problem |
| CFG-U-012 | U | Draft overlay reads (draft content vs live) |
| CFG-U-013 | U | Secrets in drafts stored in vault and replaced by refs |
| CFG-U-014 | U | Draft secrets deleted on discard |
| CFG-U-015 | U | Quick edit: draft + publish in one step (publish through the `IPublishService` seam; fake) |
| CFG-U-016 | U | Quick edit with approval-required type → 202 awaiting approval |
| CFG-U-017 | U | Collaborators need same permissions |
| CFG-U-018 | U | Plain-language problem keys exist in en/fr/ar for every CFG and module code (scan) |
| CFG-U-019 | U | Licence hook projected quota error (fake) |
| CFG-U-020 | U | Draft endpoints refuse non-desktop clients |
| CFG-U-021 | U | REG site and location: schema, parent reference, kinds |
| CFG-U-022 | U | REG tag family and tag |
| CFG-U-023 | U | REG server: connection, location and tag references |
| CFG-U-024 | U | REG device: server, parent (in the draft), location, tags |
| CFG-U-025 | U | REG link and custom field |
| CFG-U-026 | U | SCH holiday set and calendar (holiday set references) |
| CFG-U-027 | U | IRT connection and IDN user |
| CFG-U-028 | U | HIS policy override |
| CFG-U-029 | U | Catalogue of module types: unique, ordered, permissions, references name registered types, live load and list through the source |
| CFG-U-030 | U | Change set order (catalogue order, deletes reversed), live configuration untouched, invalid or closed drafts give none |
| (T3.02) | U | `PrepareApply`/`CompleteApply`/`Reload` of the module adapters (validate/prepare/complete with fakes; ALM and RGT types with their modules) are covered by CFG-U-031, 034, 036 |
| CFG-U-031 | U | Publish runs modules in catalogue order inside one transaction |
| CFG-U-032 | U | Revalidation at publish catches live changes since last validation |
| CFG-U-033 | U | BaseVersion mismatch → conflict listing objects |
| CFG-U-034 | U | Failure in PrepareApply rolls back all modules |
| CFG-U-035 | U | Version row with inverse and diff |
| CFG-U-036 | U | CompleteApply after commit; failure triggers reload and system event |
| CFG-U-037 | U | `config.published` record contents |
| CFG-U-038 | U | Publish lock serialises concurrent publishes |
| CFG-U-039 | U | Scheduled publish fires at time (SCH trigger) |
| CFG-U-040 | U | Approval: request, approve (≠ author), reject with reason |
| CFG-U-041 | U | Rollback draft = inverse of version; validates against current state |
| CFG-U-042 | U | Restore to version v (inverse of all later versions) |
| CFG-U-043 | U | Object at version v |
| CFG-U-044 | U | Diff format arrays by id |
| CFG-U-045 | U | Three-way merge: non-conflicting auto-merge |
| CFG-U-046 | U | Three-way merge: conflicting paths reported |
| CFG-U-047 | U | Rebase endpoint updates BaseVersion |
| CFG-U-048 | U | Outdated entry warning |
| CFG-U-049 | U | Audit record per publish |
| CFG-U-050 | U | Reload on other node from `config.published` (M9 path, unit with fake) |
| CFG-U-051 | U | Soft lock acquire, renew, expire, release |
| CFG-U-052 | U | Edit without lock refused; held-by error args |
| CFG-U-053 | U | Break lock with permission and reason |
| CFG-U-054 | U | Locks broadcast on live topic |
| CFG-U-055 | U | Bundle export with dependencies under a location |
| CFG-U-056 | U | Secrets never exported |
| CFG-U-057 | U | Import same site → updates with ids kept |
| CFG-U-058 | U | Import other site → new ids, references remapped |
| CFG-U-059 | U | Name-based mapping for types/calendars/tags; unmapped reported |
| CFG-U-060 | U | Dry-run report counts |
| CFG-U-061 | U | Tabular export localised headers + key row |
| CFG-U-062 | U | Tabular import per-row problems with row numbers |
| CFG-U-063 | U | CSV delimiter detection and BOM |
| CFG-U-064 | U | XLSX round-trip |
| CFG-U-065 | U | Import creates a draft, nothing live until publish |
| CFG-U-066…070 | U | Large import (10,000 devices) performance: draft creation < 30 s, validation job < 60 s |
| CFG-I-001 | I | Publish across REG + ALM + IRT in one transaction with real PostgreSQL |
| CFG-I-002 | I | Crash between commit and CompleteApply → restart loads committed state |
| CFG-I-003 | I | Concurrent publishes serialised |
| CFG-I-004 | I | Conflict and merge with two configurators |
| CFG-I-005 | I | Rollback end-to-end restores previous rule and device |
| CFG-I-006 | I | Scheduled publish end-to-end |
| CFG-I-007 | I | Quick edit via `PATCH /devices/{id}` creates version |
| CFG-I-008 | I | Approval end-to-end |

## 14. Tasks

| Task | Sections | Tests |
|---|---|---|
| T3.01 | §4–§6 | CFG-U-001…CFG-U-030 |
| T3.02 | §7–§8 | CFG-U-031…CFG-U-050, CFG-I-001…CFG-I-008 |
| T3.03 | §9–§10 | CFG-U-051…CFG-U-070 |
