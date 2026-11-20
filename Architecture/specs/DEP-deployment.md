# DEP — Deployment: Installers, Setup Wizard, Updates, Backups

| | |
|---|---|
| Spec id | DEP |
| Projects | `deploy/windows` (Inno Setup scripts), `deploy/linux` (nfpm configs, systemd units), `deploy/containers` (Dockerfiles, compose), `deploy/helm/watchpoint`, `src/ui/apps/desktop` packaging (electron-builder), `src/service/Watchpoint.Setup` (first-run wizard backend), `src/ui/apps/setup` (wizard UI), `src/service/Watchpoint.Updates`, `src/service/Watchpoint.Backup`, `tools/wpctl` |
| Milestone | M11 |
| Tasks | T11.03 (§4–§6), T11.04 (§7), T11.05 (§8–§10) |
| Event id range | 1150–1199 (Host range; HST uses 1100–1149) |
| Design docs | [Deployment and operations](../../docs/operations/deployment-and-operations.md), [Security](../../docs/operations/security.md) |

## 1. Purpose and scope

Everything needed to **ship and run** Watchpoint: platform installers and images, the first-run
setup wizard, signed updates of nodes/clients/packages with rollback, backups and restore, and
the `wpctl` command-line tool.

## 2. Dependencies and tools (all free for a closed-source product)

| Tool | Use |
|---|---|
| Inno Setup 6 (free licence) | Windows server/remote host/witness setup |
| nfpm (MIT) | `.deb` / `.rpm` packages; APT/YUM repositories generated with `reprepro`-like scripts (aptly, MIT) and `createrepo_c` (GPL tool used at build time only) |
| Docker/Buildx, OCI images (distroless-style base `mcr.microsoft.com/dotnet/runtime-deps` + non-root user) | Containers |
| Helm 3 (Apache-2.0) | Kubernetes chart |
| electron-builder (MIT) | Desktop client NSIS, MSIX, AppImage, deb/rpm, dmg (notarised) |
| Windows code signing (Authenticode, customer-trusted certificate bought by the company) + `signtool`; macOS notarisation (Apple developer account) | Signing of executables (paid certificates are unavoidable for OS trust; noted as cost in technology-stack §3) |
| minisign-compatible Ed25519 signatures (own implementation with NSec) | Update packages and repository metadata |
| pgBackRest (MIT) | PostgreSQL backups/PITR |
| CycloneDX tooling (Apache-2.0) | SBOM (T11.06) |

## 3. Layout of an installation

| Path (Windows / Linux) | Content |
|---|---|
| `C:\Program Files\Watchpoint\` / `/opt/watchpoint/` | Binaries: service, `wpctl`, integration host (x64, x86), script host, `nats-server`, `go2rtc`, LGPL `ffmpeg`, `chrome-headless-shell`, UI bundles (`ui/desktop`, `ui/web`, `ui/setup`), builtin integration packages, fonts, maps (optional) |
| `C:\ProgramData\Watchpoint\` / `/var/lib/watchpoint/` | Data: settings (`appsettings.Production.json`), secrets (master key file or DPAPI blob), NATS store, files store, logs, packages, backups (default), hostinfo.json |
| Service | Windows service `Watchpoint` (virtual account `NT SERVICE\Watchpoint`) / systemd `watchpoint.service` (user `watchpoint`, hardening: `ProtectSystem=strict`, `ReadWritePaths=/var/lib/watchpoint`, `NoNewPrivileges`, `CapabilityBoundingSet=CAP_NET_BIND_SERVICE`) |

## 4. Server installers (T11.03)

### 4.1 Windows setup (Inno Setup)

- One signed `WatchpointSetup-<version>-x64.exe`; components: **Watchpoint node** (default),
  **Bundled PostgreSQL 17 + TimescaleDB** (optional, installs the official PostgreSQL binaries
  as service `watchpoint-postgresql` on port 5433 with generated superuser password stored in
  the secrets vault), **Remote integration host only**, **Witness only**.
- Pages: licence agreement (EULA), components, install/data folders, ports (HTTPS 443/8443,
  NATS cluster 6222 when clustering, WebRTC 8555), firewall rules (created via `netsh advfirewall`
  for selected ports), summary.
- Silent install: `/VERYSILENT /SUPPRESSMSGBOXES /COMPONENTS=node,postgres /CONFIG=<file.json>`
  for GPO/Intune; config file schema `deploy/windows/install-config.schema.json`.
- Upgrade in place: detects existing installation, stops the service, replaces binaries (keeps
  data), runs `wpctl upgrade --post-install` (migrations + health check, §8.2), restarts.
- Uninstall keeps data unless "remove data" is checked (double confirmation).
- Every installer (Windows, Linux packages, OCI images, Helm) writes `Node:Environment = Production`;
  no installer option or wizard step sets `Development` (HST §12, S-031).

### 4.2 Linux packages (nfpm)

- `watchpoint_<version>_amd64.deb` / `.rpm` (x86_64; arm64 builds for Debian/Ubuntu):
  installs to `/opt/watchpoint`, creates user, systemd unit, `postinst` writes
  `/var/lib/watchpoint/hostinfo.json` (DMI product UUID, for LIC), sets `net.ipv4.ping_group_range`
  for the service group (C-PING), enables but does not start the service until setup.
- Dependencies: none mandatory (self-contained .NET); recommended `postgresql-17` +
  `timescaledb-2-postgresql-17` from official repositories (documented commands; the setup wizard
  can use a local or remote database).
- Repositories: signed APT and YUM repositories hosted by the company (`repo.watchpoint…`),
  GPG keys published; also offline packages for air-gapped sites.

### 4.3 Containers and Kubernetes

- Images: `watchpoint/node`, `watchpoint/integration-host`, `watchpoint/witness`; non-root,
  read-only root filesystem, `/data` volume, healthchecks on `/health/ready`; configuration by
  environment variables (`WATCHPOINT__…` mapping to settings) and secrets files
  (`WATCHPOINT_MASTER_KEY_FILE`).
- `docker-compose.yml` reference stack: node + PostgreSQL/TimescaleDB + optional witness.
- Helm chart `watchpoint`: StatefulSet (stable network identities for NATS routes), headless
  service, PVCs, ConfigMap/Secret, PodDisruptionBudget, anti-affinity across nodes, optional
  Ingress for web clients (WebRTC needs host ports or a UDP LoadBalancer — documented); licence
  type online-validated recommended (LIC §5.3).

### 4.4 Remote integration host and witness

Small setups/packages/images containing only the integration host or witness, with pairing
commands (IHO §8, CLU §4).

## 5. Desktop client installers (T11.03)

electron-builder targets: Windows NSIS (per-machine, silent `/S`), **MSIX** (Intune/Store
optional), Linux AppImage + deb + rpm, macOS dmg (signed, notarised, universal). Installer
options: preconfigured site profile (`--site-url`, CA fingerprint) via a config file next to
the installer for mass deployment. Auto-update from the site (§8.3), not from the internet.

## 6. Build and release pipeline (T11.03)

- GitHub Actions workflow `release.yml`: build (Windows, Linux, macOS runners) → tests gates →
  package → sign (Authenticode via a cloud HSM/signing service; Ed25519 update signatures by the
  release signing key held in a protected environment with required reviewers) → SBOM (T11.06) →
  publish artefacts to the release storage and repositories → release notes.
- Reproducibility: pinned SDK/toolchain versions, `Deterministic` builds, lockfiles (NuGet
  lock, pnpm lock); third-party binaries (NATS, go2rtc, FFmpeg LGPL, chrome-headless-shell,
  PostgreSQL) downloaded by checksum-verified scripts (`deploy/thirdparty/*.lock.json`).
- DEP-I tests run installers on clean VMs/containers per platform (§11).

## 7. Setup wizard (T11.04)

Served at `https://localhost:8443/setup/` only from the local machine (loopback check) until
setup completes, or through the first desktop client connection (setup mode token printed in
the console/log and shown by `wpctl setup token`).

| Step | Content |
|---|---|
| 1 Language, site | Language (en/fr/ar), site name, time zone (IANA, detected), country |
| 2 Database | Bundled PostgreSQL (if installed) or external: provider (PostgreSQL, SQL Server), host/port/db/credentials/TLS, **test** → capability report (version, TimescaleDB, edition limits, SQL Server Express warning and suggestion to map History to PostgreSQL), store mapping (advanced) |
| 3 Certificates | Generate site CA + node certificate (default) or import (PFX/PEM chain) |
| 4 Administrator | Login, password (policy meter), e-mail; MFA enrolment (TOTP QR) recommended |
| 5 Licence | Start trial, request activation (online/offline file), or import `.wplic` |
| 6 Deployment shape | Single node / join existing cluster (join token) / prepare for cluster (witness instructions) |
| 7 Summary | Checks (database, NATS, certificates, ports, disk, clock), apply → service restarts into normal mode; link to desktop client download |

Backend `Watchpoint.Setup`: state machine persisted in `<data>/setup/state.json` (resumable),
each step validated server-side; completion writes settings, creates DB schemas (migration
runner), secrets, admin user, licence state; then disables setup endpoints permanently
(`setup.completed` marker). Re-running setup is only possible with `wpctl setup reset` on the
machine (destroys configuration after confirmation).

## 8. Signed updates (T11.05)

### 8.1 Package format

`watchpoint-<component>-<version>-<platform>.wpup` = zip with `manifest.json` (component: node |
desktop | integration-host | witness | packages-bundle; version; platform; min previous version;
files with SHA-256; migration notes; release date) and `signature.sig` (Ed25519 over the
canonical manifest, release key id). Public release keys embedded in the product (rotation like
LIC keys).

### 8.2 Node updates

- Sources: OS package managers (APT/YUM), Windows setup, or **upload in the client** (*System →
  Updates*, `config.system.updates`) / `wpctl update apply <file.wpup>`.
- Checks: signature, platform, version ordering, licence support window (LIC: perpetual without
  SUP cannot install versions released after `supportUntil` → refused with explanation),
  cluster compatibility (CLU §10).
- Single node flow: pre-update **snapshot** (configuration DB backup + settings + secrets, §9),
  stop service, replace binaries side-by-side (`/opt/watchpoint/versions/<v>/` with a `current`
  symlink / Windows folder switch), start, run migrations, health check (ready within 5 min and
  `HLT` overall not Error for 2 min) → success; failure → **automatic rollback**: stop, restore
  snapshot (database restore only if migrations ran), switch back binaries, start; event and
  notification.
- Cluster: orchestrated rolling update (drain → update → health → undrain) per node from the UI
  (`/api/v1/updates/rollouts`), pausing on failure.

### 8.3 Desktop client updates

- Nodes host client update packages (`client.update` FIL category) per platform; on sign-in the
  shell sends its version; the node answers with the recommended/required version and URL;
  the shell downloads, verifies the Ed25519 signature **and** the OS code signature, installs
  (electron-builder updater configured with a custom provider pointing at the node), restarts at
  a safe moment (no Critical alarm open in console; operator prompt), with deferral per
  workstation group (admin setting).
- Compatibility: nodes accept clients of version N-1 minor during rolling upgrades.

### 8.4 Integration packages and bundles

`packages-bundle` updates install new builtin package versions into the package catalogue (IRT;
connection upgrades follow IRT §11 canary/rollback).

## 9. Backups and restore (T11.05)

| Item | PostgreSQL | SQL Server |
|---|---|---|
| Full backup | `pg_dump -Fc` per database (small/medium) or **pgBackRest** full/diff (large, PITR optional) | `BACKUP DATABASE … WITH COMPRESSION, CHECKSUM` (customer DBA may manage instead — Watchpoint then only backs up files/secrets/settings) |
| Files store | Archive of `files/blobs` (incremental by content hash) |
| Secrets | Master key **not** included; secrets DB rows are encrypted; backup bundle includes the wrapped data keys and requires the master key or a **recovery passphrase** (set in setup, derived key wraps master key copy) to restore |
| Settings, certificates | Included (private keys encrypted with the master key) |

- Schedules (`system.backup` CFG document): daily at 02:00 site time, keep 14, targets local
  folder, network share (SMB/NFS path), or S3-compatible storage (credentials in SEC); backup
  bundle `watchpoint-backup-<site>-<timestamp>.wpbk` = manifest + parts, SHA-256 per part, signed
  by the node key.
- Before every publish: configuration snapshot (CFG versions already provide logical rollback;
  the snapshot is a DB-level safety net, 30 days).
- Restore: `wpctl restore <file.wpbk> [--to-new-machine]` (service stopped; validates
  signature/checksums; restores DB, files, settings; new machine → footprint changes → LIC
  re-host request prepared), or restore wizard in the desktop client for configuration-only
  restores.
- HLT reports last backup age and failures; backup failures produce system events.

## 10. `wpctl` (T11.05)

Single binary (.NET AOT where possible) talking to the local node's management API (loopback
port with machine-local token file readable by administrators/root) or working offline on the
data folder.

| Command | Description |
|---|---|
| `wpctl status` | Node state, version, roles, cluster summary, health overall |
| `wpctl logs [--follow] [--level] [--since]` | Tail logs |
| `wpctl diag [--from --to] [--out]` | Diagnostic bundle (HLT) |
| `wpctl backup [--target]` / `wpctl restore <file>` | §9 |
| `wpctl update apply <file.wpup>` / `wpctl update status` | §8 |
| `wpctl reset-admin` | Bootstrap admin recovery (IDN; local root/administrator only, audited, forces password change) |
| `wpctl maintenance on|off [--reason --until]` | MNT |
| `wpctl cluster status|join <addr> <token>|drain|undrain` | CLU |
| `wpctl cert renew|show` | SEC |
| `wpctl storage migrate|cleanup|status` | STO-MS |
| `wpctl setup token|reset` | §7 |
| `wpctl secrets rewrap|export-recovery` | SEC master key operations |

Exit codes: 0 ok, 1 error, 2 usage, 3 not permitted, 75 temporary failure (retry).

## 11. Tests

| Id | Test |
|---|---|
| DEP-U-001…010 | Setup wizard backend: step validation, persistence/resume, loopback-only access, setup token, database test and capability report (fakes), Express warning and mapping suggestion |
| DEP-U-011…015 | Certificates step: generate vs import (PFX/PEM chain validation) |
| DEP-U-016…018 | Admin step password policy and MFA enrolment |
| DEP-U-019…020 | Completion: settings/secrets/schemas/admin/licence state; setup endpoints disabled; reset command |
| DEP-U-021…026 | Update package format: manifest canonicalisation, signature verification, key rotation, platform/version checks, LIC support window refusal, cluster compatibility |
| DEP-U-027…032 | Node update flow state machine: snapshot, binary switch, migrations, health gate, success, automatic rollback paths |
| DEP-U-033…036 | Rolling rollout orchestration: per-node sequence, pause on failure, resume |
| DEP-U-037…040 | Desktop update protocol: version negotiation, signature checks, safe-moment restart rule, deferral per workstation group |
| DEP-U-041…046 | Backups: schedule, targets (local, SMB path, S3 fake), bundle manifest/signature, retention, recovery passphrase wrapping, failure events |
| DEP-U-047…050 | `wpctl`: command parsing, management API auth via token file, exit codes, offline commands on data folder |
| DEP-I-001 | Windows setup silent install on a clean Windows Server VM (CI runner): service running, health ready |
| DEP-I-002 | Windows setup with bundled PostgreSQL component |
| DEP-I-003 | Windows upgrade in place keeps data |
| DEP-I-004 | Windows uninstall keeps / removes data per option |
| DEP-I-005 | .deb install on Ubuntu 24.04 container with systemd; hardening directives effective (`systemd-analyze security` score threshold) |
| DEP-I-006 | .rpm install on Rocky 9 |
| DEP-I-007 | APT/YUM repository metadata signatures valid |
| DEP-I-008 | Container image runs non-root with read-only rootfs; compose stack healthy |
| DEP-I-009 | Helm chart install on kind (Kubernetes in Docker): 2 replicas + witness form a cluster |
| DEP-I-010 | Remote integration host package pairs with a node |
| DEP-I-011 | Witness package joins a cluster |
| DEP-I-012 | Desktop NSIS silent install with preconfigured site |
| DEP-I-013 | Desktop AppImage/deb launch smoke (xvfb) |
| DEP-I-014 | macOS dmg notarisation check (weekly) |
| DEP-I-015 | Third-party binaries checksums verified in the build |
| DEP-I-016 | Node update success path on Linux and Windows |
| DEP-I-017 | Node update with a broken build (health fails) → automatic rollback restores service and data |
| DEP-I-018 | Rolling update of a 2-node cluster without lost events |
| DEP-I-019 | Desktop client auto-update from node with signature verification; tampered package refused |
| DEP-I-020 | Backup and restore on the same machine (PostgreSQL) |
| DEP-I-021 | Restore to a new machine → re-host request prepared |
| DEP-I-022 | Backup to S3-compatible storage (MinIO container, AGPL server used only in tests) |
| DEP-I-023 | SQL Server site: files/secrets/settings backup with DBA-managed database |
| DEP-I-024 | `wpctl reset-admin` audited and forces password change |
| DEP-I-025 | `wpctl` cluster and maintenance commands end-to-end |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T11.03 | §3–§6 | DEP-I-001…DEP-I-015 |
| T11.04 | §7 | DEP-U-001…DEP-U-020 |
| T11.05 | §8–§10 | DEP-U-021…DEP-U-050, DEP-I-016…DEP-I-025 |
