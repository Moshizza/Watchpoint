# LPT — Licensing Portal (company side)

| | |
|---|---|
| Spec id | LPT |
| Projects | `src/portal/Watchpoint.Portal` (ASP.NET Core API + hosted UI), `src/portal/Watchpoint.Portal.Signer` (isolated signing service), `src/portal/ui` (React app), shares `src/service/Watchpoint.Licensing.Format` |
| Milestone | M7 |
| Tasks | T7.03 (§4–§8), T7.04 (§9) |
| Event id range | 6000–6099 (portal is a separate product; its own range) |
| Design docs | [Licensing §6–§7](../../docs/modules/licensing.md#6-activation-flow) |

## 1. Purpose and scope

A separate web application **operated by the company** (not shipped to customers) where:

- partners and customers have accounts and see their sites, licences and requests;
- orders (from sales/ERP) generate **activation codes**;
- sites submit activation/modification/re-host/renewal requests (online API or uploaded
  `.wprq` files);
- the **licensing team reviews every request** (never automatic issuance) with anomaly
  detection, and issues signed licences through an isolated **signer**;
- online sites download issued licences automatically and check in (online-validated);
- renewals reminders and reporting.

## 2. Dependencies and technology

ASP.NET Core 10 minimal APIs, EF Core + PostgreSQL 17 (own database, separate from any customer
site), ASP.NET Core Identity (accounts, TOTP and WebAuthn MFA mandatory for staff and partners),
staff sign-in through the company OIDC provider (configurable), MailKit (e-mails), React 19 +
shadcn/ui + i18next (en/fr/ar), `Watchpoint.Licensing.Format` (documents, canonical JSON,
verification), NSec (Ed25519), Pkcs11Interop (Apache-2.0) for HSM signing (optional), Serilog,
OpenTelemetry. Deployed as containers (portal API, signer, PostgreSQL) on company
infrastructure.

## 3. Layout

```
src/portal/
├── Watchpoint.Portal/
│   ├── Accounts/ (organisations, users, roles, partner→customer relations)
│   ├── Orders/ (orders, items, activation codes, ERP import)
│   ├── Sites/ (sites, installations, node public keys, licence history)
│   ├── Requests/ (intake API, file upload, verification, anomaly detection, review workflow)
│   ├── Issuance/ (licence builder from order + request, signer client)
│   ├── CheckIn/ (online-validated sites, duplicate detection)
│   ├── Renewals/ (reminders, renewal orders)
│   ├── Reporting/
│   ├── Notifications/ (e-mail templates en/fr/ar)
│   └── Audit/ (append-only portal audit)
├── Watchpoint.Portal.Signer/   (gRPC service on an isolated network; holds the signing key)
└── ui/                          (React app: customer/partner area, licensing team area)
```

## 4. Accounts and roles (T7.03)

| Role | Can |
|---|---|
| `customer.admin` / `customer.user` | See own organisation's sites, licences, requests; upload request files; download licences |
| `partner.admin` / `partner.user` | Same for their customers' sites (partner → customer relation), create requests on their behalf, see orders they placed |
| `staff.sales` | Manage orders and activation codes, accounts |
| `staff.licensing` | Review queue, approve/reject, issue, re-issue, revoke |
| `staff.admin` | Users, roles, signer key rotation workflow, settings |
| `staff.viewer` | Read-only reporting |

MFA mandatory for all staff and partner users; customer users optional (enforced per
organisation setting). Sessions 8 h, idle 30 min.

## 5. Orders and activation codes (T7.03)

- Order: id, customer, partner, items (edition, device units, seats per kind, mobile users,
  nodes, remote hosts, integrations feature keys, options, model and terms: perpetual + SUP
  until, subscription term, trial/demo/NFR), status.
- Import from ERP: CSV/JSON upload or `POST /api/v1/orders` (service account token) — mapping
  of ERP product codes to licence items in a table maintained by staff.
- Activation code per order (or per order item group): 20 characters Crockford base32 in groups
  (`XXXXX-XXXXX-XXXXX-XXXXX`) with check digit; single site; consumable once for activation,
  reusable for modifications of the same site.

## 6. Requests intake and review (T7.03)

### 6.1 Intake

- Online: `POST /api/v1/requests` (anonymous but rate-limited per IP 30/h; body = request
  document; TLS) — the site's node public key (from `.wprq` signature or the online body
  signature) is recorded with the installation id.
- Offline: upload `.wprq` in the UI (customer/partner) → verify node signature, parse.
- Validation: format, signature, activation code exists and belongs to the uploader's
  organisation (for uploads), not already consumed by another site, request kind consistent
  (rehost requires existing licence of the site).

### 6.2 Anomaly detection (shown to reviewers; never auto-rejects)

| Check | Flag |
|---|---|
| Requested usage vs order quantities | Usage above ordered units/seats |
| Footprint component hashes already licensed to another site | "Footprint already used by site X" |
| Re-hosts count for the site in 12 months | > 2 |
| Virtualisation kind `container` with hardware-bound type | Suggest online-validated |
| Request from a country outside allowed constraints | Export constraint |
| Installation id seen from different public IPs via check-in (duplicates) | Possible cloning |
| Version older than minimum supported | Info |

### 6.3 Review workflow

States: `submitted → inReview (assigned) → approved | rejected | needsCorrection`; comments
thread visible to the customer/partner (internal notes separate); approval chooses the order
lines to apply and produces a licence draft (§7) shown as a diff vs the current licence
(quotas, features, nodes, dates). Four-eyes option for high-value licences (setting:
`Issuance:SecondApprovalAbove` units), approval by a second `staff.licensing` user.

## 7. Issuance and signer (T7.03)

- `LicenceBuilder` composes the payload from the approved request (nodes footprints,
  installation id, site), order items (edition, quotas, features, model/dates) and previous
  licence (version + 1).
- **Signer service** (`Watchpoint.Portal.Signer`): separate process/container on an isolated
  network segment, gRPC with mTLS (portal client certificate), single RPC
  `Sign(payloadCanonicalJson, keyId) → signature`, re-verifies the payload against a
  policy (format, max validity 10 years for perpetual, required fields) before signing, logs
  every signature (append-only file + portal audit). Key storage: **PKCS#11 HSM** (YubiHSM 2 or
  cloud HSM) preferred; fallback encrypted key file unlocked at signer start by two staff
  passphrases (Shamir 2-of-3 shares) — key never leaves the signer.
- Key rotation: new key id added to the product's embedded keys in a release before it is used;
  portal setting chooses the active key id; old keys remain valid for verification.
- Issued licence stored, request → `issued`, e-mail to the requester, available for download
  and for the site's poll.
- Revocation (staff): marks a licence revoked; delivered to online sites at next poll/check-in
  (`revoked` → site treats as Missing after grace); offline sites unaffected (documented).

## 8. Site-facing API and check-in (T7.03)

| Method and path | Auth | Description |
|---|---|---|
| `POST /api/v1/requests` | node signature | Submit request → `{ requestId, status }` |
| `GET /api/v1/requests/{id}` | node signature (header `X-Wp-Node-Signature` over `id|timestamp`) | Status; when issued, includes the licence document |
| `POST /api/v1/checkin` | node signature | Online-validated check-in: `{ installationId, licenceId, version, usage }` → `{ ok, licence?, revoked?, duplicate? }` |
| `GET /api/v1/time` | none | Server time (sites can compare for clock guard diagnostics) |

- Node signature: Ed25519 with the node identity key registered at first request; requests from
  an unknown key for an existing installation → flagged (re-install) and require staff review.
- Check-in stores last seen, version, usage (opt-in detailed usage) and source IP; duplicates
  (same installation id, different node keys within 48 h) flagged to the licensing team.

## 9. Portal UI (T7.04)

- **Customer/partner area**: sites list (status, licence expiry, SUP/subscription end, last
  check-in), site page (licences history with download, requests with statuses and comments,
  upload `.wprq`), orders (partners), notifications settings, language en/fr/ar (RTL).
- **Licensing team area**: review queue (filters, assignment, SLA timers), request page
  (request vs order comparison table, anomaly flags, footprint matches with previous licence
  "4/5 components", licence draft diff, approve/reject/needs correction with message, second
  approval), issuance history, revocation, accounts/orders management (sales), reporting
  (installed base by edition/version/country, renewals pipeline 90/30/7 days, requests volume and
  review time).
- **Renewals**: reminders e-mails 90/30/7 days before SUP/subscription end to partner and
  customer admins; renewal orders link to the site; renewal issuance re-uses the current
  footprints (no request needed unless changed) — staff can issue directly from the renewal.

## 10. Security

- OWASP ASVS L2 target; rate limits on anonymous endpoints; CSRF protection for the UI (cookie
  auth + antiforgery); strict CSP; all staff actions audited (who, what, request, licence
  version); database backups encrypted; signer isolated and unreachable from the internet.
- Licence documents contain no personal data beyond organisation names.
- Data protection: customer contacts' personal data managed under the company's privacy policy
  (export/erase tools for accounts).

## 11. Observability

| EventId | Level | Message |
|---|---|---|
| 6000 | Information | Request {RequestId} ({Kind}) received for site {Site} via {Channel} |
| 6010 | Information | Request {RequestId} approved by {User}; licence {LicenceId} v{Version} issued |
| 6011 | Information | Request {RequestId} rejected by {User} |
| 6020 | Warning | Anomaly {Anomaly} on request {RequestId} |
| 6030 | Warning | Duplicate installation {InstallationId} check-in from {Ip} |
| 6040 | Information | Signer signed payload {PayloadHash} with key {KeyId} |

## 12. Tests

| Id | Test |
|---|---|
| LPT-U-001…008 | Accounts and roles: permissions matrix, partner→customer visibility, MFA enforcement, session timeouts |
| LPT-U-009…015 | Orders: model validation, ERP CSV/JSON import with product mapping, activation code format/check digit, consumption rules |
| LPT-U-016…022 | Intake: online request validation, `.wprq` signature verification, activation code ownership, consumed code refusal, rate limit |
| LPT-U-023…030 | Anomaly detection: each check with fixtures |
| LPT-U-031…036 | Review workflow states, comments vs internal notes, assignment, second approval threshold |
| LPT-U-037…042 | Licence builder: payload from order + request + previous licence; version increments; edition defaults; dates per model |
| LPT-U-043…046 | Signer: policy checks, PKCS#11 (SoftHSM2 in tests), key file with 2-of-3 shares, audit log |
| LPT-U-047 | Revocation delivered at poll/check-in |
| LPT-U-048 | Site API node signature verification and timestamp skew (5 min) |
| LPT-U-049 | Check-in duplicate detection |
| LPT-U-050 | Shared `Licensing.Format` round trip: portal-signed licence verified by the product validator |
| LPT-U-051…058 | Customer/partner UI: sites, site page, licence download, request upload, comments, language/RTL |
| LPT-U-059…066 | Licensing team UI: queue filters/SLA, comparison table, anomaly display, draft diff, approve/reject flows, second approval |
| LPT-U-067…070 | Sales UI orders, renewals reminders scheduling, reporting views, accessibility checks |
| LPT-I-001 | Full stack in docker compose (portal, signer with SoftHSM2, PostgreSQL, Mailpit) |
| LPT-I-002 | Online activation from a real Watchpoint node container (LIC-I-003 counterpart) |
| LPT-I-003 | Offline file round trip |
| LPT-I-004 | Rehost with 3/5 footprint changes flagged and approved |
| LPT-I-005 | Online-validated check-ins and duplicate detection |
| LPT-I-006 | Signer unreachable → issuance fails cleanly, request stays approved-not-issued, retry |
| LPT-I-007 | Renewal issuance and site auto-download |
| LPT-I-008 | Security: OWASP ZAP baseline scan (free) on the UI/API, no high findings |
| LPT-I-009 | Backup/restore of portal database |
| LPT-I-010 | Load: 1,000 check-ins/min |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T7.03 | §4–§8, §10–§11 | LPT-U-001…LPT-U-050, LPT-I-001…LPT-I-010 |
| T7.04 | §9 | LPT-U-051…LPT-U-070 |
