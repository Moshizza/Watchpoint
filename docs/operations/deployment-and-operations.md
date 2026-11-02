# Deployment and Operations

## 1. Installation

### 1.1 Packages

| Platform | Artefact | Contents |
|---|---|---|
| Windows Server / Windows 10–11 | Signed **MSI** (WiX) | Watchpoint service, bundled NATS and go2rtc, `wpctl` tool, optional PostgreSQL + TimescaleDB installation, firewall rules, Windows service registration |
| Ubuntu/Debian | Signed **.deb** + APT repository | Service with systemd unit, helpers, `wpctl`; PostgreSQL/Timescale from official repositories as dependencies or external |
| RHEL/Rocky/Alma | Signed **.rpm** + YUM repository | Same |
| Containers | OCI images (`watchpoint`, `watchpoint-integration-host`), **Docker Compose** file (Watchpoint + PostgreSQL/Timescale), **Helm chart** for Kubernetes | Same software, configured by environment variables and mounted volumes |
| Remote integration host | Small MSI / .deb / image | Host only, pairs with a cluster |
| Desktop client | MSI/MSIX, .deb/.rpm/AppImage, .dmg | See [Desktop client](../clients/desktop-client.md) |

### 1.2 First-run wizard

After installing, the first desktop client connection (or a local web page on
`https://localhost:8443/setup`, available only from the machine itself until setup is done)
runs the setup wizard:

1. Language and site name, time zone.
2. Database: use the bundled one or connect to an existing PostgreSQL (connection test,
   TimescaleDB check).
3. Certificates: generate site CA (default) or import customer certificate.
4. Administrator account (password + MFA enrolment recommended).
5. Licence: start trial, or request activation (online/offline), or import a licence file.
6. Deployment shape: single node now (can grow later), or join an existing cluster with a
   pairing code.
7. Summary and health check.

### 1.3 Adding nodes

From *System → Cluster → Add node*: generates a one-time pairing code; install Watchpoint on
the new machine, enter the code; the node receives certificates, master key (through mTLS
enrolment), configuration, packages; the licence modification request is prepared
automatically.

## 2. Upgrades

- **Single node**: the update package (signed) is installed by the MSI/package manager or
  from the client (*System → Updates*, upload package). The service stops, migrations run,
  service starts, health check; automatic rollback (restore pre-upgrade snapshot) if the
  health check fails.
- **Cluster**: rolling upgrade one node at a time: drain (move leases), upgrade, health check,
  rejoin. Database migrations are designed **expand/contract** so mixed versions run during
  the upgrade window.
- **Clients** update themselves from the nodes (signed packages).
- **Integration packages** upgrade per connection with canary and rollback (see
  [Integrations](../modules/integrations.md#7-upgrades-of-packages)).
- Version policy: semantic versioning; LTS releases yearly with 3 years of fixes; feature
  releases quarterly.

## 3. Backups

| What | How | Default |
|---|---|---|
| Configuration snapshot | Before every publish | 30 days |
| Full backup (database + files + encrypted secrets) | Scheduled (pg_dump/pgBackRest + files) to local folder and/or network share/S3 | Daily, keep 14 |
| History archive | Optional chunk export (Parquet) before retention drop | Off |
| Point-in-time recovery | WAL archiving (pgBackRest) | Off (recommended for Enterprise) |

Restore: `wpctl restore <file>` or the client's restore wizard; restoring into a new machine
triggers a licence re-host request.

## 4. Monitoring and diagnostics

- Built-in **System → Health** dashboard (see [Core runtime §6](../modules/core-runtime.md#6-system-health)).
- OpenTelemetry metrics and traces; Prometheus endpoint; optional Grafana dashboards shipped
  as JSON for customers who use Grafana.
- Logs: rolling files per component (JSON lines), OS event log/journald integration, optional
  forwarding (syslog/OTLP) to a SIEM.
- **Diagnostic bundle** (`wpctl diag` or client button): versions, configuration summary,
  health, recent logs, metrics — redacted, listed before export.
- `wpctl` commands: `status`, `logs`, `diag`, `backup`, `restore`, `reset-admin`,
  `maintenance on|off`, `cluster status`, `cert renew`.

## 5. Operations runbooks (to be written with the product)

- Replace a failed node; restore from backup; move to new hardware (licence re-host).
- Rotate certificates and secrets; recover a lost administrator.
- Database disk full; history lag; NATS cluster member lost.
- Connection keeps faulting: collect protocol trace.
- Upgrade a cluster; roll back an upgrade.
