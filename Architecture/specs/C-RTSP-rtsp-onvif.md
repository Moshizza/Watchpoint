# C-RTSP — Generic RTSP / ONVIF Camera Connector

| | |
|---|---|
| Spec id | C-RTSP |
| Projects | `src/integrations/Watchpoint.Integration.Cameras` (package), tests `tests/integrations/Watchpoint.Integration.Cameras.Tests` |
| Package id | `watchpoint.integration.cameras` (builtin, licence class `standard`) |
| Milestone | M6 |
| Tasks | T6.07 |
| Event id range | 5500–5599 |
| Reference | ONVIF Core, Media (ver10), Media2 (ver20), PTZ, Events and Device specifications; WS-Discovery 1.0 (OASIS); RFC 2326 / 7826 (RTSP) — all public |

## 1. Purpose and scope

Bring IP cameras that are **not behind a VMS** into Watchpoint for live video, snapshots, PTZ
and simple camera events:

- **ONVIF mode**: WS-Discovery on the local network, device information, media profiles and
  stream URIs (Profile S, Media2/Profile T when available), snapshot URI, PTZ (continuous move,
  presets, home), events by PullPoint subscription (motion, tamper, digital inputs).
- **Plain RTSP mode**: cameras declared manually with RTSP URLs (main and sub stream), health by
  RTSP probing.

Implements `IVideoProvider` (VID §5) for **live** and snapshots; playback/export are not
provided (cameras' SD-card recordings — ONVIF Profile G — are a later extension).

## 2. Dependencies

Abstractions, SDK (ConnectorBase, PollingLoop, ISecureHttp). ONVIF SOAP client implemented in
the package (`System.Xml`, no WCF): request templates, WS-Security UsernameToken with
PasswordDigest, HTTP digest fallback. No third-party ONVIF library. Tests: `OnvifTestDevice`
(Kestrel SOAP responder in the test project emulating a camera, including WS-Discovery responder
and PullPoint events) and `mediamtx` container (MIT) as RTSP source.

## 3. Layout

```
Watchpoint.Integration.Cameras/
├── manifest.json, settings/cameras.settings.json
├── types/cameras.server.json (connection root; cameras use the standard `video.camera` type)
├── i18n/, docs/
└── src/
    ├── CamerasConnector.cs (ICommandExecutor for ptz.gotoPreset/home, IVideoProvider, IDiscovery, IDiagnostics)
    ├── Onvif/SoapClient.cs, WsSecurity.cs, DeviceService.cs, MediaService.cs, Media2Service.cs, PtzService.cs, EventsService.cs (PullPoint), Faults.cs
    ├── Discovery/WsDiscoveryProbe.cs (UDP multicast 239.255.255.250:3702)
    ├── Rtsp/RtspProbe.cs (OPTIONS/DESCRIBE with digest auth, SDP codec parsing)
    ├── Cameras/CameraSession.cs (per camera: capabilities, profiles cache, time offset, event subscription)
    └── CamerasLog.cs, CamerasDiagnostics.cs
```

## 4. Settings (`settings/cameras.settings.json`)

| Field | Type | Default | Rules |
|---|---|---|---|
| `cameras` | array (≤ 2,000) | [] | |
| `cameras[].id` | string `^[A-Za-z0-9._-]{1,64}$` | required | native id |
| `cameras[].name` | string | id | |
| `cameras[].mode` | `onvif`, `rtsp` | `onvif` | |
| `cameras[].address` | host/IP (onvif) | required for onvif | device service `http(s)://<address>[:port]/onvif/device_service` unless `deviceServiceUrl` given |
| `cameras[].deviceServiceUrl` | URL | null | |
| `cameras[].https` | boolean | false | TLS options as ISecureHttp |
| `cameras[].credentials` | credential set id | `default` | secrets `user:<id>`, `password:<id>` (secret patterns, SCM manifest) |
| `cameras[].mainUrl` / `subUrl` | RTSP URL | required main for rtsp mode | onvif mode: derived from profiles (override allowed) |
| `cameras[].profileMain` / `profileSub` | ONVIF profile token | auto (highest/lowest resolution H.264/H.265) | |
| `cameras[].events` | boolean | true | onvif PullPoint |
| `cameras[].ptz` | `auto`, `off` | `auto` | |
| `cameras[].geometry` | `{ azimuth, fov, range }` | null | passed as attributes for REG geometry suggestions |
| `credentialSets` | array | `[{ id: "default" }]` | |
| `healthInterval` | duration | PT60S | GetSystemDateAndTime (onvif) or RTSP OPTIONS (rtsp) |
| `discovery.enabled` | boolean | true | WS-Discovery in the wizard and diagnostics |
| `discovery.interfaces` | list of interface names/CIDRs | all | |

## 5. Behaviour

### 5.1 Inventory

Server `cameras.server` (native id `cameras`) → one `video.camera` device per configured
camera (native id `id`, vendor name from GetDeviceInformation: manufacturer, model, firmware,
serial as attributes). PTZ capability flagged unsupported (`UnsupportedOptionalCapabilities`)
when the camera has no PTZ service or `ptz: off`.

### 5.2 ONVIF session per camera

1. `GetSystemDateAndTime` (unauthenticated) → clock offset (used in WS-Security `Created`
   timestamps — cameras reject drift > 5 s otherwise); drift > 10 s reported as health fault
   `clockDrift`.
2. `GetServices` (fallback `GetCapabilities`) → service URLs (Media, Media2, PTZ, Events),
   rewritten to the configured address when cameras report internal IPs (NAT).
3. `GetDeviceInformation` → attributes.
4. Media2 `GetProfiles` (fallback Media ver10) → choose main/sub by resolution and codec;
   `GetStreamUri` (RTSP over TCP/UDP unicast) and `GetSnapshotUri`, cached; refreshed on
   `video.source.failed` or every 24 h.
5. PTZ: `GetConfigurations`/`GetNodes` → supported spaces (continuous velocity required for
   moves; absolute/relative optional for click-to-centre), `GetPresets`.
6. Events: `CreatePullPointSubscription` (InitialTerminationTime PT10M, renew at 80 %),
   `PullMessages` long-poll (Timeout PT20S, MessageLimit 100); topics mapped:
   `tns1:RuleEngine/CellMotionDetector/Motion` and `tns1:VideoSource/MotionAlarm` → event
   `video.motion` {state}, `tns1:VideoSource/GlobalSceneChange/*` and `tns1:Device/Tamper*` →
   `tamper` capability events, `tns1:Device/Trigger/DigitalInput` → event `video.digitalInput`
   {input, state}; unknown topics counted (diagnostics `topics`). Subscription failures →
   fallback to polling-less mode (no events) with health warning.
7. Health: `healthInterval` GetSystemDateAndTime; 3 failures → device offline; RTSP probe of
   main URL every 10 min (DESCRIBE) → fault `commFail` on stream failure while SOAP works.

Authentication: WS-UsernameToken PasswordDigest (nonce + created + password SHA-1 as required by
ONVIF) on every request; HTTP Digest on 401 (some firmwares); credentials never logged.

### 5.3 Plain RTSP mode

Health: RTSP `OPTIONS` (digest/basic auth) every `healthInterval`; `DESCRIBE` once per hour to
read codecs from SDP (H.264/H.265/MJPEG) → attribute `codec`. No PTZ, no events, snapshot via
gateway frame grab (VID §10 fallback).

### 5.4 Video provider

| Method | Implementation |
|---|---|
| `GetCapabilities` | live yes; playback no; PTZ kinds from §5.2.5; presets; snapshot (URI or gateway); export/bookmarks no; audio if profile has audio |
| `GetLiveSourceAsync` | cached stream URI for the profile with credentials (RTSP over TCP preferred: `rtsp_transport=tcp` hint to go2rtc via `#transport=tcp` source suffix) |
| `GetSnapshotAsync` | HTTP GET snapshot URI with digest/basic auth (≤ 5 MB, JPEG check), or `NotSupported` → VID frame fallback |
| `PtzAsync` | ContinuousMove (velocity space, timeout PT1S re-sent by VID dead-man rule), Stop, GotoPreset, GotoHomePosition, ClickToCentre via RelativeMove (if translation space supported) with FOV-based conversion, AreaZoom via RelativeMove + zoom |
| `GetPresetsAsync` | cached, refreshed on demand |

Commands `ptz.gotoPreset` / `ptz.home` through `ICommandExecutor` (CMD) map to the same calls.

### 5.5 Discovery (`IDiscovery`)

WS-Discovery Probe (`dn:NetworkVideoTransmitter`) on selected interfaces, collect
ProbeMatches for 3 s (two probes), parse XAddrs and scopes (name, hardware, location) → result
list in the wizard with "add selected" (pre-filled settings rows). Cameras on other subnets: add
by address. Discovery only on demand (wizard/diagnostics), never periodic.

## 6. Connection test

For up to 5 cameras: ONVIF time, services, device info, profiles, stream URI, RTSP DESCRIBE of
the main stream (codec) → steps with plain messages ("Camera 'Entrance' answers ONVIF but
rejects the password — check the camera user has the ONVIF 'operator' role").

## 7. Diagnostics

| Action | Arguments | Result |
|---|---|---|
| `discover` | `{ interface? }` | WS-Discovery results |
| `probe` | `{ cameraId }` | Services, profiles (resolution, codec, fps, bitrate), PTZ spaces, event topics |
| `topics` | `{ cameraId }` | Event topics seen and mapping |
| `snapshot` | `{ cameraId }` | Snapshot preview (stored as diagnostics file) |
| `rtsp` | `{ url }` | OPTIONS/DESCRIBE result and SDP |

## 8. Observability

| EventId | Level | Message |
|---|---|---|
| 5500 | Information | Camera {CameraId} ONVIF session ready ({Manufacturer} {Model}, profiles {Profiles}) |
| 5501 | Warning | Camera {CameraId} unreachable: {Error} |
| 5502 | Warning | Camera {CameraId} clock drift {Drift} |
| 5510 | Warning | Camera {CameraId} event subscription failed: {Fault} |
| 5520 | Information | WS-Discovery found {Count} devices on {Interface} |

## 9. Tests

| Id | Layer | Test |
|---|---|---|
| RTSP-U-001 | U | WS-Security UsernameToken digest (spec example vectors), clock offset applied |
| RTSP-U-002 | U | SOAP envelope building and fault parsing (ter:NotAuthorized, ActionNotSupported…) |
| RTSP-U-003 | U | GetServices/GetCapabilities fallback; address rewrite for NAT |
| RTSP-U-004 | U | Profiles selection main/sub by resolution and codec; overrides |
| RTSP-U-005 | U | Media2 then Media ver10 fallback |
| RTSP-U-006 | U | PTZ spaces detection; ContinuousMove/Stop/Presets/Home requests |
| RTSP-U-007 | U | Click-to-centre and area zoom conversion with FOV |
| RTSP-U-008 | U | PullPoint subscription lifecycle: create, renew at 80 %, pull long-poll, recreate on failure |
| RTSP-U-009 | U | Event topic mapping (motion, tamper, digital input) and unknown topics counting |
| RTSP-U-010 | U | Health: time polling, clock drift fault, offline after 3 failures |
| RTSP-U-011 | U | RTSP OPTIONS/DESCRIBE with digest auth; SDP codec parsing (H.264, H.265, MJPEG, audio) |
| RTSP-U-012 | U | Plain RTSP mode inventory and health |
| RTSP-U-013 | U | Video provider capabilities per camera |
| RTSP-U-014 | U | Live source URL with transport hint; credentials not logged |
| RTSP-U-015 | U | Snapshot via URI with auth, size and JPEG checks |
| RTSP-U-016 | U | ptz commands through ICommandExecutor |
| RTSP-U-017 | U | WS-Discovery probe and ProbeMatch parsing (scopes) |
| RTSP-U-018 | U | TestAsync steps and plain-language messages |
| RTSP-U-019 | U | Diagnostics actions |
| RTSP-U-020 | U | Settings validation; translations coverage; manifest validates |
| RTSP-I-001 | I | `OnvifTestDevice` + mediamtx: camera imported, live video through VID in headless Chromium |
| RTSP-I-002 | I | PTZ continuous move and presets against the test device; VID lock rules |
| RTSP-I-003 | I | Motion events through PullPoint → events and alarm rule |
| RTSP-I-004 | I | Camera reboot (test device restart) → session rebuilt, subscription recreated |
| RTSP-I-005 | I | WS-Discovery finds the test device on the container network |
| RTSP-I-006 | I | Manual check list against real cameras (lab, not CI): at least 3 brands — results recorded in `docs/integrations/cameras-compatibility.md` |
| RTSP-C | C | Conformance suite with `OnvifTestDevice` as `IVendorSimulator` |

## 10. Tasks

| Task | Sections | Tests |
|---|---|---|
| T6.07 | all | RTSP-U-001…RTSP-U-020, RTSP-I-001…RTSP-I-006, conformance |
