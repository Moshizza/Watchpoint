# Mobile App — Alarms and Events

> Requirement: a very simple mobile app, only events and alarms, no synoptics, no video,
> no configuration for now; the bare minimum to know when something is wrong, with enough
> details.

## 1. Technology

- **React Native + Expo** (open-source framework; app builds made in our own CI, not the paid Expo cloud build service), iOS and Android, sharing TypeScript models, API clients,
  translations and business rules with the web/desktop UI.
- Live stream over SignalR (same protocol as other clients) while the app is open; push
  notifications for the background (see [Notifications](../modules/notifications.md)).
- Secure storage of tokens (Keychain / Android Keystore); biometric unlock.

## 2. Features (v1)

| Screen | Content |
|---|---|
| **Sign-in** | Site (QR code enrolment from the desktop/web client, or address), user, password/SSO, MFA; biometric unlock afterwards |
| **Alarms** | Active alarms within the user's rights, sorted by priority/time, colour bands, counters; filters (priority, location, mine/unassigned); pull to refresh |
| **Alarm details** | Priority, source (name, type, location path), time (site time zone), state, owner, counter, procedure steps (read and tick checklist items, answer questions), comments, timeline, snapshot images attached to the alarm (still images, not video), linked contacts with call buttons |
| **Alarm actions** | Acknowledge, comment, take ownership, transfer, close with report — within rights; commands are **not** available in v1 |
| **Events** | Event journal feed with filters (severity, location, type), event details |
| **Notifications settings** | Subscribe to alarm notifications within rights (priority threshold, locations, quiet hours, sound) |
| **Profile** | Language (en/fr/ar with RTL), time zone display, sign out, active sessions |

## 3. Behaviour

- **Push notification** (online sites): content minimal by default ("Critical alarm at Paris
  HQ — open Watchpoint"); optional detailed content per site policy. Tapping opens the alarm
  after unlock.
- **On-site only networks**: notifications while the app has a live connection (Wi-Fi/VPN);
  the app warns that background notifications need internet.
- Critical alarms can use **critical alert** permissions (iOS) / high-priority channels
  (Android) where the customer enables them.
- Mobile licensing per named user; mobile access must be granted per user (client access).
- Lost phone: an administrator revokes the device session; the app wipes local data on next
  contact.

## 4. Later

Device states and commands for selected devices, still images from cameras, then live
video; guard tour and incident reporting with photos; offline queue of comments.
