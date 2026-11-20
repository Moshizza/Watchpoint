# VID — Video

| | |
|---|---|
| Spec id | VID |
| Projects | `src/service/Watchpoint.Video`, `src/ui/packages/video` (`@watchpoint/video`), integration contract additions in `Watchpoint.Integration.Abstractions/Features/Video/` |
| Milestone | M6 |
| Tasks | T6.05 (§4–§7), T6.06 (§8–§9) |
| Event id range | 3300–3359 |
| Spike | T0.10 decision record (go2rtc RTSP → WebRTC in Electron on Windows/Linux/macOS, H.264/H.265) |
| Design docs | [Video](../../docs/modules/video.md) |

## 1. Purpose and scope

Show **live and recorded video from integrated systems** (VMS, NVRs, cameras) without
recording it: stream brokering through a **media gateway** (go2rtc), one-time stream tickets,
rights (live, playback look-back, export, PTZ, unmask), PTZ arbitration, snapshots, exports,
bookmarks, linked video for devices/alarms/synoptics, and the client video UI (tiles, layouts,
playback timeline, PTZ controls, joystick).

Not in scope: recording, video analytics, video walls (later).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST (child process supervision via IHO `ProcessSupervisor`), IRT (connections implementing `IVideoProvider`), INT, REG (cameras, links), MOD (`video.camera` type), RGT (`video.*` permissions), FIL (snapshots, exports), ALM (Snapshot/Bookmark actions), AUD, SEC (stream credentials), go2rtc (MIT, bundled binary per platform), YARP (MIT, reverse proxy) | UIO (alarm details video, device panels), SYN (camera cones, video tiles), WEB, C-RTSP, C-MILE (M12) |

## 3. Layout

Log event ids: 3300–3359 ([README §8](../README.md#8-observability-conventions)).

```
Watchpoint.Video/
├── Contracts/IVideoService.cs, StreamRequest.cs, StreamTicket.cs, PlaybackRequest.cs, PtzArbiter contracts, VideoErrors.cs
├── Internal/
│   ├── Gateway/Go2rtcSupervisor.cs (process, config, health), Go2rtcApiClient.cs, GatewayRegistry.cs (per node; edge gateways M9)
│   ├── Streams/StreamBroker.cs (source resolution, registration, reuse, expiry), TicketService.cs
│   ├── Proxy/VideoProxyEndpoints.cs (YARP: /video/v1/ws, /video/v1/webrtc)
│   ├── Ptz/PtzArbiter.cs, Ptz/PtzService.cs
│   ├── Recording/TimelineService.cs, ExportService.cs, BookmarkService.cs, SnapshotService.cs
│   ├── Alarms/SnapshotAction.cs, BookmarkAction.cs (ALM action kinds)
│   └── Storage/VideoDbContext.cs (exports, ptz sessions history)
├── VideoModule.cs, VideoLog.cs
packages/video/src/
├── player/  VideoPlayer (WebRTC via go2rtc protocol, MSE fallback), StreamSelector (main/sub), DigitalZoom
├── tiles/   VideoTile, TileGrid, Layouts (1/4/9/16/custom), FullScreen
├── playback/ Timeline (records, motion, bookmarks), PlaybackControls, SyncGroup, InstantReplay
├── ptz/     PtzOverlay, ClickToCentre, AreaZoom, PresetsMenu, JoystickAdapter (Gamepad API)
└── actions/ Snapshot, ExportDialog, BookmarkDialog
```

## 4. Media gateway (T6.05)

- **go2rtc** binary shipped per platform in `<install>/go2rtc/` (version pinned per release;
  licence MIT). One gateway per node in M6; edge gateways on remote hosts later (IHO hosts can run
  go2rtc too — `GatewayRegistry` abstracts location).
- `Go2rtcSupervisor` starts it through the IHO `ProcessSupervisor` with a generated YAML config:
  `api.listen: 127.0.0.1:<port>` (loopback only, random port), `rtsp.listen: 127.0.0.1:<port>`,
  `webrtc.listen: :<Video:WebRtcPort>` (UDP+TCP, default 8555) with `candidates` = node
  addresses (`Video:WebRtcCandidates`, default all non-loopback interface addresses), no static
  streams, `log.level: warn`, `ffmpeg.bin` = bundled **LGPL** FFmpeg build (transcoding only when
  needed). Health: `GET /api/` every 10 s; restart policy as IHO §6.2.
- Streams are registered dynamically: `PUT /api/streams?name=<streamKey>&src=<source>`; removed
  when unused for `Video:StreamIdleTimeout` (60 s after the last viewer leaves).
- Clients never talk to go2rtc directly: the node exposes **proxy endpoints** (YARP) that
  validate a ticket, then forward to go2rtc on loopback:
  - `GET /video/v1/ws?ticket=` → go2rtc `/api/ws?src=<streamKey>` (WebSocket: WebRTC signalling
    and MSE fallback);
  - `POST /video/v1/webrtc?ticket=` → go2rtc `/api/webrtc?src=` (WHEP-style SDP exchange).
  Media itself flows by WebRTC (UDP 8555 / TCP 8555) directly between client and go2rtc.

## 5. Video provider contract (T6.05)

Added to INT (`Features/Video/`), implemented by video connectors (C-RTSP, C-MILE…):

```csharp
public interface IVideoProvider
{
    VideoCapabilities GetCapabilities(DeviceHandle camera);   // live, playback, ptz kinds, presets, export, bookmarks, snapshot, audio, privacy masks
    ValueTask<Result<StreamSource>> GetLiveSourceAsync(DeviceHandle camera, StreamProfile profile /* Main | Sub | Auto */, CancellationToken ct);
    ValueTask<Result<StreamSource>> GetPlaybackSourceAsync(DeviceHandle camera, DateTimeOffset start, double speed, CancellationToken ct);
    ValueTask<Result<RecordingTimeline>> GetTimelineAsync(DeviceHandle camera, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    ValueTask<Result> PtzAsync(DeviceHandle camera, PtzOperation operation, CancellationToken ct);
    ValueTask<Result<IReadOnlyList<PtzPreset>>> GetPresetsAsync(DeviceHandle camera, CancellationToken ct);
    ValueTask<Result<Snapshot>> GetSnapshotAsync(DeviceHandle camera, DateTimeOffset? at, CancellationToken ct);
    ValueTask<Result<VideoExportHandle>> StartExportAsync(VideoExportRequest request, CancellationToken ct);
    ValueTask<Result<VideoExportStatus>> GetExportStatusAsync(VideoExportHandle handle, CancellationToken ct);   // includes download Stream when ready
    ValueTask<Result> CreateBookmarkAsync(DeviceHandle camera, DateTimeOffset at, TimeSpan? duration, string text, CancellationToken ct);
}

public sealed record StreamSource(Uri Url /* rtsp, rtsps, http(s) mjpeg/flv */, StreamCredentials? Credentials,
    DateTimeOffset? ExpiresAt /* short-lived VMS tokens */, string? Codec /* h264 | h265 | mjpeg, if known */, bool Audio);
public abstract record PtzOperation
{
    public sealed record ContinuousMove(double Pan, double Tilt, double Zoom /* −1..1 */) : PtzOperation;
    public sealed record Stop : PtzOperation;
    public sealed record GotoPreset(string PresetId) : PtzOperation;
    public sealed record Home : PtzOperation;
    public sealed record ClickToCentre(double X, double Y /* 0..1 in the image */, double? ZoomFactor) : PtzOperation;
    public sealed record AreaZoom(double X, double Y, double Width, double Height) : PtzOperation;
    public sealed record Focus(double Speed) : PtzOperation;
    public sealed record Iris(double Speed) : PtzOperation;
}
public sealed record RecordingTimeline(IReadOnlyList<TimeRange> Recordings, IReadOnlyList<TimeRange> Motion, IReadOnlyList<VideoBookmark> Bookmarks);
```

Standard type `video.camera` (added to SCM library by T6.05): capabilities `health`, `video`
(properties `recording` boolean, `streaming` enum; attributes `azimuth/fov/range` via REG
geometry), `ptz` (optional; commands `ptz.gotoPreset {preset}` (normal), `ptz.home`), so that
CMD/automations can drive presets; continuous PTZ goes through §7, not CMD (latency).

Isolated/remote hosts: `IVideoProvider` calls are bridged like other features (IHO §5);
stream URLs returned must be reachable **by the gateway** (for remote hosts in M9, an edge
gateway on the host).

## 6. Stream requests and tickets (T6.05)

```csharp
public interface IVideoService
{
    Task<Result<StreamTicket>> RequestLiveAsync(DeviceId camera, StreamProfile profile, VideoRequester requester, CancellationToken ct);
    Task<Result<StreamTicket>> RequestPlaybackAsync(DeviceId camera, DateTimeOffset start, double speed, VideoRequester requester, CancellationToken ct);
    Task<Result<RecordingTimeline>> GetTimelineAsync(DeviceId camera, DateTimeOffset from, DateTimeOffset to, VideoRequester requester, CancellationToken ct);
}
public sealed record StreamTicket(string Ticket /* 256-bit random, base64url */, DateTimeOffset ExpiresAt /* 30 s to connect */,
    string WsUrl, string WebRtcUrl, StreamInfo Info /* codec, profile, playback start */);
```

1. Rights (RGT): live → `video.live` on the camera; playback → `video.playback` with
   `MaxLookback` constraint (start ≥ now − lookback, else `video.playback.lookback_exceeded`);
   client kind allowed (mobile: live only in v1).
2. Resolve the camera's connection → `IVideoProvider` (`video.camera.no_provider` otherwise),
   capability check.
3. **Reuse**: live streams are shared per (camera, profile) — `streamKey = live-<camera>-<profile>`;
   playback streams are per viewer (`pb-<uuid>`).
4. Get source (with credentials from the provider; credentials embedded in the go2rtc source URL
   only on loopback API, never logged — redaction of `rtsp://user:pass@`), register with go2rtc
   if not registered; refresh sources that expire (`ExpiresAt` − 10 s) transparently.
5. Issue a **one-time ticket** bound to (user session, stream key, client IP), valid 30 s for the
   first connection; the proxy consumes it. Session revocation closes the proxied WebSocket.
6. Audit `video.liveViewed` (aggregated per user × camera × 10 min) and `video.playbackViewed`
   (range) — AUD action catalogue additions.

Limits: `Video:MaxStreamsPerUser` (default 32), `Video:MaxPlaybackSessions` (default 64 per
node); 429-like `video.limit.reached`.

## 7. PTZ arbiter (T6.05)

- PTZ requests (`POST /api/v1/video/cameras/{id}/ptz`, or WebSocket channel
  `/video/v1/ptz?ticket=` for continuous moves at up to 10 messages/s) need `video.ptz`.
- **Arbitration** per camera: a user takes the PTZ lock on first move; the lock lasts while
  moves continue + `Video:PtzHoldTime` (default 30 s idle). Another user's request: allowed if
  their **PTZ priority** is higher (priority = user setting 1–10, default 5; per-camera override;
  automations/alarm actions priority 8), otherwise refused with `video.ptz.locked { by, until }`
  shown in the UI. Equal priority → first holder keeps it.
- Continuous moves are auto-stopped if no update arrives within 1 s (dead-man), protecting
  against lost connections.
- Presets/home also available as CMD commands (alarm actions, automations) and respect the lock
  (automation priority).

## 8. Video UI (T6.06)

- **VideoPlayer**: requests a ticket, opens WebRTC (go2rtc `webrtc` over WS signalling); fallback
  to MSE over the same WS when WebRTC fails within 5 s (codec unsupported, UDP blocked); shows
  status overlays (connecting, reconnecting, no rights, camera offline, source error with plain
  message); auto-reconnect with backoff; stops when hidden (IntersectionObserver / tab hidden).
- Stream selection: tile area < 640×360 → Sub, else Main (hysteresis), manual override menu.
- **Tiles and layouts**: `TileGrid` with layouts 1, 2×2, 3×3, 4×4, 1+5, 1+7, custom (saved in the
  workspace); drag cameras from trees/search/alarms into tiles; full screen; digital zoom (wheel,
  pinch) with mini-map; per-tile toolbar (snapshot, replay, playback, PTZ, export, bookmark,
  audio toggle where allowed).
- **Linked video**: alarm details and device panels show the device's linked cameras (REG links
  `camera-for`) automatically, live by default and "play from alarm time − 10 s" button.
- **Playback**: timeline (zoomable from 1 min to 7 days) with recordings, motion and bookmarks
  (timeline service, cached per range), play/pause/speed (¼×–16×)/step (when provider supports)/
  go to time; **sync group**: tiles in playback mode can be synchronised (one master clock; other
  tiles re-seek when drift > 1 s); instant replay (−10/−30 s).

## 9. PTZ controls and joystick (T6.06)

- On-tile PTZ overlay: directional pad and zoom slider (continuous moves), click-to-centre
  (single click in PTZ mode), area zoom (drag rectangle) when supported, presets menu, home, lock
  indicator ("controlled by S. Benali, 12 s").
- **Joystick** (desktop and Chromium-based browsers): Gamepad API; axis mapping configurable
  (pan, tilt, zoom twist/throttle), dead zone 0.1, buttons → presets 1–9 / next camera / snapshot;
  messages throttled to 10/s; the joystick controls the **focused tile**.
- Keyboard: arrows pan/tilt, `+`/`-` zoom, digits presets when the tile is focused.

## 10. Snapshots, exports, bookmarks

- Snapshot: provider snapshot (or a frame grabbed by go2rtc `/api/frame.jpeg?src=` when the
  provider lacks it), stored in FIL `video.snapshot` with references to the alarm/event; ALM
  action `Snapshot` (linked cameras) now implemented.
- Export: `video.export` permission; request (cameras, range ≤ `Video:MaxExportDuration` 2 h,
  format from capabilities, optional password) → job; status polled from the provider; when
  ready, the file is streamed into FIL `video.export` (retention 30 days) and the user notified;
  audited with range.
- Bookmark: `CreateBookmarkAsync` from UI, ALM `Bookmark` action, automations.

## 11. API

| Method and path | Permission | Description |
|---|---|---|
| `POST /api/v1/video/cameras/{id}/live` `{ profile }` | `video.live` | → ticket |
| `POST /api/v1/video/cameras/{id}/playback` `{ start, speed }` | `video.playback` | → ticket |
| `GET /api/v1/video/cameras/{id}/timeline?from=&to=` | `video.playback` | Timeline |
| `GET /api/v1/video/cameras/{id}/capabilities` | `video.live` | |
| `POST /api/v1/video/cameras/{id}/ptz` / `GET …/presets` | `video.ptz` | |
| `POST /api/v1/video/cameras/{id}/snapshot` `{ at? }` | `video.live` (or playback for past) | → FileInfo |
| `POST /api/v1/video/exports` | `video.export` | → job |
| `POST /api/v1/video/cameras/{id}/bookmarks` | `video.playback` | |
| `GET /video/v1/ws`, `POST /video/v1/webrtc`, `GET /video/v1/ptz` | ticket | Proxied media signalling |
| `GET /api/v1/video/gateways` | `admin.health.view` | Gateway status |

## 12. Settings, errors, observability

| Key | Default |
|---|---|
| `Video:WebRtcPort` / `WebRtcCandidates` | 8555 / auto |
| `Video:StreamIdleTimeout` | 60 s |
| `Video:MaxStreamsPerUser` / `MaxPlaybackSessions` | 32 / 64 |
| `Video:PtzHoldTime` | 30 s |
| `Video:MaxExportDuration` | 2 h |

Errors: `video.camera.no_provider`, `video.camera.offline`, `video.capability.unsupported`,
`video.playback.lookback_exceeded`, `video.ticket.invalid`, `video.ticket.expired`,
`video.ptz.locked`, `video.limit.reached`, `video.gateway.unavailable`, `video.source.failed`,
`video.export.failed`.

| EventId | Level | Message |
|---|---|---|
| 3300 | Information | Media gateway started on node (WebRTC {Port}, candidates {Candidates}) |
| 3301 | Error | Media gateway exited ({ExitCode}); restarting |
| 3310 | Debug | Stream {StreamKey} registered for camera {Camera} ({Profile}) |
| 3311 | Warning | Stream source for {Camera} failed: {Error} |
| 3320 | Information | PTZ lock on {Camera} taken by {User} (priority {Priority}) |
| 3330 | Information | Export {ExportId} for {Cameras} cameras ready ({Bytes} bytes) |

Metrics: `watchpoint.video.streams` (gauge live/playback), `watchpoint.video.viewers` (gauge),
`watchpoint.video.tickets` (counter outcome), `watchpoint.video.ptz.commands` (counter).

## 13. Security

- go2rtc API and RTSP bound to loopback; only the WebRTC port is exposed; the proxy validates
  tickets (one-time, short-lived, bound to session and IP) and enforces session revocation.
- Camera credentials only travel from the provider to go2rtc on loopback; URLs with credentials
  are redacted in logs and diagnostics.
- Bundled FFmpeg is an LGPL build (no GPL components) — licence check in CI (DEP).

## 14. Tests

| Id | Test |
|---|---|
| VID-U-001 | go2rtc config generation (loopback API/RTSP, WebRTC port, candidates, ffmpeg path) |
| VID-U-002 | Supervisor health checks and restarts (fake process) |
| VID-U-003 | Stream registration/removal via API client (fake go2rtc) |
| VID-U-004 | Idle timeout removes streams after last viewer |
| VID-U-005 | Live stream reuse per camera/profile; playback per viewer |
| VID-U-006 | Source expiry refresh before ExpiresAt |
| VID-U-007 | Rights: live, playback lookback, client kind |
| VID-U-008 | No provider / capability unsupported / camera offline errors |
| VID-U-009 | Ticket: random, one-time, 30 s, bound to session and IP |
| VID-U-010 | Proxy validates ticket and forwards to loopback; rejects reuse |
| VID-U-011 | Session revocation closes proxied WebSocket |
| VID-U-012 | Credential redaction in logs |
| VID-U-013 | Limits per user and per node |
| VID-U-014 | Audit aggregation for live views; playback ranges audited |
| VID-U-015 | Timeline retrieval and caching |
| VID-U-016…020 | PTZ arbiter: first holder, priority override, equal priority, hold time, dead-man stop |
| VID-U-021 | PTZ WebSocket throttling 10/s |
| VID-U-022 | Preset/home via CMD respect the lock with automation priority |
| VID-U-023 | Snapshot via provider or frame fallback; FIL storage and references |
| VID-U-024 | ALM Snapshot and Bookmark actions |
| VID-U-025 | Export job lifecycle, max duration, FIL streaming, notification, audit |
| VID-U-026 | Bookmark creation |
| VID-U-027 | `video.camera` type and capabilities validate in the library |
| VID-U-028 | IVideoProvider bridged through IHO (isolated host) |
| VID-U-029 | Gateways API |
| VID-U-030…035 | Error translations; permission matrix per endpoint; capabilities endpoint; multiple gateways registry abstraction; stream key collision safety; restart of go2rtc re-registers active streams |
| VID-U-036 | Player WebRTC connect flow (mock signalling) and status overlays |
| VID-U-037 | Fallback to MSE after 5 s failure |
| VID-U-038 | Auto-reconnect with backoff; stop when hidden |
| VID-U-039 | Stream profile selection with hysteresis; manual override |
| VID-U-040 | Layouts and custom layout saved in workspace |
| VID-U-041 | Drag cameras into tiles from tree/search/alarms |
| VID-U-042 | Full screen and digital zoom with mini-map |
| VID-U-043 | Tile toolbar actions gated by capabilities and rights |
| VID-U-044 | Linked video in alarm details and device panel |
| VID-U-045 | Play from alarm time − 10 s |
| VID-U-046 | Timeline zoom levels and marks rendering |
| VID-U-047 | Playback controls (speed, step, go to time) |
| VID-U-048 | Sync group drift correction |
| VID-U-049 | Instant replay |
| VID-U-050 | PTZ overlay continuous moves and stop on release |
| VID-U-051 | Click-to-centre and area zoom |
| VID-U-052 | Presets menu and home |
| VID-U-053 | Lock indicator and refusal message |
| VID-U-054 | Joystick mapping, dead zone, throttling, focused tile |
| VID-U-055 | Keyboard PTZ |
| VID-U-056 | Snapshot/export/bookmark dialogs |
| VID-U-057 | RTL layout of tiles and controls |
| VID-U-058 | Accessibility (labels, focus) |
| VID-U-059 | Mobile client kind: live only |
| VID-U-060 | Performance: 16 tiles live in desktop (Electron) with sub-streams — CPU budget (benchmark) |
| VID-I-001 | go2rtc real binary + RTSP test server (`mediamtx` container, MIT) → WebRTC in headless Chromium (Playwright) shows frames |
| VID-I-002 | H.265 source: WebRTC or MSE fallback per platform matrix (CI Linux; Windows runner) |
| VID-I-003 | Ticket replay and expiry through real proxy |
| VID-I-004 | Gateway crash: streams re-registered, players reconnect |
| VID-I-005 | PTZ against an ONVIF PTZ simulator (C-RTSP test fixtures) |
| VID-I-006 | 50 concurrent viewers of one camera share one source connection |

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| T6.05 | §4–§7, §10–§13 | VID-U-001…VID-U-035, VID-I-001…VID-I-006 |
| T6.06 | §8–§9 | VID-U-036…VID-U-060 |
