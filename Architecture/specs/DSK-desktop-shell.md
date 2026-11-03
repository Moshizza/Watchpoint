# DSK — Desktop Shell (Electron)

| | |
|---|---|
| Spec id | DSK |
| Projects | `src/ui/apps/desktop` (Electron shell), `src/ui/apps/desktop-ui` (UI bundle served by the node), node-side hosting in `Watchpoint.Api` (§5) |
| Milestone | M1 (part 1), M2 (part 2), M3 (part 3); installers and shell updates in DEP (M11) |
| Tasks | T1.24 (§4–§8), T2.17 (§9–§10), T3.16 (§11–§12) |
| Design docs | [Desktop client](../../docs/clients/desktop-client.md), [Technology stack §2.4](../../docs/architecture/technology-stack.md#24-why-electron-for-the-desktop-client-and-not-tauri-or-a-native-ui-toolkit) |

## 1. Purpose and scope

The desktop client for Windows, Linux and macOS: a **thin, hardened Electron shell** that
connects to a Watchpoint site and displays the **desktop UI bundle served by the node**
(operation + configuration side), adding what a browser cannot do: secure token storage,
certificate pinning per site, several windows across monitors, bringing alarms to the front, OS
notifications, workstation identity, kiosk mode.

### 1.1 Key decision: the UI comes from the node

The shell does **not** embed the application UI. It loads `https://<node>/app/desktop/`. Reasons:

- the UI always has exactly the node's version (no client/server compatibility matrix; clients
  "update" when the node is upgraded);
- one build of the UI per release, served to every workstation;
- the API needs no CORS (same origin).

The shell itself changes rarely (security fixes, Electron upgrades) and is updated separately
(DEP). It embeds only **local pages** (`app://local/…`): site profiles, certificate trust prompt,
connection error page, about.

Security consequences handled in §6: the remote origin is pinned per profile, the preload bridge
is minimal and checks the sender origin, navigation outside the site origin is blocked.

## 2. Dependencies (all MIT)

Electron (latest stable, pinned), `electron-vite` (build), `electron-builder` (packaging, DEP),
`electron-log`, `bonjour-service` (part 2), `zod` (IPC argument validation). The desktop UI bundle
uses `@watchpoint/operation` and (M3) `@watchpoint/configuration`.

## 3. Project layout

```
src/ui/apps/desktop/                       Electron shell
├── electron.vite.config.ts
├── src/main/
│   ├── main.ts                            app lifecycle, single-instance lock
│   ├── profiles/ProfileStore.ts           site profiles (userData/profiles.json)
│   ├── trust/TrustManager.ts              certificate pinning per profile, trust prompt
│   ├── tokens/TokenVault.ts               safeStorage-encrypted refresh tokens
│   ├── windows/WindowManager.ts           main window (M1); additional windows, monitors, bounds (M2)
│   ├── security/Hardening.ts              session permissions, navigation guards, CSP for local pages
│   ├── ipc/Bridge.ts                      IPC handlers with origin checks and zod validation
│   ├── notify/Notifications.ts            (M2)
│   ├── sound/SoundLeader.ts               (M2)
│   ├── discovery/MdnsBrowser.ts           (M2)
│   ├── workstation/WorkstationIdentity.ts (M3)
│   └── kiosk/Kiosk.ts                     (M3)
├── src/preload/preload.ts                 contextBridge.exposeInMainWorld("watchpointDesktop", …)
└── src/local/                             local pages: profiles.html, trust.html, offline.html, about.html (small React app)

src/ui/apps/desktop-ui/                    the UI bundle served by the node at /app/desktop/
├── index.html, src/main.tsx               mounts OperationApp (+ configuration side from M3) with the desktop platform adapter
└── src/platform/DesktopPlatform.ts        implements OperationPlatform through window.watchpointDesktop
```

## 4. Site profiles and start-up (part 1)

### 4.1 Profiles

```ts
interface SiteProfile {
  id: string;                    // uuid
  name: string;                  // user label, default = site name from /system/info
  urls: string[];                // node base URLs in priority order, e.g. ["https://wp1.site.local:8443", "https://wp2.site.local:8443"]
  caFingerprintSha256?: string;  // pinned site CA (SEC §7) or undefined when the site uses a publicly trusted / enterprise certificate
  trustMode: "pinnedCa" | "system";
  lastUsedAt?: string;
  lastLogin?: string;            // convenience only
}
```

Stored in `userData/profiles.json` (no secrets). Command-line / policy file can pre-provision
profiles: `--profile-file=<path>` or `C:\ProgramData\Watchpoint\desktop-profiles.json`
(Windows) / `/etc/watchpoint/desktop-profiles.json` (Linux) / `/Library/Application
Support/Watchpoint/desktop-profiles.json` (macOS), merged read-only (mass deployment).

### 4.2 Start-up flow

1. Single-instance lock (a second launch focuses the existing window; `--new-instance` allowed
   for kiosk multi-screen setups).
2. If no profile → local page `profiles.html` (add site: URL; the shell fetches
   `GET /api/v1/system/info` and `GET /api/v1/system/ca` and starts the trust flow §4.3).
3. With a profile: try `urls` in order (`GET /api/v1/system/info`, timeout 5 s each); first
   success → load `https://<url>/app/desktop/?profile=<id>` in the main window.
4. All unreachable → `offline.html` with retry (automatic every 10 s) and profile switch.
5. When the loaded UI loses the node (live status disconnected for > 30 s and REST failing),
   the UI asks the shell (`bridge.failover()`) to try the next URL; the shell reloads the UI on
   the reachable node, keeping the session (refresh token) — CLU makes sessions valid on all
   nodes.

### 4.3 Certificate trust

- `session.defaultSession.setCertificateVerifyProc` is installed **once** and applies per
  hostname of the active profile:
  - `trustMode = system`: Chromium's default verification result is used unchanged;
  - `trustMode = pinnedCa`: the chain presented must end in a CA whose SHA-256 fingerprint equals
    `caFingerprintSha256` and the leaf must be valid for the host and time; otherwise rejected.
- First connection to a site with an untrusted certificate: the shell fetches the CA via
  `/system/ca` **over that same untrusted connection**, and shows `trust.html` with the
  fingerprint (formatted in groups of 4 hex), site name and instructions to compare it with the
  fingerprint shown on the server (`wpctl status` / setup wizard). Only explicit acceptance stores
  the pin. A changed fingerprint later → blocking warning page, no "continue anyway" button
  (an administrator must remove and re-add the profile).
- Certificate errors are never ignored globally (`app.on('certificate-error')` always rejects).

## 5. UI hosting on the node (part 1, node side)

`Watchpoint.Api` serves static bundles:

| Path | Folder | Notes |
|---|---|---|
| `/app/desktop/` | `<install>/ui/desktop/` | Desktop UI bundle (operation + configuration). `index.html` `Cache-Control: no-cache`; hashed assets `Cache-Control: public, max-age=31536000, immutable` |
| `/app/web/` | `<install>/ui/web/` | Web client bundle (WEB, M6) |
| `/` | redirect to `/app/web/` (M6); M1: redirect to `/app/desktop/` in Development only |

Response headers for `/app/*`: `Content-Security-Policy: default-src 'self'; script-src 'self';
style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src
'self' wss://<host>; frame-ancestors 'none'; base-uri 'self'; form-action 'self'`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy` restricted.

The desktop bundle is reachable by any authenticated or anonymous browser (it is just code);
**all authorisation is server-side** (API §5.3 — configuration endpoints require `ck=desktop`).

## 6. Hardening (part 1)

| Setting | Value |
|---|---|
| `BrowserWindow.webPreferences` | `contextIsolation: true`, `sandbox: true`, `nodeIntegration: false`, `webSecurity: true`, `allowRunningInsecureContent: false`, `spellcheck: false`, `devTools: false` in production builds (enabled with `--devtools` only if a local policy file allows it) |
| Navigation | `will-navigate` / `will-redirect`: allowed only within the active profile origin and `app://local`; external links (`https://` other origins) opened in the system browser after confirmation; `window.open` → handled by `setWindowOpenHandler`: internal routes become shell windows (M2), others denied |
| Permissions | `session.setPermissionRequestHandler`: allow `notifications`, `media` (audio/video playback for intercom later), `fullscreen`; deny everything else (geolocation, camera/microphone unless intercom feature enabled in M6+, clipboard-read, etc.) |
| Webviews | Disabled (`will-attach-webview` → prevent) |
| Protocols | `app://local` registered as privileged standard scheme serving packaged local pages only |
| IPC | Every handler checks `event.senderFrame.url` origin ∈ {active profile origin, `app://local`} and validates arguments with zod; unknown channels ignored |
| Downloads | `will-download`: save dialog, only from the profile origin |
| Updates of Electron | Quarterly or on security advisories (DEP) |

## 7. Preload bridge and token storage (part 1)

```ts
// window.watchpointDesktop (exposed by preload; all methods async)
interface WatchpointDesktopBridge {
  getInfo(): Promise<{ shellVersion: string; platform: "win32" | "linux" | "darwin"; profileId: string; clientInstanceId: string; workstationId?: string }>;
  tokens: { load(userKey: string): Promise<string | undefined>; save(userKey: string, token: string | undefined): Promise<void> };
  failover(): Promise<void>;                               // try next node URL
  openExternal(url: string): Promise<void>;                // asks confirmation
  // part 2:
  openWindow(route: string, options?: { monitor?: number; bounds?: Rect; kind?: "panel" | "workspace" }): Promise<string>;
  bringToFront(): Promise<void>;
  notify(title: string, body: string, route?: string): Promise<void>;
  sound: { requestLeadership(): Promise<boolean>; onLeadershipChanged(cb: (leader: boolean) => void): () => void };
  monitors(): Promise<Array<{ id: number; bounds: Rect; primary: boolean }>>;
  // part 3:
  workstation: { status(): Promise<WorkstationStatus>; enroll(code: string): Promise<WorkstationStatus> };
}
```

- `clientInstanceId`: UUID generated at first launch, stored in `userData/instance.json`.
- `TokenVault`: refresh tokens per (profileId, userKey) encrypted with
  `safeStorage.encryptString` (DPAPI on Windows, Keychain on macOS, libsecret/kwallet on
  Linux) in `userData/tokens.bin`. If `safeStorage.isEncryptionAvailable()` is false (headless
  Linux without a keyring), tokens are **not persisted** (user signs in at each start) and the
  UI shows an informational notice.
- Logs: `electron-log` to `userData/logs/shell.log` (rotation 5 × 5 MB), never tokens or URLs with
  query strings.

## 8. Development and packaging (part 1)

- `pnpm --filter desktop dev`: starts Electron pointing to a node URL from `.env`
  (`WATCHPOINT_DEV_URL=http://localhost:5080`) — in Development, `http://localhost` profiles are
  allowed without TLS (flag shown in the title bar).
- The desktop UI bundle in development is served by Vite with a proxy to the node (`/api`,
  `/hub`) so the same-origin model holds.
- Dev packaging (`pnpm --filter desktop package:dir`) produces unpacked apps for manual testing;
  signed installers are DEP's job.

## 9. Windows and monitors (part 2, T2.17)

- `openWindow(route, options)`: creates a shell window loading `/app/desktop/#<route>` on the
  same profile origin (same session/cookies; tokens shared via the vault and the UI's auth
  provider). Kinds: `panel` (undocked panel such as the alarm console), `workspace` (full window
  belonging to a workspace).
- Bounds and monitor per window persisted per profile and user (`userData/windows.json`);
  restored at next start; windows whose monitor disappeared are moved to the primary monitor.
- Main window close → asks whether to close all windows; closing the last window quits.
- `bringToFront`: restore if minimised, `show`, `focus`, flash taskbar (Windows) / bounce dock
  (macOS); on Linux, `focus` plus urgency hint.
- OS notifications via Electron `Notification`; click → focus main window and navigate to
  `route`.
- Sound leadership: main process elects one window (the most recently focused visible window
  of the profile) as sound leader; changes broadcast to windows (UIO §9).

## 10. Site discovery (part 2)

- Node side: `Watchpoint.Discovery` (small module in `Watchpoint.Api`) advertises DNS-SD service
  `_watchpoint._tcp` on the main port with TXT records `site=<name>`, `node=<nodeName>`,
  `v=<apiVersion>` using `Makaretu.Dns.Multicast` (MIT). Setting `Discovery:Enabled` (default true;
  disabled automatically in containers).
- Shell side: `MdnsBrowser` lists services for 3 s when the profiles page opens; results offered
  as "Sites found on this network"; adding one still requires the trust flow.

## 11. Workstation identity (part 3, T3.16)

- Enrolment: an administrator generates a one-time **workstation enrolment code** in the
  configuration side (*System → Workstations*); on the workstation, the user (or installer)
  enters it in the shell (`workstation.enroll`).
- The shell generates a key pair (ECDSA P-256) and stores it in the OS store when possible
  (Windows: certificate store CurrentUser\My via a small native helper invoked by `certutil`/
  PowerShell; macOS: keychain via `security`; Linux: PKCS#8 file encrypted with `safeStorage`),
  sends a CSR with the code to `POST /api/v1/workstations/enroll`; the node (SEC) issues a client
  certificate (2 years) with the workstation id in the subject.
- Sign-in through `POST /api/v1/auth/desktop/sign-in` with client certificate (Electron
  `select-client-certificate` event returns the enrolled cert for the profile origin) →
  `wksVerified=true` (IDN §12).
- Workstation list: name, OS, shell version, last seen, enrolled by; revoke → certificate
  revoked (node keeps a revocation list).

## 12. Kiosk mode (part 3)

- Local policy file (§4.1 locations) `kiosk: { enabled: true, profileId, route?, windows?: [...] }`:
  full-screen, no menu, `Alt+F4`/`Cmd+Q` disabled (exit only with a configured key sequence plus an
  administrator password check through the node), auto-restart of crashed renderer, auto sign-in
  is **not** supported (an operator must sign in), screen saver inhibited.
- Multi-screen walls: several kiosk windows with routes per monitor (`windows` array).

## 13. Errors and observability

Shell errors are local (no error codes from the node); messages translated in the local pages
(en/fr/ar bundles embedded). Shell logs (electron-log) at info level: start, profile used, node
reached, trust decisions (fingerprints), windows opened, crashes (`render-process-gone` →
reload with notice).

## 14. Tests

Unit tests with Vitest for main-process modules (Electron APIs mocked); end-to-end with
**Playwright for Electron** (`_electron.launch`) against a test node (e2e harness, T1.25).

| Id | Layer | Test |
|---|---|---|
| DSK-U-001 | U | ProfileStore CRUD and policy-file merge (read-only entries) |
| DSK-U-002 | U | URL order and failover selection |
| DSK-U-003 | U | TrustManager: pinned CA accepts matching chain, rejects other CA |
| DSK-U-004 | U | TrustManager: system mode passes Chromium result through |
| DSK-U-005 | U | Fingerprint change → blocked without continue option |
| DSK-U-006 | U | `certificate-error` always rejected |
| DSK-U-007 | U | Navigation guard allows profile origin and app://local only |
| DSK-U-008 | U | External links open in system browser after confirmation |
| DSK-U-009 | U | Permission handler allows notifications/media/fullscreen only |
| DSK-U-010 | U | Webview attach prevented |
| DSK-U-011 | U | IPC handler rejects other origins |
| DSK-U-012 | U | IPC argument validation rejects malformed input |
| DSK-U-013 | U | TokenVault encrypts with safeStorage; not persisted when unavailable |
| DSK-U-014 | U | clientInstanceId generated once and persisted |
| DSK-U-015 | U | Logs never contain tokens or query strings |
| DSK-U-016 | U | Single-instance lock focuses existing window |
| DSK-U-017 | U | Offline page retry every 10 s |
| DSK-U-018 | U | Node static hosting headers (CSP, cache) — node-side test in `Watchpoint.Api.Tests` |
| DSK-U-019 | U | Development mode allows http://localhost with title flag; production refuses http |
| DSK-U-020 | E | Playwright-Electron: add profile, trust prompt, sign in, explorer visible (part of E-001) |
| DSK-U-021 | U | openWindow creates window on requested monitor with same origin |
| DSK-U-022 | U | Window bounds persisted and restored; missing monitor → primary |
| DSK-U-023 | U | Closing main window asks to close all |
| DSK-U-024 | U | bringToFront restores and focuses |
| DSK-U-025 | U | Notifications click navigates |
| DSK-U-026 | U | Sound leadership single leader, transfer on focus |
| DSK-U-027 | U | mDNS browse lists services with TXT data |
| DSK-U-028 | U | Node advertiser publishes service (node-side test) |
| DSK-U-029 | E | Undock alarm console into a second window (Playwright-Electron) |
| DSK-U-030 | U | Workstation key generation and CSR content |
| DSK-U-031 | U | Enrolment with code; certificate stored |
| DSK-U-032 | U | Client certificate selected only for the profile origin |
| DSK-U-033 | U | Kiosk policy: full screen, exit sequence requires admin check |
| DSK-U-034 | U | Renderer crash → reload with notice |
| DSK-U-035 | E | Failover between two nodes keeps session (with CLU, M9) |

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.24 | §4–§8 (incl. node hosting §5) | DSK-U-001…DSK-U-020 |
| T2.17 | §9, §10 | DSK-U-021…DSK-U-029 |
| T3.16 | §11, §12 | DSK-U-030…DSK-U-034 (DSK-U-035 enabled in M9) |
