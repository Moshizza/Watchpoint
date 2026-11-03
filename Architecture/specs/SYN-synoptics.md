# SYN — Synoptics

| | |
|---|---|
| Spec id | SYN |
| Projects | `src/service/Watchpoint.Synoptics` (service), `src/ui/packages/synoptics` (`@watchpoint/synoptics`: model, renderer, viewer, editor), `schemas/synoptic.schema.json` |
| Milestone | M6 |
| Tasks | T6.01 (§4–§6), T6.02 (§7–§8), T6.03 (§9–§11), T6.04 (§12) |
| Event id range | 3360–3399 (shared block with Video 3300–3399; VID uses 3300–3359) |
| Design docs | [Synoptics](../../docs/modules/synoptics.md) |

## 1. Purpose and scope

No-code interactive screens: **plans**, **maps**, **diagrams** and **dashboards**, stored as
validated JSON documents and rendered by one tested engine (React + Konva; MapLibre for maps)
in the desktop and web clients.

- Service: document type, storage through CFG, file references (FIL), binding validation,
  schema migrations, background processing (tiling), symbol library, PMTiles serving.
- Viewer: viewport-driven live subscriptions, level of detail, alarm focus, navigation.
- Editor (desktop configuration side): canvas, layers, background import, drag-and-drop
  devices, smart symbols, properties forms, validation, simulation mode.

Rule from the design: a document can never crash a client, run code, or show compiler errors.

## 2. Dependencies

| Uses | Used by |
|---|---|
| Service: K, CFG (object type `synoptics.document`, publish/rollback), FIL (backgrounds, images, symbols), REG (devices, locations, geometry), MOD (types, default symbols), RGT (`config.synoptics.*`, visibility), EXP (expression check), SkiaSharp (MIT, tiling), netDxf (MIT, DXF → SVG) | UIO (synoptic panels, alarm "show on plan"), WEB, VID (camera cones open video), DSK (multi-monitor) |
| UI: `@watchpoint/api-client`, `live`, `model`, `i18n`, `ui`, `cel` (TS evaluator), Konva 9 + react-konva (MIT), MapLibre GL JS 4 (BSD-3), pmtiles (BSD-3), rbush (MIT), pdf.js (Apache-2.0, editor import), zundo/immer (MIT, undo) | |

## 3. Layout

```
Watchpoint.Synoptics/
├── Contracts/SynopticDocument.cs (mirror of schema for server validation), ISynopticService.cs, SynopticErrors.cs
├── Internal/
│   ├── SynopticObjectType.cs (CFG IConfigurableObjectType), BindingValidator.cs, DocumentMigrator.cs (v1→vN), ReferenceExtractor.cs
│   ├── Backgrounds/TileBuilder.cs (SkiaSharp), DxfConverter.cs (netDxf → SVG), SvgRasteriser.cs
│   ├── Symbols/SymbolLibrary.cs (standard + package + site symbols)
│   ├── Maps/PmTilesEndpoint.cs (range requests), MapStyles.cs
│   └── Storage/ (documents stored by CFG in wp_synoptics.documents)
├── SynopticsModule.cs, SynopticsLog.cs
packages/synoptics/src/
├── model/        types generated from schema, migrations (TS, same as server), selectors
├── render/       Stage, BackgroundLayer (tiles), ZoneLayer, SymbolLayer, AnnotationLayer, element renderers, LodController, AppearanceEngine, ErrorBoundaryPerElement
├── viewer/       SynopticViewer, ViewportSubscriptions, AlarmFocus, NavigationHistory, PlanSearch, DevicePopover
├── editor/       SynopticEditor, tools (select, pan, draw shapes/zones, text), panels (layers, devices, properties, symbols, validation), import wizard, smart symbols, simulation, undo store
└── map/          MapSynoptic (MapLibre), MapLayers (device markers, zones as GeoJSON), offline style
```

## 4. Document model (T6.01)

`schemas/synoptic.schema.json` (JSON Schema 2020-12), `schemaVersion` integer (v1 at M6).

```jsonc
{
  "schemaVersion": 1,
  "id": "0192…", "kind": "plan",                       // plan | map | diagram | dashboard
  "name": { "en": "Building A – Level 1", "fr": "…", "ar": "…" },
  "location": "loc-…",                                 // optional: the location this screen represents (navigation, "show on plan")
  "canvas": { "width": 8000, "height": 5000, "unit": "px", "background": { "file": "file-…", "kind": "tiles" | "svg" | "image", "opacity": 1 }, "scaleMetersPerUnit": 0.01 },
  "map": null,                                         // kind=map: { "center": [lon, lat], "zoom": 16, "style": "offline-default", "bounds": … }
  "grid": null,                                        // kind=dashboard: { "columns": 12, "rowHeight": 80 }
  "layers": [ { "id": "zones", "name": {…}, "visible": true, "locked": false, "minZoom": 0, "maxZoom": null } ],
  "elements": [ /* §4.1 */ ],
  "smartSymbols": [ /* local definitions (§10) */ ],
  "settings": { "defaultZoom": "fit", "lod": { "symbolsFrom": 0.25, "labelsFrom": 0.6 } }
}
```

### 4.1 Elements

Common fields: `id` (string, unique), `type`, `layer`, `x`, `y`, `rotation`, `scale`, `w`, `h`
(where relevant), `visibleWhen` (appearance rule, §4.2), `label`, `locked`, `minZoom/maxZoom`.

| `type` | Specific fields |
|---|---|
| `device` | `device` (DeviceId), `symbol` (symbol id or null = type default), `size`, `showLabel`, `badges` (`alarms`, `maintenance`, `quality`), `appearance` overrides, `onClick` (`panel` default / `page` / `video`) |
| `zone` | `points` (polygon), `location` (LocationId) or `devices` (list) for the aggregated state; fill opacity; `onClick` (`navigate` to the location's synoptic, `panel`) |
| `cameraCone` | `device` (camera); geometry from REG (azimuth, fov, range) unless overridden; click → live video |
| `link` | `target`: `{ synoptic }`, `{ location }`, `{ workspace }`, `{ url }` (allow-listed); shape/icon/text |
| `commandButton` | `device` or `group` (device selector), `command`, `arguments` (fixed), `confirm` text override; rights and confirmations follow CMD |
| `automationButton` | `automation` (M8), `parameters` |
| `value` | `device`, `property`, `format` (decimals, unit display), `prefix/suffix` |
| `gauge` / `bar` / `sparkline` | `device`, `property`, `min`, `max`, thresholds; sparkline window (≤ 24 h, HIS series) |
| `widget` | `widget`: `alarmList` (filter), `eventList` (filter), `videoTile` (camera), `stateCounters` (scope), `chart` (series), `clock`, `text` (Markdown), `image`, `webPage` (allow-listed URL, sandboxed iframe) |
| `shape` | `shape`: `line`, `polyline`, `rect`, `ellipse`, `polygon`, `arrow`; stroke, fill |
| `text` | localized text, font size, weight, alignment (logical start/end) |
| `image` | `file`, fit |
| `icon` | Lucide icon name, colour |
| `group` | `children` (element ids) — transforms apply to children |
| `smartSymbolInstance` | `definition` (id), `parameters` (values: devices, texts) |

### 4.2 Appearance rules

```jsonc
"appearance": [
  { "when": { "state": ["alarm"] }, "fill": "state", "blink": true },
  { "when": { "property": "doorPosition.position", "in": ["open"] }, "icon": "door-open" },
  { "when": { "expression": "device.prop('measurement.value') > 30" }, "badge": "hot" }
]
```

- Conditions: `state` (overall states), `property` + `in`/`equals`/`gt`/`lt`, or `expression`
  (CEL subset via EXP, checked at save: syntax, types, cost ≤ 1,000 steps). First matching rule
  per visual channel (fill, stroke, icon, blink, badge, visibility) wins; type defaults last.
- Expressions evaluated in the client with the TS evaluator over live values; errors at runtime
  → rule ignored and element flagged in diagnostics (never thrown).

## 5. Service (T6.01)

- CFG object type `synoptics.document` (soft-lockable, not tabular; permissions
  `config.synoptics.view/edit/publish`, scope Locations: the document's `location`, or None).
- `ValidateAsync`: schema validation, then **binding validation** (`BindingValidator`):
  devices/locations/files/synoptics/automations exist (deleted → error for new references;
  existing references to devices deleted later are **warnings**, rendered as unavailable),
  commands exist on the device type and arguments validate, expressions check, URLs in
  allow-list (`Synoptics:AllowedUrls`), sizes (elements ≤ 20,000 per document, document ≤ 10 MB),
  ids unique, groups acyclic, element layers exist. Warnings: device outside the document's
  location subtree, overlapping device symbols (> 80 % overlap), labels missing translations,
  symbols smaller than 16 px at default zoom.
- `PrepareApplyAsync`: store document JSON, `FileReferences.ReplaceForEntityAsync` (backgrounds,
  images, symbols) in the same transaction (FIL §7).
- Reads: `GET /api/v1/synoptics?location=&kind=&text=` (list with thumbnails — thumbnail file
  generated at publish by rasterising the background + zones at 320 px), `GET /api/v1/synoptics/{id}`
  (document, **migrated** to the current schema version), `GET /api/v1/synoptics/for-location/{id}`
  (best synoptic: document with that location, else nearest ancestor's), `GET
  /api/v1/synoptics/for-device/{id}` (documents containing the device, for "show on plan").
- Visibility: a user can open a synoptic with `operate.devices.view` anywhere; elements bound to
  devices outside the user's scope are **hidden** (not "unavailable") — the client receives the
  full document but the live subscription returns nothing for them; the server additionally
  strips out-of-scope device elements from the document for non-configurators (no leaking of
  device names via labels).

## 6. Migrations and backgrounds (T6.01)

- `DocumentMigrator` (C#) and `migrations.ts` (TS) implement the same steps vN → vN+1, tested
  with shared fixtures `tests/vectors/synoptics/v*/` (input → expected output). Documents are
  migrated on read; publishing writes the current version.
- Background processing endpoint `POST /api/v1/synoptics/backgrounds` (job):
  - **image** (PNG/JPEG ≤ 30,000 px): tiles 512 px at zoom levels down to fit (SkiaSharp), stored
    as one FIL file `synoptic.plan` (zip-like tile archive `*.wptiles`: index + tiles) served via
    `GET /api/v1/synoptics/tiles/{fileId}/{z}/{x}/{y}.png` (cache headers immutable).
  - **SVG**: sanitised by FIL; kept as vector when ≤ 5 MB and ≤ 50,000 nodes, else rasterised and
    tiled.
  - **DXF**: netDxf → SVG with selected layers (layers list returned by `…/backgrounds/inspect`),
    units → `scaleMetersPerUnit`, then as SVG.
  - **PDF**: converted **in the editor** with pdf.js (page, DPI chosen by the user) to PNG,
    uploaded as image (no server-side PDF engine).

## 7. Viewer (T6.02)

### 7.1 Rendering

- Konva `Stage` with layers: background (tiles loaded for the viewport and zoom; `listening:
  false`, cached), zones, symbols, annotations, overlay (selection, focus pulse).
- Each element renderer is wrapped by `ElementBoundary`: an exception renders a grey
  placeholder with a warning icon and logs once (diagnostics), never breaking the stage.
- **Level of detail**: below `lod.symbolsFrom` zoom only zones, links and aggregated counters
  (per zone: alarm/fault counts) are drawn; symbols appear above it; labels above
  `labelsFrom`. Layer `minZoom/maxZoom` respected.
- State updates coalesced per animation frame (`requestAnimationFrame`): only changed
  symbols re-drawn (Konva node attribute updates + `batchDraw`).
- Blink via a single shared animation (opacity 1 ↔ 0.3 at 1 Hz, disabled with
  `prefers-reduced-motion`: replaced by a thick outline).
- Unavailable devices (deleted, out of scope): out of scope → hidden; deleted → grey dashed
  symbol + tooltip "Device no longer exists".

### 7.2 Viewport subscriptions

- `rbush` spatial index of element bounds; visible set = elements intersecting the viewport ⊕
  10 % margin, filtered by LOD.
- Devices of the visible set → live `devices` topic subscription `{ ids }` (≤ 2,000; above,
  LOD forces zone-only mode with a hint "zoom in to see devices"); zones → `aggregates` topic
  (≤ 500 scopes). Subscription updated on pan/zoom, debounced 200 ms, diffed (subscribe new,
  unsubscribe gone) using the live client's multiple-subscription API.
- Sparklines/charts fetch HIS series when visible, refreshed every 60 s.

### 7.3 Interaction

| Action | Behaviour |
|---|---|
| Click device | Device popover (UIO `DeviceSummaryCard` with commands); `onClick=page` opens device tab; `video` opens VID tile |
| Double-click device | Device page |
| Right-click / long-press | Context menu: acknowledge alarms of device, show video, open in new window (desktop), copy link |
| Click zone / link | Navigate (push history) |
| Command button | CMD flow (confirmations, reason, four-eyes) — same component as UIO |
| Wheel / pinch | Zoom around pointer; keyboard `+`/`-`/`0` (fit), arrows pan |
| Tab | Focus cycles through interactive elements in reading order (logical, RTL-aware); Enter activates |
| Search (Ctrl+F) | Device/label search within the document → centre and pulse |

## 8. Alarm focus and navigation (T6.02)

- "Show on plan" (from alarm console, device page): `for-device` → choose the document whose
  `location` is deepest containing the device (or the only one) → open, centre on the element,
  zoom to at least `labelsFrom`, pulse for 5 s.
- Alarm console option "auto-focus plan on new Critical alarms" per workspace panel.
- Navigation history (back/forward per panel), breadcrumbs from the document's location path,
  "go to parent location's synoptic".
- Deep links: `/synoptics/{id}?focus=<deviceId>&zoom=`.

## 9. Editor (T6.03)

### 9.1 Canvas and tools

- Desktop configuration side, route `/config/synoptics/{id}`; edits go to the active CFG draft
  (soft lock while open, UIF §5.8).
- Tools: select (marquee, shift-add), pan, rectangle/ellipse/polygon/polyline/line/arrow, zone
  polygon (snap to background lines optional), text, image, link, widget placement.
- Rulers, grid (size per document), snapping to grid and to other elements' edges/centres with
  alignment guides; align/distribute commands; z-order; group/ungroup; lock; copy/paste across
  documents (bindings kept), duplicate with offset.
- Undo/redo (zundo, unlimited in session), autosave of the draft entry every 10 s when changed.

### 9.2 Panels

- **Layers**: add, rename, reorder, visibility, lock, LOD range.
- **Devices**: location tree and server tree (UIO components) with "placed" ticks; filter
  "unplaced devices of this location"; drag onto canvas → `device` element with the type's
  default symbol already bound; multi-drag places in a row; "auto-place" row/grid; CSV import
  of positions (`nativeId|deviceId,x,y`); for maps, "place from geo-coordinates".
- **Properties**: forms for the selection (multi-selection edits common fields); appearance
  rules editor with condition builder (state list, property picker, operators) and optional CEL
  editor (UIF CEL editor) with live result for the bound device.
- **Symbols**: standard library (per type and state), package symbols, site symbols (upload
  SVG → FIL `synoptic.symbol`, sanitised).
- **Validation**: problems from the draft (server) and immediate client checks, click to select
  element.

### 9.3 Import wizard

Background import: drop file → kind detection → PDF: page picker + DPI (pdf.js preview) → DXF:
layer picker → scale (two points + known distance, or `scaleMetersPerUnit`) → processing job
progress → background set.

## 10. Smart symbols and templates (T6.03)

- Smart symbol definition: a group of elements with **parameters** (`device` slots with type
  constraints, `text` slots), created from a selection ("make smart symbol" → choose which
  bindings become parameters); stored in the document (`smartSymbols`) or in the site library
  (CFG object type `synoptics.smartSymbol`, shared).
- Instance placement: drop a device on a smart symbol in the library → slots auto-filled from the
  device and its links (e.g. door → reader child, linked camera) where types match; remaining
  slots asked in a small form.
- Templates: new document from template (blank plan, dashboard 12-column, overview map, building
  overview with zones per child location — generated from REG children).

## 11. Simulation mode (T6.03)

Editor preview with a state override panel: set overall state per device/zone (or "all
alarm", "all offline", "fire scenario" = all fire category devices in alarm), property values for
value elements; renderer uses an injected `StateSource` (live or simulated). Nothing is sent to
the server.

## 12. Map synoptics (T6.04)

- `kind: map` renders with MapLibre GL JS; element positions in WGS84 (`lon`, `lat` instead of
  x/y); device markers as a symbol layer generated from device elements (icons from the same
  symbol set, rasterised SVG sprites at 1×/2×), zones as GeoJSON polygons with state fill,
  camera cones as GeoJSON sectors from geometry, clustering at low zoom (MapLibre clustering with
  aggregated state per cluster).
- Basemap: **offline PMTiles** archive served by the node (`GET /api/v1/maps/tiles.pmtiles`,
  HTTP range requests, `Synoptics:Maps:PmTilesPath`, default `<Data>/maps/basemap.pmtiles`),
  vector style bundled (`offline-default` light and dark, labels in the user's language when the
  tiles carry `name:<lang>`, fallback `name`); optional online raster/vector source configured by
  the site (allow-listed URL).
- The installer (DEP) offers regional extracts (Protomaps builds of OpenStreetMap data, ODbL —
  attribution shown on the map); no internet access needed at runtime.
- Viewport subscriptions as §7.2 using map bounds; `maxZoom` 20 (indoor detail via linked plan
  documents: clicking a building zone opens its plan).

## 13. Settings, errors, observability

| Key | Default |
|---|---|
| `Synoptics:AllowedUrls` | [] (web widgets and URL links) |
| `Synoptics:MaxElements` / `MaxDocumentBytes` | 20,000 / 10 MB |
| `Synoptics:Maps:PmTilesPath` | `<Data>/maps/basemap.pmtiles` |

Errors: `synoptics.document.invalid` (with problems), `synoptics.binding.device_missing`,
`synoptics.binding.command_invalid`, `synoptics.expression.invalid`, `synoptics.url.not_allowed`,
`synoptics.background.unsupported`, `synoptics.background.too_large`, `synoptics.map.tiles_missing`.

| EventId | Level | Message |
|---|---|---|
| 3360 | Information | Background {FileId} processed ({Kind}, {Tiles} tiles) in {ElapsedMs} ms |
| 3361 | Warning | Background processing failed: {Error} |
| 3370 | Debug | Synoptic {Id} migrated v{From} → v{To} on read |

## 14. Tests

| Id | Test |
|---|---|
| SYN-U-001 | Schema validates all element types; rejects unknown fields/types |
| SYN-U-002 | Binding validation: missing devices/locations/files/synoptics → errors for new references, warnings for existing |
| SYN-U-003 | Command bindings validated against type commands and argument schemas |
| SYN-U-004 | Expression check through EXP (syntax, types, cost) |
| SYN-U-005 | URL allow-list |
| SYN-U-006 | Limits: elements count, document size, unique ids, acyclic groups, layers exist |
| SYN-U-007 | Warnings: device outside location, overlaps, missing translations, tiny symbols |
| SYN-U-008 | CFG apply stores document and replaces file references transactionally |
| SYN-U-009 | Thumbnail generation at publish |
| SYN-U-010 | for-location and for-device resolution (deepest location) |
| SYN-U-011 | Out-of-scope device elements stripped for non-configurators |
| SYN-U-012 | Migration v1 fixtures C# (and TS parity in SYN-U-026) |
| SYN-U-013 | Image tiling levels and tile addressing |
| SYN-U-014 | Tile endpoint cache headers and permissions |
| SYN-U-015 | SVG kept vector vs rasterised thresholds |
| SYN-U-016 | DXF inspect layers and conversion with units |
| SYN-U-017 | Symbol library: standard, package, site symbols listing |
| SYN-U-018 | Smart symbol library object type validation |
| SYN-U-019 | API list filters and thumbnails |
| SYN-U-020 | Rollback restores previous document and references |
| SYN-U-021…025 | Error translations, permissions per endpoint, soft lock behaviour, document read migrations logged once, large document (20,000 elements) validation < 2 s |
| SYN-U-026 | TS migrations parity with shared fixtures |
| SYN-U-027 | Renderer: each element type renders from fixture documents (Konva test canvas snapshot) |
| SYN-U-028 | ElementBoundary isolates a throwing element |
| SYN-U-029 | Appearance engine: state/property/expression rules, first match per channel, defaults |
| SYN-U-030 | Runtime expression errors ignored and flagged |
| SYN-U-031 | LOD thresholds and layer zoom ranges |
| SYN-U-032 | Per-frame coalescing of state updates |
| SYN-U-033 | Blink animation and reduced-motion alternative |
| SYN-U-034 | Deleted device rendering; out-of-scope hidden |
| SYN-U-035 | Spatial index visible set with margin |
| SYN-U-036 | Viewport subscription diffing and debounce |
| SYN-U-037 | > 2,000 visible devices forces zone-only mode with hint |
| SYN-U-038 | Aggregates subscription for zones |
| SYN-U-039 | Sparkline/chart series fetching when visible |
| SYN-U-040 | Click/double-click/context menu behaviours |
| SYN-U-041 | Command button uses CMD flow with confirmations |
| SYN-U-042 | Zone/link navigation and history |
| SYN-U-043 | Keyboard: zoom, pan, tab order (RTL-aware), Enter |
| SYN-U-044 | Search on plan centres and pulses |
| SYN-U-045 | Show on plan from alarm: document choice, centring, zoom, pulse |
| SYN-U-046 | Auto-focus on new Critical alarms option |
| SYN-U-047 | Breadcrumbs and parent navigation |
| SYN-U-048 | Deep links with focus |
| SYN-U-049 | Widgets: alarm list, event list, state counters, clock, text, image |
| SYN-U-050 | Web page widget sandboxed iframe only for allow-listed URLs |
| SYN-U-051 | Video tile widget placeholder when VID unavailable |
| SYN-U-052 | Touch: pinch zoom and long-press |
| SYN-U-053 | Performance: 1,000 symbols open < 1 s (benchmark in Playwright) |
| SYN-U-054 | Performance: 200 updates/s smooth (frame time budget) |
| SYN-U-055 | Memory: 5,000 symbols < 200 MB (desktop benchmark) |
| SYN-U-056…065 | Editor tools: select/marquee, shapes, zone polygon, text, image, link, widget placement; transforms; snapping and guides; align/distribute; z-order; group/lock |
| SYN-U-066…070 | Copy/paste across documents keeps bindings; duplicate; undo/redo; autosave to draft; soft lock read-only mode |
| SYN-U-071…078 | Layers panel; devices panel placed ticks and unplaced filter; drag-and-drop creates bound device elements; multi-drag row; auto-place grid; CSV positions import; place from geo |
| SYN-U-079…085 | Properties forms single/multi selection; appearance rules builder; CEL editor live result; symbols panel; site symbol upload sanitised |
| SYN-U-086…090 | Import wizard: PDF page/DPI via pdf.js, DXF layers, scale by two points, job progress, errors |
| SYN-U-091…095 | Smart symbols: create from selection, parameters, auto-fill from device links, form for remaining slots, library sharing |
| SYN-U-096…098 | Templates: blank, dashboard, building overview generated from REG children |
| SYN-U-099…100 | Simulation mode: overrides and scenarios; nothing sent to server |
| SYN-U-101…105 | Map: element coordinates, markers symbol layer and sprites, zones GeoJSON, camera cone sectors, clustering with aggregated states |
| SYN-U-106…110 | PMTiles endpoint range requests and caching; offline style light/dark; language labels with fallback; attribution |
| SYN-U-111…115 | Map viewport subscriptions; building zone opens linked plan; online source allow-list; map editor placement by drag and geo import; RTL controls |

## 15. Tasks

| Task | Sections | Tests |
|---|---|---|
| T6.01 | §4–§6, §13 | SYN-U-001…SYN-U-025 |
| T6.02 | §7–§8 | SYN-U-026…SYN-U-055 |
| T6.03 | §9–§11 | SYN-U-056…SYN-U-100 |
| T6.04 | §12 | SYN-U-101…SYN-U-115 |
