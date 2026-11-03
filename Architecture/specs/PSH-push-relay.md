# PSH — Push Relay and Node Push Channel

| | |
|---|---|
| Spec id | PSH |
| Projects | `src/pushrelay/Watchpoint.PushRelay` (company-operated service), node channel in `Watchpoint.Notifications/Internal/Channels/Push/` |
| Milestone | M10 |
| Tasks | T10.02 |
| Event id range | 6100–6199 (relay, separate product); node channel logs in NOT range (3140–3149) |
| Design docs | [Notifications §1](../../docs/modules/notifications.md#1-channels), [Mobile app §3](../../docs/clients/mobile-app.md#3-behaviour) |

## 1. Purpose and scope

Apple (APNs) and Google (FCM) only accept pushes signed with the **app publisher's**
credentials, so the company runs a small **push relay**. Sites with outbound internet register
with it; their nodes send minimal, non-sensitive push requests through it to users' phones.
This spec covers the relay service and the node's `Push` notification channel (NOT
`INotificationChannel`).

## 2. Design principles

- **No sensitive data** crosses the relay: payload = site id, alarm id, priority band, a generic
  localized title key ("Critical alarm at {siteName}") — site name optional by site policy;
  detailed content only if the site explicitly enables "detailed push" (then title/source text
  are included, still no personal data).
- The relay never sees raw device tokens from sites: apps register their APNs/FCM token with the
  **site**, the site exchanges it with the relay for an opaque **relay token** bound to that site.
- Relay is stateless for messages (no storage of payloads beyond the send attempt); stores only
  site registrations and token mappings.

## 3. Relay service

ASP.NET Core minimal API, PostgreSQL, deployed in two regions (active/active behind DNS),
containers.

| Endpoint | Auth | Description |
|---|---|---|
| `POST /v1/sites/register` | registration code (from the licensing portal, tied to the licence id) | Site registers with its public key (Ed25519, node identity) → `siteId`, relay endpoints |
| `POST /v1/tokens` | site signature | `{ platform: apns|fcm, deviceToken, appBundle }` → `{ relayToken }` (opaque, 256-bit, per site) |
| `DELETE /v1/tokens/{relayToken}` | site signature | Unregister |
| `POST /v1/push` | site signature | `{ messages: [{ relayToken, kind, priority: critical|high|normal, collapseKey, ttlSeconds, data: { siteId, alarmId?, titleKey, args? } }] }` (≤ 500 per call) → per message result (sent, invalidToken, throttled, error) |
| `GET /v1/health` | none | |

- Site signature: header `X-Wp-Site: <siteId>`, `X-Wp-Ts`, `X-Wp-Sig = Ed25519(body hash | ts)`;
  ±5 min skew; replay cache 10 min.
- **APNs**: HTTP/2 provider API with token-based auth (.p8 key in the relay's secret store),
  `apns-push-type: alert`, `apns-priority` 10 for critical/high, `interruption-level`
  `critical` only when the payload says critical and the app build has the entitlement
  (`critical-alert` sound); `apns-collapse-id`.
- **FCM**: HTTP v1 API with service-account OAuth (Google auth library), Android priority high,
  channel id per priority (`critical`, `alarms`, `info`), collapse key, TTL.
- Invalid tokens (APNs 410 / FCM UNREGISTERED) → mapping deleted, result `invalidToken` so the
  site removes the device registration.
- Quotas per site (default 10,000 pushes/day, 100/min burst) to contain abuse; per-site
  disable switch for staff; metrics per site.
- Localisation: the app localises `titleKey` itself (data-only + alert fallback in English for
  iOS alert body when the app is not running: the relay renders the alert text from a small
  embedded catalogue en/fr/ar using the token's registered language).

## 4. Node push channel

- `PushChannel : INotificationChannel` (`ChannelKind.Push`), enabled when the site registered
  with the relay (*System → Notifications → Mobile push*: registration code from the licensing
  portal, test push to own phone).
- Recipient addresses = the user's registered mobile devices (MOB §5) with relay tokens; device
  registration at the node calls `POST /v1/tokens` (retries; offline relay → device marked
  "push pending").
- Payload built from the notification request (NOT §4): alarm kinds → `titleKey`
  `push.alarm.<band>` with args `{ site: siteName? }`; detailed mode adds `{ title, source }`.
- Delivery results mapped to NOT outcomes; `invalidToken` → device push token cleared and user
  notified in-app at next sign-in.
- Sites without internet: channel shows "unavailable" in health; NOT falls back per recipient
  channel preferences (SMS/e-mail).

## 5. Security and privacy

- Relay secrets (APNs key, FCM service account) in a cloud secret manager; rotated yearly.
- Logs never contain tokens (hash prefixes only) or payload args.
- Data retention: token mappings deleted after 90 days of inactivity; site registrations removed
  when the licence is revoked (portal webhook).

## 6. Tests

| Id | Test |
|---|---|
| PSH-U-001…004 | Site registration with portal code; signature verification; skew and replay protection |
| PSH-U-005…008 | Token exchange and unregister; opaque token format; site binding |
| PSH-U-009…013 | APNs request building (headers, priority, interruption level, collapse id) against a fake HTTP/2 server; JWT provider token caching |
| PSH-U-014…017 | FCM HTTP v1 request building, OAuth token caching, channel ids |
| PSH-U-018 | Invalid token handling → mapping deleted and result propagated |
| PSH-U-019 | Quotas and staff disable switch |
| PSH-U-020 | Alert text localisation catalogue en/fr/ar |
| PSH-U-021 | Node push channel payload minimal vs detailed modes; no personal data (property test over alarm models) |
| PSH-U-022 | Node device registration retries and "push pending" |
| PSH-U-023 | Delivery result mapping into NOT outcomes and timeline |
| PSH-U-024 | Logs redact tokens and payload args |
| PSH-U-025 | Relay unavailable → channel health and NOT fallback |
| PSH-I-001 | Relay + node in compose with fake APNs/FCM servers: end-to-end push request |
| PSH-I-002 | Real FCM sandbox project (weekly, secrets in CI) to an Android emulator |
| PSH-I-003 | Real APNs sandbox (weekly, macOS runner) to an iOS simulator push file / device farm |
| PSH-I-004 | Load: 10,000 pushes/min across 100 sites |

## 7. Tasks

| Task | Sections | Tests |
|---|---|---|
| T10.02 | all | PSH-U-001…PSH-U-025, PSH-I-001…PSH-I-004 |
