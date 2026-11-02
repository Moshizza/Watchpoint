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
| Database access | **`Watchpoint.Storage` module with pluggable providers** | — | Customers can use the database their IT already runs; see [Data storage](../modules/data-storage.md) |
| Default database (bundled) | **PostgreSQL 16+** | PostgreSQL (permissive) | Most used database among professional developers, free with no size limits, runs everywhere, mature replication and HA tooling |
| Supported alternative | **SQL Server 2019+** (Express, Standard, Enterprise, Azure SQL) | Microsoft (Express free, others paid by the customer) | The most common database in the security market; many customer IT teams require it |
| On demand | MySQL / MariaDB, Oracle; ClickHouse for very large history | Various | Added when a project requires it, through the same provider contract |
| History (time series), default | **TimescaleDB** (PostgreSQL extension) | Apache-2 core + Timescale License for compression/continuous aggregates (free to use; forbids offering it as a hosted database service, which we do not) | Time partitioning, compression, retention policies, fast aggregates, same database engine |
| Event log / internal bus | **NATS with JetStream** | Apache-2 | Lightweight single binary, clustering, durable streams with replay, very low latency; fits single-machine and multi-node |
| ORM / data access | EF Core 10 + provider drivers (Npgsql, Microsoft.Data.SqlClient, Pomelo/MySqlConnector, ODP.NET) | MIT / PostgreSQL / Oracle free licence | Standard .NET data stack, versioned migrations per provider |
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
| GIS maps | **MapLibre GL JS** + **PMTiles** generated from OpenStreetMap data (Planetiler/Protomaps tools), served by the node | BSD-3 / Apache-2 / ODbL data (attribution required) | Free vector maps that work offline; no dependency on public tile servers, whose usage policy forbids product use |
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
| Installers | **Inno Setup** (Windows service setup, silent mode), nfpm (.deb/.rpm with systemd units), OCI images (Docker/Podman/Kubernetes), electron-builder with **NSIS** and MSIX (clients) | Inno Setup licence (free, commercial use allowed) / MIT / zlib | Free, widely used, scriptable for mass deployment; an MSI can be added later if enterprise customers require one |
| Tests | xUnit, Testcontainers (.NET); Vitest, Playwright (front-end); **k6** (load tests) | Apache-2 / MIT / AGPL-3 (k6 is only used internally, not shipped) | Popular, run in CI |
| Static analysis | .NET analyzers, **Semgrep Community Edition**, ESLint, TypeScript strict mode | MIT / LGPL-2.1 | Free security scanning for private repositories |

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

### 2.2 Why PostgreSQL + TimescaleDB by default, with SQL Server and others as options

- Free on every platform; SQL Server Express has a 10 GB limit per database and SQL Server
  Standard is costly per core, which hurts small sites and price-sensitive markets.
- TimescaleDB gives partitioning, compression (often 90%+ on event data) and retention
  policies for history out of the box.
- PostgreSQL is now the most used database among professional developers, so popularity
  and hiring are not a concern.
- SQL Server remains the most common database **in the security market**, and many
  customer IT teams require it. All database access therefore goes through the
  [storage module](../modules/data-storage.md): SQL Server is a fully supported provider
  (phase 2), MySQL/MariaDB and Oracle can be added on demand, and each store
  (configuration, state, history, audit) can be placed on a different database — e.g.
  configuration on the customer's SQL Server and history on the bundled PostgreSQL to avoid
  SQL Server Express size limits.

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

## 3. Costs and licence conditions

Everything shipped in the product is free and allows a closed commercial product. The
remaining costs are services and memberships, not software licences.

### 3.1 Unavoidable costs (company side)

| Item | Why | Approximate cost |
|---|---|---|
| Windows code-signing certificate | Sign service, installers, clients | ~USD 200–700 / year |
| Apple Developer Program | Sign and notarise the macOS client, publish the iOS app, send iOS push notifications | USD 99 / year |
| Google Play developer account | Publish the Android app | USD 25 once |
| Key protection for signing keys | Licence and package signing keys (cloud key vault, or hardware HSM later) | A few USD / month (cloud KMS) |
| Hosting | Licensing portal, push relay, update repository | Infrastructure costs |
| Vendor partner programmes | Salto (OEM licence for its HTTPS interface), Milestone (partner programme, full XProtect editions for API tests), Vanderbilt | Per vendor agreements |
| SMS sending | Per-message provider fees | Usually paid by the customer |

### 3.2 Free with conditions to respect

| Item | Condition | How Watchpoint handles it |
|---|---|---|
| TimescaleDB compression and continuous aggregates | Free under the Timescale License, except offering it as a hosted database service (we don't). **Not available in most managed cloud databases** (AWS RDS, Azure Database for PostgreSQL) | Customer-hosted cloud installs run PostgreSQL + TimescaleDB on their own VM. History also works without these features (plain partitioned tables), with more storage and slower reports |
| OpenStreetMap data | Free (ODbL) with attribution; public tile servers forbid product use | Tiles generated from OSM data and served by the node; attribution shown on maps |
| FFmpeg (only if bundled for transcoding) | Must be an **LGPL** build to stay compatible with a closed product | Bundle an LGPL build only; prefer passthrough without transcoding |
| k6 (AGPL-3) | AGPL obligations apply only if distributed or offered as a service | Used internally for load tests, never shipped |
| OPC UA | The OPC Foundation .NET stack is GPL for non-members | Use open62541 (MPL-2.0) instead; MPL only requires publishing changes made to open62541's own files |
| MIT / BSD / Apache / MPL / OFL libraries | Keep copyright and licence notices | A generated third-party notices file ships with every release (from the SBOM) |
| Developer tools | Docker Desktop and Visual Studio need paid licences above company size/revenue thresholds | Team uses VS Code, Docker Engine or Podman (free); paid IDEs only by choice |

Legal review of every licence remains a release requirement.

## 4. Processes on a node

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

## 5. Supported platforms (target)

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
