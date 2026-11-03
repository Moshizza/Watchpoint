# WEB — Web Client (operation only)

| | |
|---|---|
| Spec id | WEB |
| Projects | `src/ui/apps/web` (Vite app), node hosting in `Watchpoint.Api` (DSK §5 table, `/app/web/`) |
| Milestone | M6 |
| Tasks | T6.08 |
| Design docs | [Web client](../../docs/clients/web-client.md) |

## 1. Purpose and scope

The browser application for **operation only**: the operation packages (UIO, SYN viewer, VID,
reports viewer) built into a separate bundle that does **not contain** configuration code, with
browser-specific authentication, notifications and protections. Served by every node at
`https://<site>/app/web/` (and `/` redirects there).

## 2. Dependencies

`@watchpoint/operation`, `synoptics` (viewer entry only), `video`, `api-client`, `live`, `i18n`,
`ui`, `model`. Must **not** import `@watchpoint/configuration` or `synoptics/editor` (lint rule +
bundle analysis test WEB-U-001).

## 3. Layout

```
src/ui/apps/web/
├── index.html, vite.config.ts (manualChunks per route, no config packages), public/ (icons, manifest.webmanifest)
└── src/
    ├── main.tsx                 bootstraps OperationApp with WebPlatform
    ├── platform/WebPlatform.ts  OperationPlatform implementation (§4)
    ├── auth/CookieTokenStore.ts refresh via HttpOnly cookie, access token in memory
    ├── notifications/BrowserNotifications.ts, SoundUnlock.ts
    ├── layout/SingleWindowWorkspaces.tsx (tabs + split views; no multi-window)
    └── routes.tsx               operation routes + reports viewer + synoptic viewer
```

## 4. Platform adapter

| `OperationPlatform` member | Web implementation |
|---|---|
| `kind` | `"web"` |
| `tokenStore` | Access token in memory only; refresh token in an **HttpOnly, Secure, SameSite=Strict cookie** scoped to `Path=/api/v1/auth` set by IDN when `clientKind=web` (IDN returns no refresh token in the body for web); refresh by `POST /api/v1/auth/refresh` with the cookie + CSRF header `X-Watchpoint-Csrf` (double-submit token from a non-HttpOnly cookie) |
| `serverProfile()` | Same origin (`location.origin`) |
| `openInNewWindow` | Not provided → UI shows "open in new tab" (`window.open` with `noopener`) for routes |
| `bringToFront` | Not provided; Critical alarm → browser notification + title flashing |
| `showOsNotification` | Notifications API after explicit user opt-in (button in user menu; never prompted automatically); click focuses the tab and navigates to the route |
| `playSound` | Web Audio; sounds are **unlocked** by the first user gesture after sign-in (banner "Click to enable alarm sounds" until unlocked); visibility-hidden tabs keep playing |
| `storage` | `localStorage` namespaced per user id (preferences only, never tokens) |

IDN changes (part of T6.08): sign-in/refresh endpoints support the cookie mode for `web`
(`Set-Cookie` with `__Host-` prefix name `__Host-wp_rt`, rotation on each refresh, revocation
deletes it), CSRF validation on cookie-based refresh, sign-out clears cookies.

## 5. Features and restrictions

- All operation features of UIO (alarm console, alarm details/procedures, event journal, device
  explorer and pages, commands within rights and client-kind constraints), synoptic viewer
  (plans, maps, dashboards), video live/playback and on-screen PTZ (Gamepad joystick in Chromium
  browsers), reports run/view/download, automation manual start (M8).
- Workspaces: single window — tabs and split views (2 or 4 panels); multi-monitor/undock/kiosk
  are desktop-only (menu items absent).
- No configuration: no side switch; links to configuration pages are not rendered; any direct
  attempt returns 403 `api.client_kind.not_allowed` from the API.
- Responsive layout ≥ 768 px wide (tablets); below, a notice suggests the mobile app (M10) but
  the console remains usable.
- `manifest.webmanifest` for "install as app" (standalone display) without a service worker (no
  offline caching of an operational UI — avoids stale bundles after upgrades).

## 6. Performance protections (client side)

- Lists virtualised and server-paged (UIC `DataGrid`); live subscriptions only for visible rows
  and viewports (UIC/SYN rules).
- When the tab is hidden for > 60 s: non-alarm subscriptions paused (devices, aggregates,
  events) and resumed with resync on visibility; the alarm subscription stays active.
- Live client's slow-consumer handling: the hub coalesces for slow connections (API §8); the
  client shows "Connection is slow — some updates are grouped".
- History queries from the web client use the API's lower priority class (API sets
  `X-Watchpoint-Client: web` → query budget and statement timeout 15 s instead of 30 s; API
  change in T6.08).

## 7. Node-side settings (T6.08)

| Key | Default | Effect |
|---|---|---|
| `Web:Enabled` | true | `/app/web/` served; false → 404 and web sign-in refused (`identity.client_kind.disabled`) |
| `Web:AllowedNetworks` | [] (all) | CIDRs allowed for web sessions (checked at sign-in and on every request with `ck=web`) |
| `Web:SsoOnly` | false | Web sign-in only through OIDC providers |
| `Web:MaxSessionsPerUser` | 3 | |
| `Web:PublicUrl` | null | Used for deep links in notifications (NOT §6.2) when the site is behind a reverse proxy |

Seats: concurrent web sessions limited by licence (LIC, M7) through `ISessionAdmission`.

CSP for `/app/web/` as DSK §5 plus `connect-src 'self' wss://<host>` and the video signalling
paths (same origin), `media-src 'self' blob:`; no inline scripts; `Strict-Transport-Security:
max-age=31536000` when served over HTTPS (always, except Development).

## 8. Tests

| Id | Test |
|---|---|
| WEB-U-001 | Bundle analysis: no module from `@watchpoint/configuration` or synoptics editor in the build output (rollup stats) |
| WEB-U-002 | Lint rule forbids importing configuration packages in apps/web |
| WEB-U-003 | Cookie token store: access token only in memory; refresh via cookie with CSRF header |
| WEB-U-004 | IDN web cookie mode: `__Host-` cookie attributes, rotation, revocation clears |
| WEB-U-005 | CSRF validation failure → 403 |
| WEB-U-006 | Browser notifications only after opt-in; click navigates |
| WEB-U-007 | Sound unlock banner until a user gesture |
| WEB-U-008 | Critical alarm in hidden tab: notification + title flashing + sound |
| WEB-U-009 | Hidden tab pauses non-alarm subscriptions and resyncs on return |
| WEB-U-010 | Single-window workspaces: tabs and split views; no multi-window actions |
| WEB-U-011 | No side switch or configuration links rendered for configurators |
| WEB-U-012 | Responsive layout at 768 px (tablet) and mobile notice |
| WEB-U-013 | `Web:Enabled=false` → 404 and sign-in refused |
| WEB-U-014 | `Web:AllowedNetworks` enforced at sign-in and per request |
| WEB-U-015 | `Web:SsoOnly` hides local sign-in and refuses password sign-in for web |
| WEB-U-016 | Max sessions per user |
| WEB-U-017 | CSP and HSTS headers on `/app/web/` |
| WEB-U-018 | Web history queries use lower budget (statement timeout 15 s) |
| WEB-U-019 | Deep links with `Web:PublicUrl` |
| WEB-U-020 | Cross-browser smoke (Playwright: Chromium, Firefox, WebKit): sign-in, alarm ack, synoptic open, video tile (Chromium/Firefox) |

## 9. Tasks

| Task | Sections | Tests |
|---|---|---|
| T6.08 | all (plus IDN cookie mode and API web query budget) | WEB-U-001…WEB-U-020 |
