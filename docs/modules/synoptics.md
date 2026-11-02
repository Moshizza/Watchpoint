# Synoptics — Interactive Screens Without Code

> Requirement: synoptics must be simple to create, with very little to no risk of
> breaking, and **never** show code or compiler error messages to people who don't code.

## 1. Approach

A synoptic is a **document** (JSON, validated against a schema) rendered by a fixed,
tested engine in the clients. Nothing is compiled, nothing is generated as code, nothing
is executed except safe, bounded expressions. As a result:

- a synoptic cannot crash the client or the server;
- a broken reference (deleted device) shows a grey "unavailable" symbol, never an error;
- the editor validates while you work and explains problems in plain language;
- a screen saved by one user cannot affect other screens.

The same renderer runs in the desktop client and the web client (React + Konva).

## 2. Kinds of synoptics

| Kind | Background | Typical use |
|---|---|---|
| **Plan** | Floor plan (PDF, DXF, SVG, PNG/JPEG) | Building floors, car parks, technical rooms |
| **Map** | Geographic map (MapLibre with OpenStreetMap online or an offline tile file) | Campus, city, perimeter, multi-site overview |
| **Diagram** | Blank canvas with shapes | Single-line diagrams of BMS, network overview, control panels with buttons |
| **Dashboard** | Grid of widgets | Overview screens: counters per site, alarm lists, video tiles, charts |

All kinds share the same element model; plans and maps add a positioned background.

## 3. Element model

| Element | Description |
|---|---|
| **Device symbol** | Bound to one device. Uses the device type's default symbol: appearance per overall state (normal, alarm, fault, offline…), optional badges (alarm counter, maintenance), label (name). Click → device panel with commands; double-click → device page; right-click → context actions |
| **Location zone** | Polygon bound to a location; filled with the aggregated state colour (e.g. a room turns red when a device inside is in alarm); click → navigates to that location's synoptic |
| **Camera cone** | For cameras: field-of-view sector from the device's geometry; click → live video |
| **Navigation link** | Button/hotspot opening another synoptic, a location, a workspace or a URL |
| **Command button** | Runs one command on one device or a group command (with the normal confirmation and rights rules) |
| **Automation button** | Starts an automation (with rights) |
| **Value display** | Shows a property (temperature, count, armed state) with formatting and unit |
| **Gauge / bar / sparkline** | Visual for measurements |
| **Widgets** | Alarm list (filtered), event list, video tile (live), counters per state for a scope, chart, clock, text block, image, web page (allow-listed) |
| **Shapes and text** | Lines, rectangles, polygons, icons (from a free icon set), labels, images |
| **Group** | Several elements moved/configured together |
| **Smart symbol** | A reusable composite with parameters (e.g. "Door block" = door symbol + reader badge + linked camera button), placed and filled in one action |

### 3.1 Bindings without code

Everything dynamic is configured with **forms**, not code:

- **Device binding**: choose a device from a searchable picker (filtered by location and
  type) or drag a device from the device tree onto the canvas — the right symbol appears
  already bound.
- **Appearance rules**: "When *state* is *alarm* → blink red" are pre-filled from the device
  type; overrides are chosen from lists (state → colour, icon, blink, visibility).
- **Advanced conditions** (optional, for power users): a CEL expression such as
  `door.doorPosition.position == "open" && site.mode == "night"`, typed in a small editor
  with completion of device names and properties, live preview of the current result, and
  plain-language errors. Expressions are bounded (no loops, cost-limited) and cannot have
  side effects.

## 4. Editor

Located in the desktop client, configuration side (*Configuration → Synoptics*).

| Feature | Description |
|---|---|
| Canvas | Zoom, pan, rulers, grid and snapping, alignment guides, layers (background, zones, devices, annotations), lock layers |
| Background import | Drop a PDF/DXF/SVG/image; choose page and scale; DXF layers selectable; converted to a vector/tiled background stored in the file store |
| Device tree panel | Technical tree and location tree; drag devices onto the plan; already-placed devices are ticked; "show unplaced devices of this floor" |
| Auto-placement helpers | Place selected devices in a row/grid; import positions from CSV; for maps, place devices from their geo-coordinates |
| Properties panel | Form for the selected element(s); multi-selection editing |
| Symbol library | Standard symbols per device type and state (free SVG set designed for Watchpoint, colour-blind safe), integration-provided symbols, site symbols (import SVG) |
| Smart symbols | Create from a selection, define parameters, reuse |
| Templates | Start from a template (floor plan, dashboard layout, overview map) |
| Undo/redo | Unlimited during the session; autosave of the draft |
| Preview | Live preview with real states (read-only) or **simulation mode** (pick states to see how the screen looks during a fire alarm, offline, etc.) |
| Validation panel | List of problems with links (unbound symbol, device out of the plan's location, overlapping symbols, missing translations of labels) |
| Versions | Publish, compare versions visually, roll back |
| Accessibility | Minimum symbol size checks, colour + icon state cues |

### 4.1 What can never happen

| Classic failure | Why it cannot happen |
|---|---|
| Compile error on save | Nothing is compiled |
| One broken screen blocks all screens | Each document is independent; the renderer isolates errors per element |
| Screen runs arbitrary code on operator PCs | No code in documents; expressions are sandboxed CEL; web widgets only for allow-listed URLs in a sandboxed frame |
| Renaming a device breaks a screen | Bindings use device ids, not names |
| Deleted device crashes the screen | Unbound/orphaned elements render as grey "unavailable" with a tooltip |

## 5. Viewer (operation side)

- Opens in any window/panel of a workspace; multi-monitor in the desktop client.
- Smooth zoom with **level of detail**: at low zoom only zones and aggregated counters are
  shown; symbols appear when zoomed in; labels when closer.
- Live updates via the client's subscription for **only the devices visible** in the
  current viewport (subscriptions change as the user pans/zooms), so a plan with 5,000
  devices stays light.
- Alarm focus: from an alarm, "show on plan" opens the location's synoptic centred and
  pulsing on the device.
- Navigation history (back/forward), breadcrumbs by location, search on the plan.
- Touch support (tablets, touch tables) and keyboard navigation.

## 6. Performance targets

| Metric | Target |
|---|---|
| Open a plan with 1,000 symbols | < 1 s on a standard workstation |
| Update rate | 200 symbol updates/s smoothly (coalesced per animation frame) |
| Memory | < 200 MB for a 5,000-symbol plan in the desktop client |

The renderer uses Konva layers: static background cached as bitmap tiles, symbols layer
redrawn only where changes occur.

## 7. Storage

- Synoptic documents in `config.synoptics` (JSON + version), referencing files in the file
  store (backgrounds, images, symbols).
- Document schema versioned; the engine migrates older documents on load (never breaks
  old screens).
