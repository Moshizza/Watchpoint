# Technology Stack

> Selection criteria, in order: (1) fits the requirement, (2) modern and widely used, so
> hiring, documentation and long-term support are easy, (3) cross-platform, (4) free and
> open source with a licence compatible with selling a closed product, (5) small
> operational burden for integrators installing on-site.

All licences below must be re-checked by legal before the first commercial release.

## 1. Summary

| Layer | Choice | Licence | Why |
|---|---|---|---|
| Service runtime | **.NET 10 (C#)**, ASP.NET Core | MIT | Plugin loading of DLLs with isolation (`AssemblyLoadContext`), most security vendors ship .NET SDKs, native Windows service / systemd / container hosting, high performance, long-term support |
| Configuration and live-state database | **PostgreSQL 17+** | PostgreSQL (permissive) | Most popular open-source database, JSON support, runs everywhere, mature replication and HA tooling |
| History (time series) | **TimescaleDB** (PostgreSQL extension) | Apache-2 core + Timescale License for compression/continuous aggregates (free to use; forbids offering it as a hosted database service, which we do not) | Time partitioning, compression, retention policies, fast aggregates, same database engine |
| Event log / internal bus | **NATS with JetStream** | Apache-2 | Lightweight single binary, clustering, durable streams with replay, very low latency; fits single-machine and multi-node |
| ORM / data access | EF Core 10 + Npgsql | MIT / PostgreSQL | Standard .NET data stack, versioned migrations |
| Client live protocol | **ASP.NET Core SignalR** (WebSocket, MessagePack) | MIT | Official clients for TypeScript, .NET, React Native-compatible JS, automatic transport fallback |
| Public API | REST + OpenAPI 3.1 | — | Universal; TypeScript clients generated from the contract |
| Node ↔ integration host / script host | **gRPC** over named pipes, Unix sockets or mTLS TCP | Apache-2 | Typed contracts, streaming, cross-language |
| Front-end (shared by desktop and web) | **React 19 + TypeScript + Vite** | MIT | Most popular UI stack; huge ecosystem |
| UI components | **shadcn/ui** (Radix primitives + Tailwind CSS) | MIT | Popular, accessible, owned source, right-to-left support through Radix `Direction` and Tailwind logical utilities |
| Data fetching / state | TanStack Query, Zustand | MIT | Standard, small |
| Large tables and lists | TanStack Table + TanStack Virtual | MIT | Virtualised lists of 100k rows for alarm/event consoles |
| Charts | Apache ECharts | Apache-2 | Fast canvas charts, time-series, RTL-aware |
| Desktop shell | **Electron** | MIT | Bundles Chromium: identical rendering, WebRTC and codecs on Windows, Linux and macOS; multi-window and multi-monitor; auto-update |
| Mobile app | **React Native + Expo** | MIT | Shares TypeScript code with the web app; iOS and Android from one codebase |
| Synoptic canvas | **Konva** (react-konva) | MIT | High-performance 2D canvas with layers, transforms, hit detection; good for thousands of live symbols |
| Plans import | pdf.js (PDF), dxf-parser (DXF), native SVG/PNG/JPEG | Apache-2 / MIT | Free, browser-side conversion of plans to backgrounds |
| GIS maps | **MapLibre GL JS** + OpenStreetMap or offline PMTiles | BSD-3 / ODbL data | Free vector maps, works offline with a local tile file |
| Automation editor | **React Flow** (xyflow) | MIT | Standard node-graph editor |
| Code editor | **Monaco Editor** | MIT | The editor of VS Code: TypeScript diagnostics, completion, hover, refactoring in the browser |
| Script runtime | **TypeScript** executed by **V8 via Microsoft ClearScript** in a sandbox process | MIT (ClearScript), BSD (V8) | Modern, popular language; V8 isolates with memory and time limits; runs on Windows/Linux/macOS x64 and arm64 |
| Expression language (conditions, synoptic bindings) | **CEL** (Common Expression Language) syntax, one TypeScript implementation used both in clients and in the server sandbox | Apache-2 (spec) | Safe by design (no loops, no side effects, bounded cost), familiar C-like syntax, used by Kubernetes and Google Cloud |
| Video gateway | **go2rtc** (primary candidate), MediaMTX (alternative) | MIT | RTSP/RTMP/HTTP sources in, WebRTC/MSE/HLS out, passthrough without transcoding, small single binary |
| Logging | Serilog (structured, with redaction) | Apache-2 | Standard .NET structured logging |
| Metrics and traces | OpenTelemetry, Prometheus endpoint | Apache-2 | Industry standard; plugs into any monitoring |
| Reports (PDF) | HTML templates rendered by headless Chromium (PuppeteerSharp) | MIT | Free, pixel-perfect, same templates for screen and PDF |
| Spreadsheet export | ClosedXML | MIT | Free XLSX generation |
| E-mail | MailKit | MIT | Standard SMTP/IMAP library |
| Translation | i18next + ICU message format (client), .NET resources + ICU (server), Weblate for translators | MIT / GPL (Weblate is a separate tool, not shipped) | Plurals, genders, RTL, numbers and dates per locale |
| Installers | WiX Toolset (Windows MSI), .deb/.rpm with systemd units (Linux), OCI images (Docker/Podman/Kubernetes), electron-builder (clients) | MS-RL / MIT | Free, standard for each platform |
| Tests | xUnit, Testcontainers (.NET); Vitest, Playwright (front-end) | Apache-2 / MIT | Popular, run in CI |

## 2. Key decisions explained

### 2.1 Why .NET for the service

- **DLL-based integrations** were a requirement. .NET can load assemblies at runtime, give
  each package its own dependency set (`AssemblyLoadContext`), and unload or restart them.
- Most vendor SDKs used in this market are .NET (Milestone MIP SDK, many access control and
  intercom SDKs) or C/C++ libraries easily wrapped from .NET. Java, Go or Node would need
  bridges for many of them.
- One binary runs as a **Windows service**, a **systemd** service or a **container** with
  the official hosting extensions, which matches "no server executable, just a service".
- Performance: ASP.NET Core and Kestrel are among the fastest web stacks; the core needs to
  handle thousands of state changes per second.

Alternatives considered: **Go** (great for services, poor for loading third-party DLLs and
vendor SDKs), **Java/Kotlin** (good plugin story but few vendor SDKs, heavier runtime),
**Node.js** (weak isolation of native SDKs, single-threaded CPU-bound processing),
**Rust** (excellent but plugin ABI and SDK wrapping are costly, smaller hiring pool).

### 2.2 Why PostgreSQL + TimescaleDB instead of SQL Server

- Free on every platform; SQL Server Express has a 10 GB limit per database and SQL Server
  Standard is costly per core, which hurts small sites and price-sensitive markets.
- TimescaleDB gives partitioning, compression (often 90%+ on event data) and retention
  policies for history out of the box.
- If a customer's IT mandates SQL Server, the data-access layer is isolated (EF Core +
  repository interfaces) so a SQL Server provider can be added later, without
  TimescaleDB-specific features (history would use partitioned tables instead).

### 2.3 Why NATS JetStream as event log

The core needs a durable, ordered log of everything that happens so that:

- a crash never loses events or state (state is rebuilt from a snapshot + replay);
- several nodes can share work and take over each other's tasks;
- federation links can resume after a WAN outage from a sequence number.

NATS is a ~20 MB single binary, bundled and supervised by the Watchpoint service, so the
integrator does not install or operate it separately. Kafka was rejected as too heavy for
small sites; Redis Streams lacks the clustering and retention model needed; using only
PostgreSQL (outbox + LISTEN/NOTIFY) was kept as a fallback design for single-node "lite"
installations if NATS ever proves a burden.

### 2.4 Why Electron for the desktop client (and not Tauri or a native UI toolkit)

The desktop client must show **live video** reliably on **Windows, Linux and macOS** and
share its code with the web client.

| Option | Verdict |
|---|---|
| **Electron** | ✅ Chosen. Ships its own Chromium, so WebRTC, hardware video decoding, codecs and rendering are identical on all three systems. Used by VS Code, Teams, Slack, Discord. Heavier download (~100 MB) is acceptable for an operator workstation. |
| Tauri | ❌ Uses the system web view: WebView2 on Windows is fine, but WebKitGTK on Linux has incomplete/optional WebRTC support and macOS WKWebView differs in codecs. Video is too central to accept that variance. (Earlier discussions mentioned Tauri; this analysis replaces that suggestion.) |
| Avalonia / WPF / Qt | ❌ Would need a second UI codebase separate from the web client, and native video players per platform. |

Electron also gives: multiple windows across monitors, kiosk mode, auto-start, OS
notifications, USB joysticks (Gamepad API) for PTZ, and auto-update.

### 2.5 Why TypeScript for scripting

- The most used language for application scripting today; huge community; easy to learn
  for integrators who know a little JavaScript.
- **Monaco** gives a VS Code-grade experience in the client with no server round trip:
  red underlines, completion of the user's own devices, hover documentation, quick fixes.
- **V8 isolates** (through ClearScript) provide memory limits, CPU time limits and no
  access to anything not explicitly exposed — a real sandbox, which C# scripts cannot
  offer in .NET (Code Access Security no longer exists).
- C# scripting via Roslyn was considered: more powerful, but cannot be sandboxed inside the
  process and gives non-developers a harsher experience.

### 2.6 Why CEL for expressions

Synoptic bindings ("colour red when door forced"), alarm conditions and automation
conditions need a tiny expression language that can never hang, never have side effects and
never "crash". CEL (Common Expression Language, by Google) is designed exactly for that, is
documented and has a familiar syntax (`door.position == "open" && door.lock == "unlocked"`).
One TypeScript evaluator is used by clients and by the server sandbox, so an expression
behaves identically everywhere. Library choice (existing open-source CEL implementation vs.
our own small interpreter of the CEL subset we need) will be decided at implementation.

### 2.7 Why a media gateway

Browsers cannot play RTSP. A gateway (go2rtc) converts camera and VMS streams to WebRTC
without transcoding when the codec allows (H.264, and H.265 where the client supports it),
with sub-second latency. It is bundled and supervised by the node like NATS. It removes the
need for vendor video players on operator PCs.

## 3. Processes on a node

From the operating system's point of view a node is **one service**. That service supervises
helper processes it ships with:

| Process | Started by | Purpose |
|---|---|---|
| `watchpoint` (the service) | OS (Windows SCM, systemd, container runtime) | Core, API, integration runtime (in-process connections), supervision of the others |
| `nats-server` | watchpoint | Event log and internal bus |
| `go2rtc` | watchpoint | Media gateway |
| `watchpoint-integration-host` | watchpoint (on demand) | Isolated connections (native SDKs, untrusted, other architecture) |
| `watchpoint-script-host` | watchpoint | V8 sandbox for scripts |
| PostgreSQL | OS (own service) — installed by the Watchpoint installer or provided by the customer | Database |

Helper processes are restarted automatically, health-checked, and never exposed directly
to the network except where configured (NATS cluster ports between nodes, media gateway
WebRTC ports).

## 4. Supported platforms (target)

| Component | Windows | Linux | macOS | Container |
|---|---|---|---|---|
| Watchpoint service | Windows Server 2019+, Windows 10/11 (small sites) | Ubuntu 22.04+, Debian 12+, RHEL/Rocky/Alma 9+ | — (development only) | Docker, Podman, Kubernetes |
| Desktop client | Windows 10/11 | Ubuntu 22.04+, Debian 12+, Fedora | macOS 13+ | — |
| Web client | Current Chrome, Edge, Firefox, Safari | same | same | — |
| Mobile app | — | — | — | iOS 16+, Android 10+ |
| Integration host | Windows, Linux (per package requirements) | | | yes |

Some vendor SDKs are Windows-only. Packages declare their supported platforms; a
Linux-based node can run a Windows-only integration through a **remote integration host**
on a Windows machine (see [Integrations](../modules/integrations.md)).
