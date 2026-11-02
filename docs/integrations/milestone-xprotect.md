# Worked Example — Milestone XProtect (Video)

## 1. Context

Milestone XProtect is a VMS: a management server, one or more recording servers, cameras,
microphones, speakers, inputs/outputs, events and alarms, recordings, bookmarks and exports.
Watchpoint does not record: it supervises the VMS, shows live and recorded video, controls
PTZ, receives VMS events (including analytics), and requests bookmarks and exports.

Interfaces to evaluate (with Milestone's official documentation and partner programme):

| Interface | Use | Notes |
|---|---|---|
| **MIP SDK** (.NET) | Configuration browsing, events, PTZ, playback, exports, bookmarks | Historically Windows-only components → run **isolated**, on a Windows node or a **remote Windows integration host** |
| **REST APIs and API Gateway** (recent XProtect versions) | Configuration, events over WebSocket, possibly WebRTC video | If sufficient, allows a cross-platform connector; evaluate versions supported by customers |
| **RTSP** (e.g. Milestone's ONVIF/RTSP bridge component) | Live and playback streams for the media gateway | Avoids decoding frames in the connector |

Decision for v1: implement against the documented REST/API Gateway features where they
cover the need, fall back to the MIP SDK for the rest in an isolated host. Final choice
after a technical spike.

## 2. Package

| Item | Value |
|---|---|
| Package | `watchpoint.integration.milestone` |
| Connectors | `XProtectConnector` (server type `milestone.xprotect.management`) |
| Platforms | win-x64 (MIP SDK parts); REST parts cross-platform |
| Isolation | `isolated-required` when MIP SDK components are used |
| Licence | `int.milestone`, advanced class |

## 3. Mapping to the model

| XProtect object | Watchpoint | Device type | Licence class |
|---|---|---|---|
| Management server | Server | `milestone.xprotect.management` | free |
| Recording server | Child server | `milestone.xprotect.recording` | free |
| Camera | Device | `video.camera` (PTZ capabilities when supported) | full |
| Microphone / speaker | Child device of camera or device | `video.audio-source` / `comm.speaker` | free |
| Input / output | Device | `generic.binary-input` / `generic.binary-output` | light |
| Analytics events / rules | Events on cameras; optionally `video.analytics-rule` devices | — | free |
| User-defined events | Events on the server | — | — |

Camera attributes: model, resolution, streams (profiles) for sub/main selection, geometry
for synoptic cones (from Watchpoint configuration — XProtect map data if available).

## 4. Video flow

1. Client requests live/playback of camera C → video module checks rights.
2. Connector returns a **stream source**: an RTSP URL (with short-lived credentials) via the
   RTSP bridge or API Gateway, or, if only the SDK can provide media, a packet feed from the
   isolated host into the media gateway.
3. go2rtc serves WebRTC to the client; playback position/speed commands map to the source's
   playback controls.

## 5. Events and states

| XProtect concept | Watchpoint |
|---|---|
| Camera connection lost / restored, recording server offline | `health` connectivity and faults |
| Recording state | `recording.recording` |
| Motion | `analytics.detection(kind = motion)` (often not alarmed by default) |
| Analytics events (line crossing, object left…) | `analytics.detection` with kind and metadata |
| VMS alarms | Events with type `milestone.alarm`; Watchpoint alarm rules decide whether to raise (avoid double alarm consoles) |
| Input activated | `binaryValue` change |

## 6. Commands

| Watchpoint command | XProtect action |
|---|---|
| `ptz.move/stop/absoluteMove/zoom` | PTZ control |
| `presets.gotoPreset` | Go to preset |
| `patrol.startPatrol` | Start patrolling profile |
| `recording.startRecording/stopRecording` | Manual recording (if allowed by VMS rules) |
| `recording.bookmark(text)` | Create bookmark |
| `recording.export(range, format)` | Create export job; result contains job id; progress via events |
| `snapshot.takeSnapshot` | Get JPEG at time T (live or recorded) |
| Output `switchOn/Off/pulse` | Output control |

## 7. Default alarm rules shipped

| Rule | Priority band |
|---|---|
| Camera offline > 1 min | Medium |
| Recording server offline | High |
| Camera recording stopped unexpectedly | Medium |
| Analytics intrusion detection (when enabled per camera) | High |

## 8. Questions to confirm

- REST/API Gateway coverage per XProtect version and edition; WebRTC availability.
- Licensing on the Milestone side for third-party integrations (MIP licence requirements).
- Authentication (Windows vs basic users), token lifetimes.
- Event subscription model and delivery guarantees for catch-up.
- Export formats and API.

## 9. Test plan

- XProtect lab (free XProtect Essential+ for basic tests, a full edition for API coverage),
  4 cameras including 1 PTZ, 1 analytics source.
- Scenarios: live/playback in desktop and web clients; 16-tile layout; PTZ priority between
  two operators; recording server stop/start; management server restart; export job;
  isolated-host crash and recovery.
