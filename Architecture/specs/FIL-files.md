# FIL — File Store

| | |
|---|---|
| Spec id | FIL |
| Projects | `src/service/Watchpoint.Files` |
| Milestone | M5 |
| Tasks | T5.03 |
| Event id range | 2300–2349 (Files/Scheduling range; SCH uses 2350–2399) |
| Design docs | [System architecture §2](../../docs/architecture/system-architecture.md#2-data-stores-and-what-lives-where), [Data storage](../../docs/modules/data-storage.md) |

## 1. Purpose and scope

One place for **binary files** used by modules: synoptic plans and images (SYN), symbols,
alarm snapshots and video export clips (VID), report outputs (RPT), event/audit exports (EVT,
AUD), integration packages uploaded through the API (IRT), client update packages (DEP),
attachments to alarms and notes, diagnostics bundles.

- Content-addressed blob storage (SHA-256) on the local disk with a **replication interface**
  (implemented in M9 by CLU).
- File metadata, **references** from owning entities, garbage collection, retention, quotas per
  category.
- Upload (simple and resumable chunked), download (range, ETag), short-lived signed URLs for
  browsers, validation and scanning hooks.

Not in scope: databases (STO), the event log (PIP), package cache folders (IRT keeps its own
unpacked cache, but uploaded `.wpkg` files go through FIL first).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST (paths), STO (`wp_files` metadata), SEC (URL signing key), AUD (downloads of sensitive categories), API (endpoints, jobs) | SYN, VID, RPT, EVT/AUD exports, IRT uploads, DEP, ALM attachments, HLT diagnostics, CLU (replication) |

## 3. Layout

Log event ids: 2300–2349 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Files/
├── Contracts/
│   ├── FileId.cs, FileInfo.cs, FileCategory.cs, FileRef.cs, IFileStore.cs, IFileReferences.cs
│   ├── IFileValidator.cs, IFileScanner.cs, IFileReplicator.cs
│   └── FileErrors.cs
├── Internal/
│   ├── Blobs/LocalBlobStore.cs (content-addressed), BlobPath.cs
│   ├── FileStore.cs, Uploads/UploadSessionService.cs, Signing/UrlSigner.cs
│   ├── Gc/FileGarbageCollector.cs, Retention/FileRetentionJob.cs, Quotas/QuotaService.cs
│   ├── Scanning/ClamdScanner.cs (optional), NoopScanner.cs
│   ├── Validation/ContentSniffer.cs, ImageValidator.cs, SvgSanitizer.cs (own allow-list sanitiser, §6.4), PdfValidator.cs
│   └── Storage/FilesDbContext.cs
├── FilesModule.cs
└── FilesLog.cs
```

## 4. Contracts

```csharp
public readonly record struct FileId(Guid Value);   // UUIDv7, Kernel typed id pattern

public sealed record FileCategory(string Code /* "synoptic.plan" */, string OwnerModule, long MaxFileBytes,
    IReadOnlySet<string> AllowedContentTypes, TimeSpan? DefaultRetention /* null = while referenced */,
    bool Sensitive /* downloads audited, signed URLs ≤ 60 s */, bool Replicated /* default true */, long? QuotaBytes);

public sealed record FileInfo(FileId Id, string Name, string ContentType, long Size, string Sha256, FileCategory Category,
    UserId? CreatedBy, string? CreatedByModule, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, string> Metadata /* width/height for images, pages for PDF, ≤ 20 entries */, FileState State);
public enum FileState { Pending /* uploading or scanning */, Available, Quarantined, Deleted }

public interface IFileStore
{
    Task<Result<FileInfo>> PutAsync(NewFile file, Stream content, CancellationToken ct);           // validates, scans, stores
    Task<Result<FileInfo>> GetInfoAsync(FileId id, CancellationToken ct);
    Task<Result<FileContent>> OpenAsync(FileId id, ByteRange? range, CancellationToken ct);       // stream + info
    Task<Result> DeleteAsync(FileId id, string reason, CancellationToken ct);                      // soft delete; refs must be gone or force
    Task<Result<FileInfo>> CopyAsync(FileId id, FileCategory target, string? newName, CancellationToken ct);  // dedup: same blob
    string CreateSignedUrl(FileId id, TimeSpan lifetime, string? disposition = null);              // /api/v1/files/{id}/content?exp=&sig=
}
public sealed record NewFile(string Name, string? ContentType, FileCategory Category, UserId? CreatedBy, string? CreatedByModule,
    TimeSpan? Retention = null, IReadOnlyDictionary<string, string>? Metadata = null, FileRef? InitialReference = null);

public sealed record FileRef(FileId File, string OwnerModule, string EntityType, Guid EntityId, string? Role /* "background", "snapshot" */);

public interface IFileReferences
{
    Task AddAsync(FileRef reference, CancellationToken ct);
    Task RemoveAsync(FileRef reference, CancellationToken ct);
    Task ReplaceForEntityAsync(string ownerModule, string entityType, Guid entityId, IReadOnlyList<FileRef> references,
        DbConnection? connection, DbTransaction? transaction, CancellationToken ct);   // used inside CFG publish transactions
    Task<IReadOnlyList<FileRef>> GetAsync(FileId file, CancellationToken ct);
}

public interface IFileValidator { string Category { get; } ValueTask<Result<ValidatedFile>> ValidateAsync(Stream content, NewFile file, CancellationToken ct); }
public interface IFileScanner { ValueTask<ScanResult> ScanAsync(Stream content, CancellationToken ct); }  // Clean | Infected(name) | Error
public interface IFileReplicator                                   // M9 (CLU); single node: NoopReplicator
{
    ValueTask OnStoredAsync(FileInfo file, CancellationToken ct);  // replicate blob to peers
    ValueTask<Stream?> FetchMissingAsync(string sha256, CancellationToken ct);  // blob absent locally → get from a peer
}
```

Categories are registered by modules at start-up (phase `Catalogue`) — a file can only be
stored in a registered category. v1 categories:

| Code | Owner | Max size | Content types | Retention | Sensitive |
|---|---|---|---|---|---|
| `synoptic.plan` | SYN | 50 MB | image/png, image/jpeg, image/svg+xml, application/pdf | while referenced | no |
| `synoptic.symbol` | SYN | 1 MB | image/svg+xml, image/png | while referenced | no |
| `video.snapshot` | VID | 5 MB | image/jpeg, image/png | 90 days | yes |
| `video.export` | VID | 4 GB | video/mp4, application/zip | 30 days | yes |
| `report.output` | RPT | 200 MB | application/pdf, xlsx, text/csv, text/html | 90 days | yes |
| `export.events`, `export.audit` | EVT, AUD | 2 GB | text/csv, xlsx | 7 days | yes |
| `alarm.attachment` | ALM | 20 MB | images, application/pdf | with the alarm history retention | yes |
| `integration.package` | IRT | 500 MB | application/vnd.watchpoint.package (`.wpkg`) | 1 day (IRT moves it to the package folder) | no |
| `client.update` | DEP | 1 GB | application/octet-stream | while referenced | no |
| `diagnostics.bundle` | HLT | 2 GB | application/zip | 7 days | yes |
| `config.import` | CFG | 100 MB | application/json, text/csv, xlsx | 1 day | no |

## 5. Storage

- Blobs at `Paths:Files` (default `<Data>/files/`) as `blobs/<aa>/<bb>/<sha256>` (first two
  byte pairs as folders). Written to `tmp/<uuid>` first, fsync, hash verified, atomic rename.
  Identical content is stored once (reference counting by metadata rows).
- Metadata in `wp_files` (§9). A blob is deleted when no `files` row with state ≠ Deleted points
  to it and the GC grace period passed.
- Integrity: a weekly job re-hashes a sample (1 % or 10 GB, whichever smaller) of blobs; mismatch →
  `files.integrity.mismatch` health warning, file quarantined, replicator asked for a good copy
  (M9).
- Disk space: before accepting uploads, free space must stay above `Files:MinFreeBytes`
  (default 5 GB) → `files.disk.full` (503-like Unavailable).

## 6. Upload and download

### 6.1 API

| Method and path | Permission | Description |
|---|---|---|
| `POST /api/v1/files?category=` (multipart, single part ≤ 100 MB) | per category (owner module decides: e.g. `config.synoptics.edit` for plans) | Simple upload → `FileInfo` |
| `POST /api/v1/files/uploads` `{ name, category, size, sha256? }` | same | Create resumable session (24 h) → `{ uploadId, chunkSize: 8 MiB }` |
| `PUT /api/v1/files/uploads/{id}` with `Content-Range: bytes a-b/total` | same | Upload a chunk (idempotent per range) |
| `GET /api/v1/files/uploads/{id}` | same | Received ranges (resume) |
| `POST /api/v1/files/uploads/{id}/complete` | same | Assemble, verify sha256, validate, scan → `FileInfo` (job for > 500 MB) |
| `GET /api/v1/files/{id}` | category view permission | Metadata |
| `GET /api/v1/files/{id}/content` | same, or valid signature | Download; `Range` supported (206), `ETag: "<sha256>"`, `If-None-Match` (304), `Content-Disposition` attachment by default, `inline` only for images/PDF in allowed categories; `X-Content-Type-Options: nosniff`; SVG served with `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'` |
| `DELETE /api/v1/files/{id}` | owner module rule | Soft delete (refused if referenced: `files.file.referenced`) |

Permission checks are delegated: each category registers an `IFileAccessPolicy` (`CanUpload`,
`CanRead(file, user)`, `CanDelete`) — e.g. VID checks `video.export` + device scope of the clip.

### 6.2 Signed URLs

`CreateSignedUrl`: `exp` (unix seconds) + `sig` = base64url(HMAC-SHA256(key, fileId|exp|disposition))
with the SEC key purpose `files-url` (rotated monthly, previous key accepted until expiry).
Lifetime ≤ 15 min (≤ 60 s for sensitive categories). Used by clients for `<img>` tags (plans,
snapshots). E-mails (NOT, RPT) never carry signed URLs: they attach the file or link to the web
client page, which requests a signed URL with the user's own session.

### 6.3 Validation and scanning

1. Size ≤ category max; declared content type in allowed set.
2. `ContentSniffer`: magic bytes must match the declared type (PNG, JPEG, PDF, ZIP/XLSX, MP4,
   SVG/XML, JSON, CSV text) → mismatch `files.content.type_mismatch`.
3. Category validator (`IFileValidator`): images decoded header-only (dimensions ≤ 30,000 px,
   decompression-bomb guard: width × height ≤ 400 MP); SVG sanitised (§6.4); PDF: header and
   xref sanity, JavaScript/launch actions rejected; packages: deferred to IRT signature check.
4. Scanner (`Files:Scanner = none | clamd`, `clamd` = ClamAV daemon over TCP INSTREAM, free and
   external): infected → state Quarantined, upload refused `files.content.infected`, audit
   `files.quarantined`; scanner error → policy `Files:ScannerFailurePolicy` (`reject` default for
   uploads by users, `accept` for module-produced files).

### 6.4 SVG sanitiser

Allow-list parser (XmlReader with DTD processing prohibited, no external entities): elements
`svg, g, defs, symbol, use (local refs only), path, rect, circle, ellipse, line, polyline,
polygon, text, tspan, title, desc, linearGradient, radialGradient, stop, clipPath, mask,
pattern, image (data: URIs of PNG/JPEG only), style (CSS without url(), @import, expression)`;
attributes geometry/presentation only; all `on*` attributes, `javascript:`, external `href`,
`foreignObject`, `script` removed. The sanitised SVG is what is stored (original discarded);
metadata `sanitised=true` and counts of removed items.

## 7. References, GC and retention

- Files created with `InitialReference` or referenced later; configuration documents (SYN
  synoptics) call `ReplaceForEntityAsync` inside their CFG publish transaction, so references
  follow published configuration exactly (drafts reference files as `draft` owner entries that are
  replaced at publish or dropped on discard).
- **GC** (hourly): files with no references, older than `Files:UnreferencedGrace` (default 24 h),
  and no retention → soft-deleted; soft-deleted rows older than 7 days purged with their blob if
  unshared.
- **Retention**: files with `ExpiresAt` passed are soft-deleted even if referenced (the
  reference becomes dangling; owners handle missing files — e.g. an alarm snapshot shows "expired").
- Quotas: per category `QuotaBytes` (site settings) → uploads refused `files.quota.exceeded`;
  usage exposed in HLT.

## 8. Replication interface (implemented in M9)

`FileStore` calls `IFileReplicator.OnStoredAsync` after storing; `OpenAsync` calls
`FetchMissingAsync` when metadata exists but the blob is missing locally (another node stored it).
Single node: `NoopReplicator` (missing blob → `files.blob.missing`, health error).

## 9. Data (`wp_files`)

| Table | Columns |
|---|---|
| `files` | id, name, content_type, size, sha256 char(64), category, created_by, created_by_module, created_at, expires_at, metadata jsonb, state smallint, deleted_at — index sha256, (category, created_at), expires_at |
| `file_refs` | file_id, owner_module, entity_type, entity_id, role — PK (file_id, owner_module, entity_type, entity_id, role); index (owner_module, entity_type, entity_id) |
| `upload_sessions` | id, name, category, size, sha256, created_by, created_at, expires_at, received jsonb (ranges), tmp_path |

## 10. Settings, errors

| Key | Default |
|---|---|
| `Paths:Files` | `<Data>/files` |
| `Files:MinFreeBytes` | 5 GB |
| `Files:UnreferencedGrace` | 24 h |
| `Files:Scanner` / `ScannerAddress` / `ScannerFailurePolicy` | none / `localhost:3310` / reject |
| `Files:Quotas:<category>` | per category table |

Errors: `files.file.not_found`, `files.file.referenced`, `files.category.unknown`,
`files.size.too_large`, `files.content.type_not_allowed`, `files.content.type_mismatch`,
`files.content.invalid` (validator), `files.content.infected`, `files.quota.exceeded`,
`files.disk.full`, `files.upload.not_found`, `files.upload.range_invalid`,
`files.upload.hash_mismatch`, `files.url.invalid_signature`, `files.url.expired`,
`files.blob.missing`.

## 11. Observability

| EventId | Level | Message |
|---|---|---|
| 2300 | Debug | File {FileId} stored ({Category}, {Size} bytes, dedup {Dedup}) |
| 2301 | Warning | Upload rejected: {Reason} |
| 2310 | Warning | File {FileId} quarantined: {Threat} |
| 2320 | Information | GC: {Deleted} files soft-deleted, {Purged} blobs purged, {Freed} bytes freed |
| 2330 | Error | Blob integrity mismatch for {Sha256} |
| 2331 | Error | Disk free space below minimum ({Free} bytes) |

Metrics: `watchpoint.files.bytes` (gauge per category), `watchpoint.files.uploads` (counter,
result), `watchpoint.files.downloads` (counter, category).

## 12. Tests

| Id | Layer | Test |
|---|---|---|
| FIL-U-001 | U | Put: write tmp, fsync, hash, atomic rename; dedup of identical content |
| FIL-U-002 | U | Category registration; unknown category refused |
| FIL-U-003 | U | Size and content type limits |
| FIL-U-004 | U | Content sniffing for each supported type; mismatch refused |
| FIL-U-005 | U | Image validator dimensions and decompression-bomb guard |
| FIL-U-006 | U | SVG sanitiser: removes script, on*, foreignObject, external href, javascript: URLs, CSS url()/@import; keeps allowed geometry; DTD/XXE prohibited (OWASP test vectors) |
| FIL-U-007 | U | PDF validator rejects JavaScript/launch actions |
| FIL-U-008 | U | Scanner: infected → quarantined + audit; error policy reject/accept |
| FIL-U-009 | U | Resumable upload: ranges idempotent, resume listing, completion hash check, expiry |
| FIL-U-010 | U | Download ranges (206), ETag/304, disposition rules, nosniff, SVG CSP header |
| FIL-U-011 | U | Signed URL creation/verification, expiry, key rotation overlap, sensitive lifetime cap |
| FIL-U-012 | U | Access policy delegation per category |
| FIL-U-013 | U | References add/remove/replace within a transaction (rollback leaves refs unchanged) |
| FIL-U-014 | U | GC: unreferenced after grace soft-deleted; shared blobs kept; purge after 7 days |
| FIL-U-015 | U | Retention expiry deletes even if referenced |
| FIL-U-016 | U | Quotas per category |
| FIL-U-017 | U | Disk free space guard |
| FIL-U-018 | U | Integrity sampling detects tampered blob → quarantine + health |
| FIL-U-019 | U | Copy reuses blob |
| FIL-U-020 | U | Missing blob → replicator fetch (fake) / blob.missing with Noop |
| FIL-I-001 | I | Upload 1 GB resumable with interruption and resume over HTTP |
| FIL-I-002 | I | Concurrent identical uploads deduplicated without corruption |
| FIL-I-003 | I | Crash during write leaves no partial blob (tmp cleaned at start) |
| FIL-I-004 | I | ClamAV container (`clamav/clamav`) detects EICAR test file |
| FIL-I-005 | I | CFG publish with SYN-like references then rollback: references follow versions |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T5.03 | all | FIL-U-001…FIL-U-020, FIL-I-001…FIL-I-005 |
