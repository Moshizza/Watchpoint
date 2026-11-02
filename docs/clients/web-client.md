# Web Client — Operation Only

> Requirement: the web interface offers client usage (operation) only, no configuration,
> without impacting the performance of the system.

## 1. What it is

The same React UI as the desktop client, built **without** the configuration modules (the
code is not even shipped), served by the nodes over HTTPS at `https://<site>/`. It works
in current Chrome, Edge, Firefox and Safari, on desktops, laptops and tablets, and on
Linux/macOS machines where the desktop client is not installed.

## 2. Features

| Feature | Web client |
|---|---|
| Alarm console (acknowledge, comment, procedures, transfer, close) | ✓ |
| Event journal (live and history, filters, export) | ✓ |
| Device explorer, device pages, commands (within rights and client-kind constraints) | ✓ |
| Synoptics viewer (plans, maps, dashboards) | ✓ |
| Video live and playback (WebRTC), PTZ on screen | ✓ |
| Video export download | ✓ (if allowed) |
| Workspaces | Single-window layouts (tabs and split views); multi-monitor workspaces are desktop-only |
| Automations manual start | ✓ |
| Reports viewing and download | ✓ |
| Notifications | Browser notifications (when tab open), sounds |
| Configuration of anything | ✗ (by design; the API refuses configuration from web sessions) |
| USB joystick PTZ | Basic (Gamepad API in Chromium browsers) |
| Kiosk/wall modes | ✗ (desktop client) |

Rights can further restrict web usage per user (client access) and per permission
(e.g. "critical commands only from the desktop client").

## 3. Performance protection

The web client must never degrade supervision. Measures:

| Measure | Effect |
|---|---|
| Static assets (JS/CSS/fonts) are pre-built, compressed, cached (immutable hashes, HTTP caching) | Serving the app costs almost nothing after first load |
| Server-side filtering and paging for every list; virtualised tables | No large payloads |
| Subscriptions limited to what is on screen (viewport-based for synoptics, visible rows for lists) | Live traffic proportional to what the user sees |
| Coalescing of state updates for slow connections | Slow browsers cannot back up the node |
| Per-session and per-user rate limits and subscription limits | A misbehaving tab cannot overload the node |
| Video goes browser ↔ media gateway (WebRTC), not through the API | Video load is isolated and can run on media nodes |
| **Front role** nodes (scale-out) | Large sites can serve web users from dedicated nodes, keeping integration/pipeline nodes free |
| Web seat licensing | Caps concurrency to what the site sized for |
| Priority | Live hub prioritises alarm messages over state updates and over web history queries; long history queries run with time limits and lower database priority |

## 4. Security

- HTTPS only, HSTS, secure cookies for refresh tokens (HttpOnly, SameSite=Strict), access
  tokens kept in memory.
- Strict CSP, no third-party scripts, no CDN (works offline on site networks).
- Sign-in with local, directory, SSO and MFA (WebAuthn works in browsers).
- Optional restriction of web access to given networks (IP ranges) and to SSO only.
- Can be published to the internet behind a reverse proxy/WAF if the customer chooses;
  default is local network only.
