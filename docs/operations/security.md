# Security

> Watchpoint controls doors, alarms and cameras. Its own security is a product feature.
> Target references: OWASP ASVS level 2 for the APIs and clients, IEC 62443-4-1/4-2
> practices for the product lifecycle and components (formal certification later),
> GDPR-compliant handling of personal data.

## Principles

1. Secure by default: a fresh install is encrypted, authenticated and audited without extra
   configuration.
2. Least privilege everywhere: users, script identities, federation identities, database
   roles, service accounts, processes.
3. No secret in clear: not in configuration, logs, command lines, URLs or diagnostic files.
4. Everything that executes is signed: service, clients, integration packages, updates.
5. Defence in depth: rights checked server-side per object, network segmentation guidance,
   process isolation for integrations and scripts.

## Transport security

| Link | Protection |
|---|---|
| Clients ↔ nodes | HTTPS/WSS, TLS 1.2+ (1.3 preferred); certificate from the customer's PKI, from ACME (Let's Encrypt) for internet-facing sites, or from the **site CA** generated at installation |
| Node ↔ node, node ↔ NATS, node ↔ remote integration hosts, federation links | **mTLS** with certificates issued by the site CA; automatic renewal; revocation on node removal |
| Node ↔ PostgreSQL | TLS + password/certificate authentication; database roles per purpose (service, migrations, read-only reporting) |
| Connections ↔ integrated systems | As secure as the vendor allows; TLS validation never disabled silently — self-signed vendor certificates are **pinned** explicitly in the connection settings |
| Media gateway ↔ clients | WebRTC (DTLS-SRTP), access by one-time ticket |

**Site CA**: created at installation, key stored in the vault, used only to issue
internal certificates. Clients pin the site CA at first connection (with an explicit trust
prompt showing the fingerprint) unless a public/enterprise certificate is used.

## Secrets vault

- Stores passwords, API keys, private keys and tokens for connections, SMTP, LDAP, SMS
  providers, federation links, backup encryption.
- Each secret is encrypted with AES-256-GCM using a data key; data keys are encrypted with
  the **master key**.
- Master key protection: Windows DPAPI (machine scope) + optional TPM; Linux: TPM 2.0 when
  available, otherwise a root-only key file; containers/cloud: injected from the
  orchestrator's secret store or a cloud KMS. In a cluster, the master key is shared through
  an mTLS-protected enrolment step when a node joins.
- Secrets are write-only in the UI (can be replaced, never displayed); every access by a
  component is audited (which connection read which secret, when).
- Rotation helpers: "rotate" action per secret, expiry reminders for certificates and API
  keys.

## Authentication and sessions

See [Users and rights](../modules/users-and-rights.md): Argon2id, lockout, MFA (TOTP,
WebAuthn), OIDC SSO, LDAP, short-lived tokens with rotating refresh tokens, revocation,
break-glass account.

## Authorisation

- Every API call and every live message is authorised **per object** against the user's
  scopes; no "all data then filter in the client".
- Configuration endpoints reachable only from desktop client sessions with configuration
  permissions.
- No generic "execute SQL" or "execute process" endpoints exist.
- Static shared authentication state is forbidden in code (each request carries its own
  identity context) — enforced by code review rules and tests.

## Code and supply chain

| Item | Measure |
|---|---|
| Service binaries, installers, clients | Code-signed (Authenticode, macOS notarisation, Linux package signatures) |
| Integration packages | Ed25519-signed manifests with file hashes; refused if unsigned/tampered |
| Updates | Signed update manifests with version, hashes, minimum source version; verified before install; staged rollout in clusters with automatic rollback on failed health checks |
| Dependencies | Lock files, SBOM (CycloneDX) per release, automated vulnerability scanning (Dependabot / OSV), licence scanning |
| Build | Reproducible CI builds, protected signing keys (HSM or cloud KMS), two-person release approval |
| Secure development | Threat modelling per module, SAST (Semgrep Community Edition + .NET analyzers + ESLint security rules), secret scanning, dependency review, penetration test before each major release |

## Processes and isolation

- The service runs as a dedicated low-privilege account (`NT SERVICE\Watchpoint` /
  `watchpoint` user), not as Administrator/root.
- Integration hosts and the script host run with even fewer rights (no write access outside
  their working folders), with OS resource limits.
- The Electron client is a thin shell: it loads the UI only from the pinned site origin (no other
  remote content), with context isolation, sandboxed renderers, a minimal origin-checked preload
  bridge and strict CSP (see `Architecture/specs/DSK-desktop-shell.md`).
- The web client has no third-party scripts and no CDN dependencies.

## Logging and privacy in logs

- Structured logs (Serilog) with **automatic redaction**: secret fields declared in schemas,
  authorization headers, credentials in URLs, cardholder data unless in debug mode with a
  time limit.
- Protocol traces (for integration troubleshooting) are opt-in per connection, time-limited
  and redacted.
- Diagnostic bundles (for support) are generated with redaction by default and list their
  contents before export.

## Network guidance (installation guide)

- Place nodes in a security/OT VLAN; clients in operator VLANs; integrated systems in their
  own segments; remote integration hosts for isolated segments.
- Documented port matrix (HTTPS 443 for clients, NATS cluster ports between nodes,
  PostgreSQL between nodes only, WebRTC UDP range for video, federation port).
- Inbound endpoints for vendor push/webhooks are authenticated and bound to specific
  interfaces.

## Security features for customers

- Audit trail with hash chaining and export.
- Security dashboard: failed sign-ins, locked accounts, MFA coverage, expiring certificates,
  unsigned/development packages, admin accounts, sessions.
- Hardening checklist in the client (*System → Security*) with recommendations
  (e.g. "MFA not required for configuration users").
