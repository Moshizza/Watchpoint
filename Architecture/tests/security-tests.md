# Security Tests and Hardening Checklist (S-001…S-031)

Task T11.06. Source of requirements: [Security](../../docs/operations/security.md). Each item
is an automated test unless marked **(review)** (a documented manual check with evidence stored
in `docs/security/evidence/<release>/`). All tools listed are free.

## 1. Tooling

| Tool | Use |
|---|---|
| Semgrep CE (LGPL rules engine, community rules + `tools/semgrep/watchpoint.yml`) | SAST on C# and TypeScript in CI |
| `dotnet list package --vulnerable`, `pnpm audit`, OSV-Scanner (Apache-2.0) | Dependency vulnerabilities |
| CycloneDX (`CycloneDX` dotnet tool, `@cyclonedx/cyclonedx-npm`) | SBOM per release artefact |
| `tools/licenses` (own script over SBOM) | Third-party notices file and licence allow-list check (no GPL/AGPL in shipped artefacts; LGPL only for dynamically used FFmpeg) |
| OWASP ZAP (Apache-2.0) baseline + API scan with OpenAPI | DAST on a running node |
| testssl.sh (GPL, external tool, not shipped) | TLS configuration |
| Trivy (Apache-2.0) | Container image scanning |
| gitleaks (MIT) | Secret scanning of the repository |

## 2. Tests

| Id | Area | Test |
|---|---|---|
| S-001 | Transport | All listeners TLS 1.2+ (1.3 preferred), no weak ciphers (testssl.sh grade ≥ A) on HTTPS, NATS cluster routes, gRPC host/federation links |
| S-002 | Transport | HSTS, CSP, X-Content-Type-Options, Referrer-Policy, Permissions-Policy, frame-ancestors on all UI paths (DSK §5, WEB §7) |
| S-003 | Transport | mTLS required on NATS routes, integration hosts, script host pipe ACLs, federation links; unauthenticated peers refused |
| S-004 | AuthN | Brute force: lockout and rate limits on sign-in, pairing, setup, webhooks endpoints |
| S-005 | AuthN | Token lifetimes, refresh rotation and theft detection (IDN), revocation propagation < 2 s |
| S-006 | AuthN | MFA bypass attempts (skip step, replay TOTP, reuse WebAuthn challenge) fail |
| S-007 | AuthZ | Generated authorisation matrix over the endpoint catalogue × permission sets × client kinds: no endpoint reachable without its declared permission; configuration endpoints refuse web/mobile |
| S-008 | AuthZ | Scope leakage: users scoped to one location never receive other locations' data via REST, live topics, exports, reports, notifications, federation (aggregates X-RGT-API-C-001, X-NOT-RGT-C-001, X-RPT-RGT-C-001) |
| S-009 | AuthZ | Escalation: rights-copy and grant editing cannot grant beyond the actor's rights (RGT §10) |
| S-010 | Input | ZAP API scan with OpenAPI: no High/Medium findings |
| S-011 | Input | Injection tests: SQL (EF parameterisation; raw SQL only in providers reviewed), LDAP filters (IDN), JSON path/CEL expression sandbox, template placeholders (NOT, RPT), CSV formula injection in exports (cells starting with `= + - @` prefixed) |
| S-012 | Files | Upload validation: content sniffing, SVG sanitiser OWASP vectors, PDF actions, decompression bombs, path traversal in archives (`.wpkg`, `.wpbk`, `.wpup`) |
| S-013 | Secrets | No secrets in logs, exceptions, metrics, diagnostics bundles, API responses (scanner over captured outputs of the full e2e suite) |
| S-014 | Secrets | Vault encryption at rest, master key providers, key rotation and rewrap; recovery passphrase path |
| S-015 | Integrity | Package signatures (`.wpkg`), update signatures (`.wpup`), licence signatures, backup signatures refuse tampered inputs (fuzzed byte flips) |
| S-016 | Integrity | Audit chain tamper detection end-to-end (AUD-I-003) |
| S-017 | Isolation | Connector in isolated host cannot read the node's data folder or secrets (file system permissions test) |
| S-018 | Isolation | Script sandbox escape attempts (prototype pollution of bindings, `eval`, `Function`, `import()` of absolute paths, infinite recursion, SharedArrayBuffer) fail |
| S-019 | Isolation | Synoptic documents cannot execute script (web widget sandbox, SVG sanitised symbols) |
| S-020 | Supply chain | SBOM generated for every artefact; licence allow-list check passes; third-party notices file included in installers and About dialog |
| S-021 | Supply chain | No known vulnerabilities ≥ High in dependencies at release (OSV, `dotnet list package --vulnerable`, `pnpm audit`); exceptions documented with justification |
| S-022 | Supply chain | Container images: Trivy no High/Critical OS vulnerabilities; non-root; read-only rootfs |
| S-023 | Supply chain | gitleaks clean; no secrets in repository history |
| S-024 | Platform | Windows service account least privilege (virtual account); data folder ACLs; Linux systemd hardening score (`systemd-analyze security` ≤ 3.0) |
| S-025 | Platform | Default configuration is secure: management port loopback only, web enabled on LAN only, no default passwords, setup only from loopback |
| S-026 | Privacy | Personal data masking without permission across UI, exports, reports, notifications; erasure/pseudonymisation (HIS §12) complete |
| S-027 | Privacy | Push payloads and portal/relay requests carry no personal data (X-PSH-PRIV-C-001, LIC request content test) |
| S-028 | Client | Electron shell hardening: contextIsolation, sandbox, no nodeIntegration, navigation allow-list, permission handlers, certificate pinning per site profile (DSK tests) |
| S-029 | (review) | Threat model updated (STRIDE per component: node, hosts, clients, portal, relay) and residual risks accepted by the product owner |
| S-030 | (review) | Pre-penetration-test package: scope document, test accounts and environments, architecture diagrams, previous findings closed; external penetration test scheduled before the first customer release |
| S-031 | Platform | Development switches refused on a production install: `Node:Environment=Development` with the service host or the default data path stops the node (HST §12); installers write `Production`; Development-only POSTs answer loopback only (IRT §10) |

## 3. Hardening checklist (installation guide content generated from S-tests)

The release pipeline generates `docs/operations/hardening-guide.md` sections from the passing
tests: network ports and firewall rules, TLS certificate options, service accounts, backup
encryption, SSO/MFA enforcement, web exposure guidance (reverse proxy/WAF), log forwarding to
SIEM, update policy.
