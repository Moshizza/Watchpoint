# MOB — Mobile App (alarms and events)

| | |
|---|---|
| Spec id | MOB |
| Projects | `src/mobile/app` (Expo / React Native app), shared packages from `src/ui/packages` (`api-client`, `live`, `model`, `i18n` — platform-agnostic parts) |
| Milestone | M10 |
| Tasks | T10.01 |
| Design docs | [Mobile app](../../docs/clients/mobile-app.md) |

## 1. Purpose and scope

A deliberately simple iOS/Android app: sign in to a site, see and handle **alarms** (acknowledge,
comment, take ownership, transfer, close with report, procedure checklist), browse **events**,
manage own **notification subscriptions**, profile. No synoptics, video, commands or
configuration in v1. Push notifications through PSH; live updates over the same hub protocol.

## 2. Dependencies and technology

Expo SDK (MIT; builds made locally/CI with `eas build --local` or `expo prebuild` + Gradle/Xcode —
no paid cloud service), React Native, TypeScript, React Navigation (MIT), TanStack Query,
`@microsoft/signalr` + MessagePack protocol (as UIC live client), `expo-secure-store`,
`expo-local-authentication`, `expo-notifications`, `expo-camera` (QR scan),
`react-native-ssl-public-key-pinning` (MIT) for site CA/public key pinning, i18next with RTL
(`I18nManager`), Jest + React Native Testing Library, Maestro (Apache-2.0) for device flows.

## 3. Layout

```
src/mobile/app/
├── app.config.ts (bundle ids, permissions, plugins: notifications, pinning, critical alerts entitlement flag)
├── src/
│   ├── platform/  MobilePlatform (token store in SecureStore, biometric lock, app state → live pause/resume)
│   ├── enrol/     QrEnrolScreen, ManualSiteScreen, TrustScreen (CA fingerprint confirm)
│   ├── auth/      SignInScreen (local/SSO via system browser + PKCE), MfaScreen (TOTP; WebAuthn passkeys later), BiometricUnlock
│   ├── alarms/    AlarmListScreen, AlarmDetailsScreen, ProcedureChecklist, ActionSheet (ack, comment, own, transfer, close), Timeline, SnapshotGallery
│   ├── events/    EventFeedScreen, EventDetailsScreen
│   ├── notifications/ SubscriptionsScreen, PushRegistration
│   ├── profile/   ProfileScreen (language, time zone display, sessions, sign out)
│   └── shared/    theme (tokens from @watchpoint/ui design tokens), components
```

Shared packages must not import DOM APIs (lint rule `no-dom-in-shared` checked by a TS project
with `lib: ["es2022"]` only).

## 4. Enrolment and trust

- **QR enrolment**: desktop/web user menu "Connect mobile app" shows a QR with
  `watchpoint://enrol?u=<siteUrl>&fp=<caSha256>&t=<enrolToken>` (token optional, 10 min, single
  use, pre-fills the user name; `POST /api/v1/me/mobile-enrolment` creates it).
- Manual: site URL; the app fetches the site CA certificate (`/api/v1/system/ca`) and shows the
  fingerprint for confirmation (or matches `fp` from the QR).
- Pinning: the site CA public key is pinned for that site profile; certificate errors outside the
  pin are fatal (no "continue anyway").
- Several site profiles supported (switcher).

## 5. Authentication and sessions

- Client kind `mobile` (IDN): password + MFA (TOTP) or OIDC SSO through the system browser
  (ASWebAuthenticationSession / Custom Tabs) with PKCE; mobile access must be allowed for the
  user (client access kinds, RGT) and licensed (LIC mobile named users).
- Refresh token in SecureStore (Keychain / Android Keystore, `requireAuthentication` when
  biometric lock enabled); access token in memory.
- **Biometric/PIN lock**: after sign-in the app offers biometric unlock; app lock after
  `Mobile:IdleLock` (default 5 min in background).
- **Remote wipe**: when the node reports `sessionRevoked` or a 401 with
  `identity.session.revoked_device`, the app deletes all local data for that site profile.
- Device registration: `POST /api/v1/me/mobile-devices { platform, pushToken (APNs/FCM),
  appVersion, deviceName }` → device id; listed in the user's sessions; admins can revoke
  (`admin.sessions.manage`).

## 6. Screens and behaviour

| Screen | Details |
|---|---|
| Alarms | Live `alarms` topic (visible scope + priority range); sorted priority then time; colour band + icon + text label per priority; counters; filters (priority ≥, location, mine/unassigned); pull to refresh; offline banner when the live connection is down (last update time) |
| Alarm details | Priority, title, source (name, type, location path), times in site time zone (and device time zone if different), state, owner, counter, procedure (read, tick checklist steps, answer questions — ALM procedure answers), comments, timeline (HIS), snapshots (FIL signed URLs, images only), linked contacts with call buttons (`tel:`) |
| Actions | Acknowledge, comment, take ownership, transfer (to user/group), close with report (required fields) — buttons shown only with rights; reasons/confirmations as on desktop; four-eyes not supported on mobile (commands absent) |
| Events | `events` topic + journal query (last 24 h by default, infinite scroll), filters severity/location/type, details |
| Notifications | Own subscriptions (NOT §4.1): kinds (alarm raised/escalated), priority threshold, locations, quiet hours calendar or simple time range, sound/critical alert toggle (if site allows) |
| Profile | Language en/fr/ar (RTL restart prompt), time display preference (site/device), active sessions, sign out, about (versions) |

Live connection only while foreground (app state change → stop/resume with resync); background
relies on push (PSH). Data minimisation: no alarm data cached on disk except the last alarm list
snapshot (encrypted by SecureStore-held key, cleared on sign-out).

## 7. Push handling

- On notification tap: app opens (biometric unlock if enabled) → deep link
  `watchpoint://site/<siteId>/alarms/<alarmId>` → fetches details from the site (payload carries
  only ids).
- Android channels: `critical` (high importance, bypass DND only if the user/site enables),
  `alarms`, `info`. iOS: critical alerts entitlement used only when the site enables it and the
  build includes it (Apple approval required; feature flag in `app.config.ts`).
- Without internet (on-site network only): the app warns in Notifications settings that
  background notifications require internet; foreground live notices still work.

## 8. Node-side additions (T10.01)

- `mobile` client kind restrictions in API: only operation endpoints for alarms, events, profile,
  subscriptions, files (snapshots) — explicit allow-list (`MobileEndpoint()` metadata) to keep
  the mobile surface small; others → `api.client_kind.not_allowed`.
- `GET /api/v1/system/ca` (anonymous): site CA certificate (PEM) and fingerprint.
- Mobile devices registry (IDN): `mobile_devices` table (id, user_id, platform, push_token
  encrypted, app_version, name, created_at, last_seen, revoked_at).

## 9. Tests

| Id | Test |
|---|---|
| MOB-U-001…005 | Enrolment: QR parsing, token pre-fill, manual URL, CA fingerprint confirmation, pinning failure fatal |
| MOB-U-006…012 | Auth: password + TOTP, SSO PKCE flow (mock), mobile access denied message, licence refusal, token storage in SecureStore, biometric lock and idle lock, remote wipe |
| MOB-U-013…016 | Device registration/update/revocation; multiple site profiles |
| MOB-U-017…025 | Alarm list: live updates, sorting, colour+icon+text, filters, counters, offline banner, pull to refresh, scope filtering |
| MOB-U-026…035 | Alarm details: fields and time zones, procedure checklist and answers, comments, timeline, snapshots via signed URLs, contacts call buttons |
| MOB-U-036…040 | Actions with rights gating, reasons, close report validation, errors translated |
| MOB-U-041…044 | Events feed, filters, details |
| MOB-U-045…047 | Subscriptions screen ↔ NOT API; critical alert toggle gating |
| MOB-U-048 | Push tap deep link with unlock and fetch |
| MOB-U-049 | RTL layout (Arabic) snapshots |
| MOB-U-050 | Shared packages contain no DOM API usage (type check with es2022 lib only) |

Device flows (Maestro, Android emulator in CI; iOS simulator on macOS runner weekly): enrol by QR
image injection, sign in, acknowledge an alarm, open from a push (local notification simulation).

## 10. Tasks

| Task | Sections | Tests |
|---|---|---|
| T10.01 | all | MOB-U-001…MOB-U-050 |
