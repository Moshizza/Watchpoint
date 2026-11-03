# UIC — UI Core Packages

| | |
|---|---|
| Spec id | UIC |
| Packages | `src/ui/packages/api-client`, `live`, `model`, `i18n`, `ui`, `testing` (all `@watchpoint/*`) |
| Milestone | M0 (workspace skeleton), M1 |
| Tasks | T0.08 (§3), T1.21 (§4–§6), T1.22 (§7–§9) |
| Design docs | [Technology stack](../../docs/architecture/technology-stack.md), [Localisation](../../docs/modules/localisation.md) |

## 1. Purpose and scope

Shared TypeScript foundations for the desktop app, the web app and (partly) the mobile app:
typed REST client, live hub client, data stores and hooks, models, translations and right-to-left
support, design system components, test utilities.

Not in scope: feature screens (UIO, UIF), Electron main process (DSK), mobile screens (MOB).

## 2. Dependencies (all MIT unless noted)

| Package | Key dependencies |
|---|---|
| `api-client` | `openapi-fetch`, generated types from `openapi-typescript` |
| `live` | `@microsoft/signalr`, `@microsoft/signalr-protocol-msgpack`, `zustand` |
| `model` | none (types + pure functions); generated document types from `wptypes gen-ts` |
| `i18n` | `i18next`, `react-i18next`, `i18next-icu`, `intl-messageformat` (BSD-3) |
| `ui` | `react`, `react-dom`, Radix primitives, `tailwindcss` (v4), `class-variance-authority` (Apache-2), `lucide-react` (ISC), `@tanstack/react-table`, `@tanstack/react-virtual`, `@tanstack/react-query`, `react-hook-form`, `sonner` |
| `testing` | `vitest`, `@testing-library/react`, `@testing-library/user-event`, `msw`, `happy-dom` |

## 3. Workspace (T0.08)

```
src/ui/
├── package.json            private root: scripts lint, typecheck, test, build, gen:api, gen:types
├── pnpm-workspace.yaml     packages/*, apps/*
├── tsconfig.base.json      strict, noUncheckedIndexedAccess, exactOptionalPropertyTypes, moduleResolution bundler
├── eslint.config.js        typescript-eslint strict, react-hooks, jsx-a11y, custom rules (§3.1)
├── prettier.config.js
├── vitest.workspace.ts
├── tailwind/preset.ts      design tokens (§9.1), logical-property enforcement
└── packages/<name>/        package.json (exports map, "type": "module"), src/, tests next to sources (*.test.ts(x))
```

### 3.1 Custom lint rules (`eslint-plugin-watchpoint`, local package)

| Rule | Purpose |
|---|---|
| `wp/no-literal-strings` | JSX text and string props `title`, `aria-label`, `placeholder`, `alt` must come from `t()` (allow-list for symbols like `—`) |
| `wp/no-physical-direction-classes` | Forbid Tailwind `ml-`, `mr-`, `pl-`, `pr-`, `left-`, `right-`, `text-left`, `text-right`, `rounded-l-`… (use logical utilities) |
| `wp/no-direct-fetch` | Forbid `fetch(` outside `api-client` |
| `wp/test-id-name` | Test names must start with a test id (`UIC-U-012 …`) |

## 4. REST client (`@watchpoint/api-client`, T1.21)

- Types generated from `openapi/v1.json` (API §5.7) with `openapi-typescript` into
  `src/generated/schema.d.ts` (`pnpm gen:api`); committed; CI checks they are up to date.
- `createApiClient(options)` wraps `openapi-fetch`:

```ts
export interface ApiClientOptions {
  baseUrl: string;                                   // e.g. "https://wp.site.local:8443"
  auth: AuthProvider;                                // §6
  language: () => string;                            // sent as Accept-Language for display names
  onUnauthorized?: () => void;                       // after refresh failed
}
export interface ApiClient {
  GET: Client["GET"]; POST: Client["POST"]; PATCH: Client["PATCH"]; DELETE: Client["DELETE"];  // typed by path
  upload(path: string, file: Blob, onProgress?: (p: number) => void): Promise<unknown>;          // multipart with progress (XHR)
}
```

- Middleware: adds `Authorization`, `Accept-Language`; on 401 `identity.token.expired` → one
  refresh attempt then retry once; generates `Idempotency-Key` (UUID) for operations marked
  idempotent in the OpenAPI document (`x-wp-idempotent`).
- Errors: non-2xx with problem details → thrown `ApiError` (`status`, `code`, `messageKey`,
  `args`, `fieldErrors`, `traceId`); network errors → `NetworkError`.
- Query hooks: `@watchpoint/api-client/react` exports `useApiQuery(path, params, options)` and
  `useApiMutation(...)` on TanStack Query with query keys `[path, params]`.

## 5. Live client (`@watchpoint/live`, T1.21)

```ts
export interface LiveClientOptions {
  url: string;                            // `${baseUrl}/hub/v1?v=1`
  auth: AuthProvider;
  onStatus?: (s: LiveStatus) => void;     // "connecting" | "connected" | "reconnecting" | "disconnected"
}
export interface LiveClient {
  start(): Promise<void>;
  stop(): Promise<void>;
  subscribe<TItem, TDelta>(topic: string, filter: unknown, handlers: SubscriptionHandlers<TItem, TDelta>): LiveSubscription;
  status: LiveStatus;
  serverTimeOffsetMs: number;             // from Ping
}
export interface SubscriptionHandlers<TItem, TDelta> {
  onSnapshot(items: TItem[], sequence: number, complete: boolean): void;
  onDelta(delta: TDelta, sequence: number): void;
  onResync(reason: string): void;
  onError?(error: ApiError): void;
}
export interface LiveSubscription { update(filter: unknown): Promise<void>; close(): Promise<void>; lastSequence: number }
```

Behaviour:

- Uses `HubConnectionBuilder` with MessagePack protocol, automatic reconnect policy (0, 2, 5,
  10, 30 s then every 30 s), `accessTokenFactory` from `AuthProvider`.
- Renews the token on the hub (`RenewToken`) at 80 % of its lifetime.
- On reconnect, re-subscribes every active subscription with `ResumeFrom = lastSequence`.
- Ignores deltas with sequence ≤ last applied; applies snapshot chunks in order; on `Resync`,
  clears local state and waits for the new snapshot.
- `Closing` reasons: `sessionRevoked`/`maintenance` → stop reconnecting and notify the app
  (sign-out or maintenance screen); `tokenExpired` → refresh then reconnect; `overloaded`/
  `shutdown` → reconnect with back-off.

### 5.1 Device state store

```ts
// Ref-counted, viewport-friendly subscription manager on top of LiveClient.
export function useDeviceStates(ids: readonly string[]): ReadonlyMap<string, DeviceStateView>;
export function useDeviceState(id: string | undefined): DeviceStateView | undefined;
export function useAggregates(scopes: readonly AggregateScope[]): ReadonlyMap<string, AggregateCounts>;
```

- A Zustand store holds `Map<deviceId, DeviceStateView>`; components register interest; the
  manager **debounces** interest changes (100 ms) and maintains `devices` subscriptions in chunks
  of ≤ 2,000 ids (updates via `UpdateSubscription`), unsubscribing ids no longer needed after 5 s
  (so quick navigation back does not resubscribe).
- Deltas applied immutably per device; updates batched per animation frame
  (`requestAnimationFrame`) to avoid re-rendering on every message.
- `DeviceStateView` adds client-side helpers: `effectiveQuality(path)`, `overall`, `isStale`.

## 6. Authentication provider

```ts
export interface AuthProvider {
  getAccessToken(): Promise<string | undefined>;           // refreshes if expiring within 60 s
  refresh(): Promise<boolean>;
  signIn(login: string, password: string): Promise<SignInResult>;
  signOut(): Promise<void>;
  session: SessionInfo | undefined;                         // user, session id, client kind, expiry
  subscribe(listener: (s: SessionInfo | undefined) => void): () => void;
}
export interface PlatformTokenStore {                       // provided by the app
  loadRefreshToken(): Promise<string | undefined>;          // desktop: OS keychain via IPC; web: undefined (cookie)
  saveRefreshToken(token: string | undefined): Promise<void>;
}
export function createAuthProvider(api: ApiClient, store: PlatformTokenStore, client: ClientDescriptor): AuthProvider;
```

- Access tokens live **only in memory**. Refreshes are single-flight (concurrent callers share one
  request). Refresh failure → session cleared, `onUnauthorized`.
- `ClientDescriptor`: `{ kind: "desktop" | "web" | "mobile", clientInstanceId, appVersion, workstationId? }`;
  `clientInstanceId` generated once and persisted by the platform.

## 7. Models (`@watchpoint/model`)

- Generated document types (`wptypes gen-ts`) for capabilities and device types.
- API DTO aliases (re-exported from `api-client` generated schema) with friendly names:
  `Device`, `Server`, `Location`, `Tag`, `DeviceState`, `PropertyState`, `WpValue`…
- Pure helpers (unit-tested): `parseWpValue`, `formatWpValue(value, property, locale)` (units,
  decimals, enums via translations), `overallStateOrder`, `worstState(states)`,
  `locationPathNames(location, tree, culture)`, `displayName(entity, culture)`,
  `isConfigurationPermission(code)`.

## 8. Internationalisation (`@watchpoint/i18n`, T1.22)

### 8.1 Bundles and namespaces

| Source | Namespace | Location |
|---|---|---|
| Application strings | `app`, `errors`, `common` | `packages/i18n/locales/<culture>/<ns>.json` (bundled) |
| Types, capabilities, packages | `types` | fetched from `GET /api/v1/i18n/types/<culture>` (ETag-cached in memory and IndexedDB) |

Keys: lowercase dotted (`app.devices.list.title`); ICU MessageFormat via `i18next-icu`.

### 8.2 API

```ts
export function initI18n(options: { culture: string; fetchTypesBundle: (culture: string) => Promise<object> }): Promise<i18n>;
export function useT(ns?: string): (key: string, args?: Record<string, unknown>) => string;
export function useCulture(): { culture: string; dir: "ltr" | "rtl"; setCulture(c: string): Promise<void> };
export function translateError(e: ApiError | NetworkError, t: TFunction): { title: string; fields: Record<string, string> };
export function formatDateTime(d: Date | string, opts: { culture: string; timeZone: string; style?: "short" | "medium" | "long" }): string;
export function formatNumber(n: number, opts: { culture: string; unit?: string; decimals?: number }): string;
export function formatDuration(iso: string, culture: string): string;   // "PT1H30M" → "1 h 30 min" / localised
```

- Supported cultures from `/system/info`; default from user profile, else browser/OS.
- `setCulture` loads bundles, sets `document.documentElement.lang` and `dir`; components never
  read `dir` themselves except through `useCulture` (e.g. icon mirroring).
- Digits: Western digits by default for `ar` (`numberingSystem: "latn"`), user option `arab`.
- Missing key → in development shows `⟦key⟧` and logs once; in production falls back to `en`.
- Bidi: `bidiIsolate(text)` wraps interpolated user content in Unicode FSI/PDI; the ICU formatter is
  configured to isolate all arguments by default.
- **Pseudo-locales** (development and tests): `en-XA` (accented, +40 % length), `ar-XB` (RTL
  mirrored pseudo).

## 9. Design system (`@watchpoint/ui`, T1.22)

### 9.1 Tokens

CSS variables (light, dark — default for control rooms — and high-contrast themes):

| Token group | Examples |
|---|---|
| Surfaces | `--wp-bg`, `--wp-surface`, `--wp-surface-raised`, `--wp-border` |
| Text | `--wp-text`, `--wp-text-muted`, `--wp-text-inverse` |
| States | `--wp-state-alarm`, `-fault`, `-warning`, `-normal-active`, `-normal`, `-maintenance`, `-disabled`, `-offline`, `-unknown` (each with `-bg` and `-fg` variants, contrast ≥ 4.5:1 checked by test) |
| Priority bands (M2) | `--wp-prio-critical`, `-high`, `-medium`, `-low`, `-info` |
| Spacing, radius, font sizes | Tailwind scale; fonts Noto Sans / Noto Sans Arabic bundled (OFL) |

### 9.2 Components (M1 set)

| Component | Behaviour highlights |
|---|---|
| `AppShell` | Top bar, navigator (start side), main area, context panel (end side), dockable bottom panel slot; mirrors in RTL |
| `DataGrid<T>` | TanStack Table + Virtual; server-side paging via cursors (`useInfiniteQuery`), column visibility, sorting (server), resizing, keyboard navigation, selection, sticky header; 100,000 rows smooth |
| `VirtualTree<T>` | Lazy children, virtualised, keyboard (arrows, Home/End, type-ahead), expand state, badges slot, RTL arrows |
| `StateBadge`, `StateDot` | Overall state with colour **and** icon and text (accessibility) |
| `DeviceIcon` | Type icon (Lucide or `wp-*` custom SVG set) |
| `PropertyValue` | Formats a property with unit, enum label, quality marker (stale = grey + clock icon + tooltip with last seen) |
| `SchemaForm` | Renders a parameter schema (SCM §4.4): field types, `x-wp-ui` groups/advanced/visibleWhen, validation messages from field errors, secret fields (write-only "•••• (set)" + replace) |
| `EntityPicker` | Searchable picker for devices/servers/locations/users with filters from `entityRef` |
| `DurationInput`, `DateTimeInput` (time-zone aware), `LocalizedNameInput` (default + translations) |
| `ErrorView`, `ProblemToast` | Render `ApiError` with `translateError`; trace id copy button |
| `ConnectionBanner` | Live status: red banner when disconnected with "last update at …" |
| `ConfirmDialog` | Used by commands (M2) and destructive actions |
| `Toaster` | `sonner` |

All components: keyboard accessible, `aria-*` labels from translations, visible focus,
`prefers-reduced-motion` respected.

## 10. Test utilities (`@watchpoint/testing`)

- `renderWithProviders(ui, { culture, theme, session, apiHandlers, live })` wraps i18n, Query
  client, theme, auth.
- MSW handlers generated from the OpenAPI document for success/error responses; helpers
  `mockProblem(status, code, args)`.
- `FakeLiveHub`: in-memory implementation of the hub protocol (snapshot/delta/resync/closing)
  injectable into `LiveClient` via a transport option, to test stores and components without a
  server.
- Fixtures: generated devices/types/locations; pseudo-locale rendering helper
  `expectNoUntranslatedText(container)`.

## 11. Tests

| Id | Package | Test |
|---|---|---|
| UIC-U-001 | workspace | TypeScript strict build of all packages succeeds |
| UIC-U-002 | workspace | Lint rules: fixture files trigger each custom rule (no-literal-strings, physical direction classes, direct fetch, test id names) |
| UIC-U-003 | workspace | Vitest workspace runs every package's tests |
| UIC-U-004 | api-client | Generated types up to date (generation produces no diff) |
| UIC-U-005 | api-client | Authorization and Accept-Language headers added |
| UIC-U-006 | api-client | 401 expired → single refresh and retry; second 401 → onUnauthorized |
| UIC-U-007 | api-client | Concurrent 401s share one refresh |
| UIC-U-008 | api-client | Problem details → ApiError with field errors |
| UIC-U-009 | api-client | Network failure → NetworkError |
| UIC-U-010 | api-client | Idempotency-Key generated for idempotent operations, reused on retry |
| UIC-U-011 | api-client | Upload reports progress |
| UIC-U-012 | live | Subscribe receives chunked snapshot in order, `complete` on last chunk |
| UIC-U-013 | live | Deltas with sequence ≤ last ignored |
| UIC-U-014 | live | Reconnect resubscribes with ResumeFrom |
| UIC-U-015 | live | Resync clears subscription state |
| UIC-U-016 | live | Closing sessionRevoked stops reconnecting and notifies |
| UIC-U-017 | live | Closing tokenExpired → refresh then reconnect |
| UIC-U-018 | live | RenewToken at 80 % lifetime (fake timers) |
| UIC-U-019 | live | Server time offset computed from Ping |
| UIC-U-020 | live | `useDeviceStates` debounces interest and chunks subscriptions ≤ 2,000 ids |
| UIC-U-021 | live | Ids no longer needed unsubscribed after 5 s grace |
| UIC-U-022 | live | Updates batched per animation frame (100 deltas → 1 render) |
| UIC-U-023 | live | `useAggregates` subscribes and updates counts |
| UIC-U-024 | auth | Access token kept in memory only (no localStorage/sessionStorage writes) |
| UIC-U-025 | auth | Refresh token persisted through PlatformTokenStore (desktop) / not persisted (web) |
| UIC-U-026 | auth | Sign-in handles `next: mfa` and `next: changePassword` outcomes |
| UIC-U-027 | auth | Sign-out clears session and calls API |
| UIC-U-028 | model | `formatWpValue` for every value kind incl. units and enum labels |
| UIC-U-029 | model | `worstState` ordering |
| UIC-U-030 | model | `displayName` resolution with translations fallback |
| UIC-U-031 | i18n | Culture switch sets `lang` and `dir` (ar → rtl) |
| UIC-U-032 | i18n | ICU plurals in en/fr/ar (ar has 6 plural forms: zero, one, two, few, many, other) |
| UIC-U-033 | i18n | Missing key: dev marker, prod fallback to en |
| UIC-U-034 | i18n | Types bundle fetched and cached by ETag |
| UIC-U-035 | i18n | `translateError` uses `errors.<code>` with args and field messages |
| UIC-U-036 | i18n | Arguments bidi-isolated by default |
| UIC-U-037 | i18n | Date formatting in site vs user time zone |
| UIC-U-038 | i18n | Number formatting: Western digits default for ar; `arab` option |
| UIC-U-039 | i18n | Duration formatting localised |
| UIC-U-040 | i18n | Pseudo-locales render without untranslated text in UIC components |
| UIC-U-041 | i18n | Every key used in code exists in en, fr, ar (static extraction check) |
| UIC-U-042 | ui | State tokens contrast ≥ 4.5:1 in all themes (computed) |
| UIC-U-043 | ui | AppShell mirrors navigator/context panel sides in RTL |
| UIC-U-044 | ui | DataGrid renders 100,000 rows virtualised (< 200 DOM rows) |
| UIC-U-045 | ui | DataGrid infinite loading with cursors |
| UIC-U-046 | ui | DataGrid keyboard navigation and selection |
| UIC-U-047 | ui | VirtualTree lazy loading, keyboard, type-ahead |
| UIC-U-048 | ui | VirtualTree arrow keys swap meaning in RTL |
| UIC-U-049 | ui | StateBadge shows icon and text, not colour only |
| UIC-U-050 | ui | PropertyValue stale marker and tooltip |
| UIC-U-051 | ui | SchemaForm renders each field type and default values |
| UIC-U-052 | ui | SchemaForm visibleWhen, groups, advanced section |
| UIC-U-053 | ui | SchemaForm maps field errors by JSON pointer |
| UIC-U-054 | ui | SchemaForm secret field write-only behaviour |
| UIC-U-055 | ui | EntityPicker filters by entityRef constraints |
| UIC-U-056 | ui | LocalizedNameInput edits default and translations |
| UIC-U-057 | ui | DateTimeInput time zone aware around DST |
| UIC-U-058 | ui | ErrorView shows translated title and trace id |
| UIC-U-059 | ui | ConnectionBanner states |
| UIC-U-060 | ui | Accessibility: axe-core checks on component stories (no serious violations) |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T0.08 | §3 | UIC-U-001…UIC-U-003 |
| T1.21 | §4, §5, §6, §7 | UIC-U-004…UIC-U-030 |
| T1.22 | §8, §9, §10 | UIC-U-031…UIC-U-060 |
