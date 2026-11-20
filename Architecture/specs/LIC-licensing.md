# LIC — Licensing (in the product)

| | |
|---|---|
| Spec id | LIC |
| Projects | `src/service/Watchpoint.Licensing`, `src/service/Watchpoint.Licensing.Format` (shared with the portal: document model, canonical serialisation, signature verification), `tools/wphostid` (container host-id generator) |
| Milestone | M7 |
| Tasks | T7.01 (§4–§7), T7.02 (§8–§11) |
| Event id range | 3400–3499 |
| Design docs | [Licensing](../../docs/modules/licensing.md) |

## 1. Purpose and scope

- Licence file model, signature verification, footprints of every node, tolerance matching.
- Quotas and usage: device units by licence class, seats, mobile users, nodes, remote hosts,
  integration feature keys and options.
- Activation, modification, re-host and renewal requests (online and offline files), licence
  import, online check-in for online-validated licences.
- Enforcement that **never stops supervision**: grace periods, restricted mode, hooks into CFG,
  IDN, IRT, HIS, CLU; banners; UI panel.

Not in scope: the company's portal and issuance (LPT).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, STO (`wp_licensing`), SEC (node identity key for signing request files; installation id), REG (device counts), MOD (licence class per type), IDN (sessions, mobile named users), IRT (packages feature keys, hosts count), CLU (node list, M9), AUD, NOT (notifications to configurators), API (system topic banners), NSec (Ed25519, MIT), System.Management (MIT, Windows WMI) | CFG (publish quota check), IDN (`ISessionAdmission` seats), IRT (connection creation for unlicensed packages), HIS (restricted mode history limit), SCR/FED/RPT (option checks), UIF (licence panel), LPT (shares `Licensing.Format`) |

## 3. Layout

Log event ids: 3400–3499 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Licensing.Format/            (no dependencies beyond Kernel; referenced by the portal)
├── LicenceDocument.cs, LicenceRequestDocument.cs, Footprint.cs, FootprintComponent.cs
├── CanonicalJson.cs (RFC 8785 JCS), Signatures.cs (Ed25519 sign/verify), EmbeddedKeys.cs (public keys + ids)
└── FootprintMatcher.cs (tolerance rule)
Watchpoint.Licensing/
├── Contracts/ILicenceService.cs, LicenceStatus.cs, Usage.cs, ILicenceGate.cs, LicensingErrors.cs
├── Internal/
│   ├── Footprints/IFootprintCollector.cs, WindowsFootprintCollector.cs, LinuxFootprintCollector.cs, ContainerHostId.cs, VirtualisationDetector.cs
│   ├── Validation/LicenceValidator.cs, ClockGuard.cs, ValidationScheduler.cs
│   ├── Usage/UsageCalculator.cs (units, seats, features), Forecast.cs (draft projection)
│   ├── Requests/RequestBuilder.cs, PortalClient.cs, RequestFileWriter.cs (.wprq), RequestPoller.cs
│   ├── Activation/LicenceImporter.cs, CheckInService.cs (online-validated)
│   ├── Enforcement/EnforcementState.cs, Gates/ (CfgQuotaGate, SeatAdmission, FeatureGate, HistoryWindowGate, NodeGate)
│   ├── Trial/TrialService.cs
│   └── Storage/LicensingDbContext.cs
├── LicensingModule.cs, LicensingLog.cs
```

## 4. Licence document (T7.01)

`.wplic` file = UTF-8 JSON:

```jsonc
{
  "format": "watchpoint-licence/1",
  "payload": {
    "licenceId": "LIC-2026-000123", "version": 3, "issuedAt": "2026-11-03T10:00:00Z",
    "customer": { "id": "C-0456", "name": "Example Bank" }, "partner": { "id": "P-0012", "name": "…" },
    "site": { "name": "Head office", "installationId": "0192…" },
    "type": "hardwareBound",                       // hardwareBound | onlineValidated
    "nodes": [ { "nodeId": "…", "footprint": { "v": 1, "components": { "board": "…hash…", "cpu": "…", "os": "…", "disk": "…", "nic": "…" } } } ],
    "model": { "kind": "perpetual", "supportUntil": "2027-11-03" },   // perpetual | subscription {termEnd} | trial | demo | nfr | developer {expiresAt}
    "edition": "professional",
    "quotas": { "deviceUnits": 500, "desktopSeats": 8, "webSeats": 5, "mobileUsers": 10, "nodes": 2, "remoteHosts": 5 },
    "features": ["int.salto", "int.milestone", "int.spc", "opt.scripting", "opt.reports.scheduled"],
    "constraints": { "maxVersion": null, "countries": null }
  },
  "signature": { "alg": "Ed25519", "keyId": "lic-2026-1", "value": "base64…" }
}
```

- Signature over `CanonicalJson(payload)` (RFC 8785). Public keys embedded in
  `Licensing.Format` (`EmbeddedKeys`, with ids and validity periods); unknown key id →
  `licensing.licence.unknown_key`. The developer build additionally trusts a test key **only** when
  compiled with `WATCHPOINT_DEV_KEYS` (never in release builds; CI check).
- Unknown payload fields are ignored (forward compatibility); `format` major version must be 1.
- Edition defaults (limits, included seats/integrations, options) are a table in
  `Licensing.Format/Editions.cs`; explicit quotas in the payload override them.

## 5. Footprints (T7.01)

### 5.1 Components

Each component value is normalised (trimmed, upper-case for hex identifiers) and hashed:
`hash = hex(SHA-256("wp-fp-v1|" + componentName + "|" + value))[0..32]`. Raw values never leave
the node or appear in logs.

| Component | Windows | Linux | Rules |
|---|---|---|---|
| `board` | WMI `Win32_ComputerSystemProduct.UUID` (fallback `Win32_BaseBoard.SerialNumber`) | `/sys/class/dmi/id/product_uuid` (root-only → captured by the installer into `/var/lib/watchpoint/hostinfo.json`, mode 0644, refreshed by the service's root-run helper at upgrade) | ignore all-zero/`FFFFFFFF…`/`To be filled by O.E.M.` values (component absent) |
| `cpu` | `Win32_Processor.Name` + `ProcessorId` | `/proc/cpuinfo` `model name` + `cpu family/model/stepping` | |
| `os` | `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` | `/etc/machine-id` | |
| `disk` | Serial of the physical disk hosting the system volume (`Win32_DiskDrive` via partition association) | serial of the block device holding `/` (`/sys/block/<dev>/device/serial`, or `ID_SERIAL` from `/run/udev/data`) | |
| `nic` | MAC of the **primary NIC**: physical (not virtual/VPN/loopback/Hyper-V/Docker), Ethernet or Wi-Fi, lowest numeric MAC among those | same rule with `/sys/class/net/*/device` present | |

Context (not matched): OS name/version, Watchpoint version, node role, virtualisation kind
(`none`, `hyperv`, `vmware`, `kvm`, `xen`, `virtualbox`, `container`; from SMBIOS
manufacturer/product and `/proc/1/cgroup`/`/.dockerenv`).

### 5.2 Matching

`FootprintMatcher.Match(licensed, current)`: match if **≥ 3 of the 5** components are equal
(absent components count as not equal); returns matched components (shown in UI as "4/5
components match").

### 5.3 Containers

- `tools/wphostid` (run on the container host, root) generates `/etc/watchpoint/hostid`
  containing `{ hostId (random 128-bit), board/os/nic hashes of the host }`; mounted read-only
  into the container at `/run/watchpoint/hostid`.
- In a container, components = host-id file values (board, os, nic from the host) + `cpu` from
  the container view + `disk` replaced by `hostId` (component name `disk` reused) — so the same
  3-of-5 rule applies.
- No host-id file in a container → footprint invalid (`licensing.footprint.container_without_hostid`);
  only online-validated licences work there.

## 6. Usage and quotas (T7.01)

`UsageCalculator` (recomputed on REG/IDN/IRT change events, debounced 5 s):

| Quota | Counting rule |
|---|---|
| `deviceUnits` | Σ over devices that are **enabled and not orphaned** of the type's licence class weight (full 1, light 0.1, free 0) — decimal, rounded up to 0.1 for display; servers free |
| `desktopSeats` / `webSeats` | Concurrent sessions by client kind (IDN registry; a desktop session counts once even with several windows) |
| `mobileUsers` | Distinct users with a mobile session or registered device in the last 30 days (named-user licensing) |
| `nodes` | Cluster members (1 before M9) |
| `remoteHosts` | Paired remote hosts (IHO) that are enabled |
| features | Package `licence.class`: `included` → always allowed; `standard`/`advanced` → requires `int.<key>` feature in the licence (`licence.featureKey` from the manifest); options `opt.*` checked by modules through `ILicenceGate.IsAllowed("opt.scripting")` |

```csharp
public interface ILicenceGate
{
    LicenceStatus Status { get; }                                    // cached, updated by validation
    bool IsAllowed(string feature);                                  // feature or option
    QuotaCheck CheckQuota(string quota, decimal projected);          // projected = after change
    event Action<LicenceStatus>? Changed;
}
```

**Forecast**: CFG publish hook computes projected units = current − units of devices
disabled/deleted in the draft + units of devices created/enabled → `CheckQuota`.

## 7. Validation (T7.01)

At start (phase `Edge`, before connections start in phase `Connections`, HST §5.1) and every 10 min, plus on licence import:

1. Load the current licence (DB `licences` current row; replicated in M9).
2. Signature, format, key validity at `issuedAt`.
3. Footprint: the local node's footprint must match one entry in `nodes` (`onlineValidated`: the
   installation id must equal `site.installationId` instead).
4. Dates: model expiry/term; `supportUntil` vs the running version's release date (perpetual
   without SUP can run versions released before `supportUntil`; newer versions →
   `licensing.support.expired_for_version` → treated like expiry with grace, and DEP refuses
   to install such versions in M11).
5. Quotas vs usage (overuse states).
6. **Clock guard**: persist the maximum observed UTC time (signed by node key, DB + file);
   current time earlier than max − 24 h → `clockRollback` flag (grace then restricted);
   moving forward is always accepted.
7. Compute `LicenceStatus` (§10) and publish `licensing.statusChanged` on WP_SYS when it changes.

## 8. Requests (T7.02)

```csharp
public enum RequestKind { Activation, Modification, Rehost, Renewal }
public sealed record LicenceRequestDocument(string Format /* "watchpoint-licence-request/1" */, Guid RequestId, RequestKind Kind,
    string? ActivationCode /* order */, string? CurrentLicenceId, SiteInfo Site /* name, installationId, timezone, country */,
    IReadOnlyList<NodeFootprint> Nodes, UsageSnapshot Usage, string WatchpointVersion, DateTimeOffset CreatedAt,
    string? Message /* free text to the licensing team */);
```

- Built from all nodes' footprints (single node before M9; in M9 collected from every member via
  the cluster control channel). No personal data (customer contacts come from the portal
  account, not the site).
- **Online**: `POST {Licensing:PortalUrl}/api/v1/requests` over HTTPS (system trust store, pinned
  portal public key hash as extra check), body = request document; response `{ requestId,
  status }`. The node polls `GET …/requests/{id}` every 15 min (and on check-in) until `issued`
  (licence downloaded → §9), `rejected` (message shown, configurators notified) or
  `needsCorrection`.
- **Offline**: file `.wprq` = `{ request, signature }` signed with the node identity key (SEC) so
  the portal can verify integrity and link the request to the installation's public key
  (included). Downloaded from the UI; uploaded by a person to the portal.
- Request history stored (`requests` table) with status; audit `licence.requested`.

## 9. Import, activation, check-in (T7.02)

- Import (`POST /api/v1/licensing/licence` with `.wplic`, or automatic from the portal): validate
  (§7 steps 2–5 against the current node set) → on success store as current (previous kept in
  history), audit `licence.activated`, notify configurators, `licensing.statusChanged`.
  Invalid licence → refused with the precise reason (signature, footprint "2/5 components match on
  node X", expired, site/installation mismatch); never replaces a valid current licence.
- **Online-validated**: `CheckInService` every 24 h → `POST …/api/v1/checkin { installationId,
  licenceId, version, usage, nodePublicKey, signature }`; response may carry a newer licence
  (renewal, modification) or a `revoked`/`duplicate` flag. Valid for 30 days without a successful
  check-in, then restricted mode (grace banners from day 23).
- **Trial** (`TrialService`): on first start without licence, a trial record (start time,
  installation id, signed by node key) is created: Professional features, 50 units, 2
  integrations, 30 days; watermark banner. A second trial on the same installation is refused;
  removing the record (DB reset) is detected by the clock-guard file and the node key history →
  `licensing.trial.already_used`.

## 10. Enforcement (T7.02)

```csharp
public sealed record LicenceStatus(LicenceState State /* Trial | Licensed | Grace | Restricted | Unlicensed */,
    IReadOnlyList<LicenceIssue> Issues /* kind, since, graceUntil, details */, LicenceSummary? Licence, Usage Usage, DateTimeOffset EvaluatedAt);
public enum LicenceIssueKind { QuotaExceeded, TemporaryOveruse, SupportExpired, TermExpired, OnlineValidationOverdue, FootprintMismatch,
    NodeNotLicensed, Missing, Tampered, ClockRollback, FeatureMissing }
```

| Issue | Grace | Effect during grace | Effect after grace (Restricted) |
|---|---|---|---|
| QuotaExceeded (projected) | — | Publish refused (`licensing.quota.exceeded` with numbers) | — |
| TemporaryOveruse (usage > quota without publish, e.g. failover in M9, devices re-enabled by import) | 30 days | Banner | Restricted |
| SupportExpired | — | Info banner; newer versions refused by DEP | — |
| TermExpired / trial / demo expired | 30 days | Daily banner + notification | Restricted |
| OnlineValidationOverdue | until day 30 | Banner from day 23 | Restricted |
| FootprintMismatch (local node) | 14 days | Banner, full mode | Restricted |
| NodeNotLicensed (cluster member, M9) | 14 days | Node standby-only (CLU) | Node refused from cluster |
| Missing / Tampered | none (after trial) | — | Restricted |
| ClockRollback | 3 days | Banner | Restricted |
| FeatureMissing (package/option) | — | New connections of the package and option use refused (`licensing.feature.missing`); existing connections keep running with a banner (they were licensed before or under trial) | — |

**Restricted mode** (`ILicenceGate.Status.State == Restricted`): supervision continues (connections,
events, states, alarms, notifications, commands, live video); **blocked**: CFG publish (except
the licence object and maintenance/system essentials: `config.licence`, users' password resets),
history queries older than 7 days (HIS gate → `licensing.restricted.history`), report schedules
(paused), new connections; permanent banner. Exiting restricted mode is immediate on a valid
licence import.

Gates are implemented as hooks registered in other modules' extension points: CFG publish
validator (`IPublishValidator`), IDN `ISessionAdmission` (seats: over seat count → new session of
that kind refused `licensing.seats.exhausted` with the list of sessions to sign out — **never**
closes existing sessions; administrators may sign in over quota with one extra "emergency" seat
per kind), IRT `IConnectionAdmission` (feature keys), HIS `IQueryWindowPolicy`.

## 11. UI panel and API (T7.02)

UIF page *Configuration → System → Licence* (`config.licence`):

- Status card (state, issues with remaining grace days, licence id/version, model, dates,
  edition), nodes table (footprint match x/5 per node), features list, usage vs quotas (units by
  class and by category, seats in use by kind, mobile users, remote hosts) with a draft forecast
  line ("publishing the current draft adds 12.4 units → 503.4 / 500").
- Actions: request activation/modification/rehost/renewal (form: kind, activation code,
  message) → online submit or "save request file"; import licence file (drag-and-drop); request
  history with statuses.
- Banners in both sides of the desktop client and web client via live `system` topic
  (`licence { state, issues }`).

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/licensing/status` | authenticated (summary) / `config.licence` (details) | Status, usage |
| `GET /api/v1/licensing/footprints` | `config.licence` | Hashed footprints per node + match |
| `POST /api/v1/licensing/requests` | `config.licence` | Create request; `submit: online|file` → JSON status or `.wprq` download |
| `GET /api/v1/licensing/requests` | `config.licence` | History |
| `POST /api/v1/licensing/licence` | `config.licence` | Import `.wplic` |
| `GET /api/v1/licensing/licences` | `config.licence` | Licence history |

`config.licence` added to the RGT catalogue (configuration family, None scope).

## 12. Data, settings, errors, observability

Tables `wp_licensing`: `licences` (id, licence_id, version, document jsonb, imported_at,
imported_by, current bool), `requests` (id, kind, document jsonb, status, portal_request_id,
submitted_at, updated_at, message), `trial` (installation_id, started_at, signature),
`clock_guard` (max_seen_utc, signature), `status_history` (at, state, issues jsonb).

| Key | Default |
|---|---|
| `Licensing:PortalUrl` | `https://licensing.watchpoint.example` (placeholder until the company domain exists) |
| `Licensing:PortalKeyPin` | SHA-256 of the portal TLS public key (shipped) |
| `Licensing:ValidationInterval` | 10 min |
| `Licensing:RequestPollInterval` | 15 min |

Errors: `licensing.licence.invalid_signature`, `licensing.licence.unknown_key`,
`licensing.licence.format_unsupported`, `licensing.licence.footprint_mismatch` (args node,
matched), `licensing.licence.installation_mismatch`, `licensing.licence.expired`,
`licensing.quota.exceeded` (args quota, used, projected, limit), `licensing.seats.exhausted`,
`licensing.feature.missing` (args feature), `licensing.restricted.history`,
`licensing.trial.already_used`, `licensing.footprint.container_without_hostid`,
`licensing.portal.unreachable`, `licensing.request.rejected`.

| EventId | Level | Message |
|---|---|---|
| 3400 | Information | Licence {LicenceId} v{Version} valid: {Edition}, {Units} units, state {State} |
| 3401 | Warning | Licence issue {Kind}: grace until {GraceUntil} |
| 3402 | Error | Restricted mode entered: {Issues} |
| 3410 | Information | Licence request {RequestId} ({Kind}) submitted {Mode} |
| 3411 | Information | Licence {LicenceId} v{Version} imported by {User} |
| 3420 | Warning | Clock rollback detected: now {Now}, max seen {MaxSeen} |

Metrics: `watchpoint.licensing.units.used` / `.limit` (gauges), `watchpoint.licensing.seats.used`
(gauge by kind), `watchpoint.licensing.state` (gauge enum).

## 13. Tests

| Id | Test |
|---|---|
| LIC-U-001 | Canonical JSON (RFC 8785 test vectors) |
| LIC-U-002 | Signature verify valid/tampered payload/unknown key/expired key id |
| LIC-U-003 | Dev keys only with build flag (assembly attribute check) |
| LIC-U-004 | Unknown payload fields ignored; format major version check |
| LIC-U-005 | Edition defaults and payload overrides |
| LIC-U-006…015 | Footprint collectors (Windows and Linux) with fake WMI/sysfs providers: each component, invalid values ignored, primary NIC rule (virtual/VPN/Docker excluded, lowest MAC), system disk detection, hostinfo.json usage on Linux |
| LIC-U-016 | Hashing normalisation and prefix; raw values never logged (log capture) |
| LIC-U-017 | Matching 3-of-5 tolerance incl. absent components |
| LIC-U-018 | Virtualisation detection fixtures |
| LIC-U-019 | Container host-id file usage; missing file error |
| LIC-U-020 | `wphostid` output format |
| LIC-U-021…028 | Usage: units by class (enabled, not orphaned, servers free, decimal rounding), seats by kind, mobile named users 30-day window, nodes, remote hosts, features by package class, options |
| LIC-U-029 | Forecast from draft entries |
| LIC-U-030 | Usage recompute debounce on change events |
| LIC-U-031…036 | Validation steps: footprint per node, installation id for online-validated, dates per model, supportUntil vs version release date, quotas, status change publication |
| LIC-U-037…040 | Clock guard: persistence signed, rollback detection threshold, forward accepted, tampered guard file |
| LIC-U-041…045 | Validation scheduling at start before connections, every 10 min, on import; status caching; ILicenceGate API |
| LIC-U-046 | Request document contents (no personal data), kinds |
| LIC-U-047 | Online submission with pinned key; portal unreachable error |
| LIC-U-048 | Request polling until issued/rejected/needsCorrection with notifications |
| LIC-U-049 | Offline `.wprq` signed by node key; verification sample |
| LIC-U-050 | Request history and audit |
| LIC-U-051…055 | Import: success replaces current and keeps history; each refusal reason (signature, footprint x/5 node, expired, installation mismatch); never replaces valid licence with invalid |
| LIC-U-056…058 | Check-in: schedule, newer licence applied, revoked/duplicate flags, 30-day tolerance and day-23 banners |
| LIC-U-059…061 | Trial: creation, limits and watermark, reuse refused (DB reset detection) |
| LIC-U-062…070 | Enforcement table: each issue kind → grace and effects (FakeTimeProvider), restricted mode entry and exit |
| LIC-U-071 | CFG quota gate refuses publish with numbers; licence object still publishable in restricted mode |
| LIC-U-072 | Seat admission: refuses new sessions over quota, never closes existing, emergency admin seat |
| LIC-U-073 | Feature gate: new connections of unlicensed packages refused; existing keep running with banner |
| LIC-U-074 | HIS window gate in restricted mode |
| LIC-U-075 | Report schedules paused in restricted mode |
| LIC-U-076 | Banners payload on system topic |
| LIC-U-077 | API permissions and responses |
| LIC-U-078 | UI panel: status, nodes match, usage and forecast rendering |
| LIC-U-079 | UI: request form online/file and import drag-and-drop |
| LIC-U-080 | Translations coverage en/fr/ar |
| LIC-I-001 | Real footprint collection on CI Windows and Linux runners (values stable across two runs) |
| LIC-I-002 | Footprint in Docker with mounted host-id |
| LIC-I-003 | Online activation round-trip against the LPT test portal (docker compose): request → approve in portal API → licence auto-imported |
| LIC-I-004 | Offline round-trip with files |
| LIC-I-005 | Restricted mode end-to-end: alarms still raised/acknowledged, configuration publish refused, history > 7 days refused |
| LIC-I-006 | Seat exhaustion with real sessions (desktop/web) |

## 14. Tasks

| Task | Sections | Tests |
|---|---|---|
| T7.01 | §4–§7 | LIC-U-001…LIC-U-045 |
| T7.02 | §8–§12 | LIC-U-046…LIC-U-080, LIC-I-001…LIC-I-006 |
