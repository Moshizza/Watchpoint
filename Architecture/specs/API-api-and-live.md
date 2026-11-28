# API — REST Framework and Live Hub

| | |
|---|---|
| Spec id | API |
| Projects | `src/service/Watchpoint.Api` (framework, hub, system endpoints); modules own their endpoints |
| Milestone | M1 |
| Tasks | T1.19 (§4–§7), T1.20 (§8–§10) |
| Design docs | [API and live protocol](../../docs/modules/api-and-protocol.md), [Architecture README §6](../README.md#6-conventions--api) |

## 1. Purpose and scope

- The **HTTP pipeline** every endpoint runs through: authentication, authorisation, client-kind
  checks, rate limiting, errors as problem details, paging, ETags, idempotency, OpenAPI.
- The **live hub**: one SignalR connection per client with typed subscriptions, snapshots and
  deltas, sequence numbers, resume, coalescing and limits.
- System endpoints (`/system/*`).

Endpoints of each domain are specified in the module's spec (REG §11, STA §7, IRT §10, MOD §7,
IDN §7.2, …) and implemented in the module with the helpers defined here.

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST, IDN (token validation, sessions), RGT (permissions, from M3; M1 fallback §5.2), STO (idempotency table), ASP.NET Core minimal APIs, `Microsoft.AspNetCore.OpenApi`, SignalR + `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`, rate limiting middleware | All modules exposing endpoints or live topics; all clients |

## 3. Project layout

```
Watchpoint.Api/
├── Contracts/
│   ├── EndpointConventions.cs        RequirePermission, RequireClientKind, ConfigurationEndpoint, Versioned…
│   ├── HttpResults.cs                Result → IResult mapping
│   ├── PagingBinding.cs, SortBinding.cs
│   ├── ICurrentUser.cs               per-request identity (user, session, client kind, language, permissions access)
│   ├── Live/ILiveTopic.cs, ILiveSubscription.cs, LiveMessages.cs, SubscriptionRequest.cs
│   └── ApiErrors.cs
├── Internal/
│   ├── Pipeline/ProblemDetailsWriter.cs, ExceptionHandler.cs, RequestLogging.cs, RateLimits.cs
│   ├── Auth/JwtAuthenticationHandler.cs, PermissionFilter.cs, ClientKindFilter.cs, SessionTouchMiddleware.cs
│   ├── Idempotency/IdempotencyFilter.cs, IdempotencyStore.cs
│   ├── OpenApi/OpenApiSetup.cs
│   ├── Live/LiveHub.cs, ConnectionState.cs, OutboundQueue.cs, SubscriptionRouter.cs, TokenRenewal.cs
│   ├── Live/Topics/DevicesTopic.cs, AggregatesTopic.cs, ConnectionsTopic.cs, SystemTopic.cs, JobsTopic.cs
│   └── System/SystemEndpoints.cs
├── ApiModule.cs
└── ApiLog.cs                          event ids 2000–2199
```

As built (T1.19): the host module is `Watchpoint.Host/Modules/ApiModule.cs`; `Internal/System` is
`Internal/Platform` (a `System` namespace segment shadows `System.*`). The API references IDN (token validation), so
the IDN endpoints (§7) are in `Internal/Accounts/AuthEndpoints.cs`. Every other module references the API and maps
its own endpoints with these helpers (`RegistryEndpoints`, `StateEndpoints`, `ModelEndpoints`, `PackageEndpoints`,
`ConnectionEndpoints` in each module's `Contracts/`); the API never references a domain module, so the live topics
over REG/STA (§8.5, T1.20) belong to the module that owns the data (e.g. `DevicesTopic` in STA) or to a project above both.
Idempotency records use the configuration store (`ApiStorage.AddApiStorage`, schema `wp_api`); without storage
(unit tests) they are held in node memory.

## 4. HTTP pipeline (in order)

| # | Middleware | Behaviour |
|---|---|---|
| 1 | Forwarded headers | Only when `Http:TrustedProxies` is set (CIDR list); otherwise ignored |
| 2 | HSTS + HTTPS redirection | Production only, on the main endpoint |
| 3 | Exception handler | Unhandled exception → 500 problem details `common.internal` with `traceId`; exception logged (2001 Error) |
| 4 | Request logging | One structured line per request at Information for mutating methods, Debug for GET: method, route template (not raw path), status, duration, user id, session id, client kind; **never** query strings containing `access_token`, bodies or headers |
| 5 | Response compression | Brotli/gzip for JSON and SVG responses ≥ 1 KB; never for already compressed content (images, archives) |
| 6 | Rate limiting | §4.2 — runs **after** rows 7–8 (as built, T1.19): the authenticated partition key is the session |
| 7 | Authentication | JWT bearer (`Authorization: Bearer`), or `access_token` query parameter **only** for `/hub/v1` negotiate/connect; validation by IDN (§6.2 of IDN), session revocation check |
| 8 | Session touch | `ISessionRegistry.Touch` for authenticated requests |
| 9 | Endpoint filters | Permission, client kind, idempotency, ETag (per endpoint conventions); before them a body-limit check (§4.1: a declared `Content-Length` over the endpoint's limit → 413 `api.request.too_large`) |
| 10 | CORS | No CORS policy (same origin only); the web client is served by the node |

### 4.1 Limits

Request body default 10 MB (endpoint-specific overrides: package upload 500 MB, plan upload
50 MB), JSON max depth 32, header size 32 KB, URL length 8 KB, request timeout 60 s for normal
endpoints (long operations return `202` + job id, §6.6).

### 4.2 Rate limits

| Policy | Partition key | Limit |
|---|---|---|
| `anonymous` | remote IP | 10 requests/s, burst 20 |
| `authenticated` | session id | 50 requests/s, burst 100 |
| `signin` | IP and login (IDN) | IDN §7.1 |
| `heavy` (history queries, exports, discovery) | session id | 2 concurrent, queue 4 |

Exceeded → 429 problem `common.rate_limited` with `Retry-After`.

## 5. Endpoint conventions

### 5.1 Registration

```csharp
// In a module's MapEndpoints:
var g = endpoints.MapGroup("/api/v1/devices").WithTags("Devices");
g.MapGet("/", ListDevices).RequirePermission(Permissions.DevicesView).WithPaging().Produces<PageDto<DeviceDto>>();
g.MapPatch("/{id}", PatchDevice).RequirePermission(Permissions.RegistryEdit).ConfigurationEndpoint().Versioned();
```

| Convention | Meaning |
|---|---|
| `.RequirePermission(code)` | Permission filter (§5.2); `.AllowAnonymous()` must be explicit for anonymous endpoints |
| `.RequireAuthentication()` | Authenticated user, no specific permission (e.g. `/auth/me`, own jobs and sessions, MOD reads) → 401 when anonymous |
| `.ConfigurationEndpoint()` | Requires client kind `desktop` (§5.3) and marks the operation `x-wp-config` in OpenAPI |
| `.RequireClientKind(params ClientKind[])` | Explicit client kind restriction |
| `.Versioned()` | Requires `If-Match` on PATCH/PUT/DELETE (428 `common.version.required` if missing) and writes `ETag` on responses |
| `.Idempotent()` | Honours `Idempotency-Key` (§5.5) |
| `.WithPaging(defaultLimit = 100)` | Binds `limit`, `cursor`, `sort` |
| `.Heavy()` | Applies the `heavy` rate limit policy |

An architecture test (API-U-030) asserts every endpoint has exactly one of
`RequirePermission`/`RequireAuthentication`/`AllowAnonymous`, and a unique operation id (`WithName`); the host runs
the same check (`ApiConventions.Validate`) before it listens, and a host-wide test composes every module.

### 5.2 Permission filter

```csharp
public interface ICurrentUser
{
    UserId UserId { get; }
    SessionId SessionId { get; }
    ClientKind ClientKind { get; }
    bool IsAdministrator { get; }
    string Language { get; }
    ValueTask<bool> HasPermissionAsync(PermissionCode permission, CancellationToken ct);
    ValueTask<IVisibleScope> GetVisibleScopeAsync(PermissionCode permission, CancellationToken ct); // RGT; M1: everything
}
```

- **M1–M2 fallback** (before RGT): permissions starting with `config.` or `admin.` require
  `adm=true`; other permissions require authentication only. `GetVisibleScopeAsync` returns
  "everything".
- **M3+**: `IRightsEvaluator` (RGT) decides. Object-level checks (scopes) are done by the
  endpoint using `GetVisibleScopeAsync` (e.g. REG `DeviceQuery.Within`) — the filter checks only
  that the user holds the permission somewhere.
- Denied → 403 problem `common.forbidden` with arg `permission`.

### 5.3 Client kind rule for configuration

`ConfigurationEndpoint()` returns 403 `api.client_kind.not_allowed` (args `required: desktop`)
when the token's `ck` is not `desktop`. This enforces "no configuration from the web or mobile
clients" on the server, independent of UI bundles.

### 5.4 Results to HTTP

`HttpResults.From(Result<T> r, Func<T, IResult> onSuccess)` maps errors by `ErrorKind`
(README §6.2) into problem details:

```json
{ "type": "https://watchpoint.dev/errors/<code>", "title": "<code>", "status": 404,
  "code": "registry.device.not_found", "messageKey": "errors.registry.device.not_found",
  "args": { "id": "…" }, "errors": [ { "path": "/settings/port", "code": "common.value.out_of_range", "args": { "min": 1, "max": 65535 } } ],
  "traceId": "00-…" }
```

`title` is the code (clients translate via `messageKey`); `DeveloperDetail` never serialised.
`common.version.conflict` always maps to 412 (whatever kind a module gave it), `common.version.required` to 428 and
`api.request.too_large` to 413.

### 5.5 Idempotency

- Header `Idempotency-Key` (1–64 chars, `[A-Za-z0-9_-]`) on endpoints marked `.Idempotent()`.
- Store `wp_api.idempotency(key, user_id, method, route, body_sha256, status, response_body bytea,
  response_headers jsonb, created_at)`, PK (user_id, key), retention 24 h (purged hourly).
- Same key, same body hash → stored response replayed with header `Idempotent-Replayed: true`.
  Same key, different body → 422 `api.idempotency.mismatch`. Concurrent duplicate while the first
  is running → 409 `api.idempotency.in_progress`.

### 5.6 Long-running operations

Endpoints that may take longer than a few seconds (discovery, imports, exports, report
generation) return `202 Accepted` with `{ "jobId": "…", "status": "running" }` and `Location:
/api/v1/jobs/{id}`. Jobs (`IJobRegistry` in this module): status `running | succeeded | failed |
cancelled`, progress (0–100 + message key), result (JSON or file id), errors; retained 24 h;
`DELETE /api/v1/jobs/{id}` cancels. Progress also pushed on live topic `jobs`.

### 5.7 OpenAPI

- Document generated with `Microsoft.AspNetCore.OpenApi` at `/openapi/v1.json` (Development
  only at runtime). CI exports it (`dotnet run --project tools/export-openapi`) to
  `src/ui/packages/api-client/openapi/v1.json`; the UI client is generated from it; CI fails if the
  committed file differs from the generated one (`API-I-015`).
- Every endpoint has an operation id `<Tag>_<Verb><Noun>` (e.g. `Devices_ListDevices`), summary,
  response types, problem responses, permission (`x-wp-permission`), `x-wp-config`.

## 6. System endpoints (owned by API)

| Method and path | Auth | Response |
|---|---|---|
| `GET /api/v1/system/info` | anonymous | `{ product: "Watchpoint", version, apiVersion: "1.0", liveProtocol: 1, siteName, nodeId, nodeName, environment, features: [...], supportedCultures: ["en","fr","ar"], time: "…" }` (features = enabled modules) |
| `GET /api/v1/system/ca` | anonymous | SEC §7 CA certificate PEM + SHA-256 fingerprint |
| `GET /api/v1/system/nodes` | authenticated | Node list (M1: one) for client failover (CLU later) |
| `GET /api/v1/jobs/{id}`, `DELETE /api/v1/jobs/{id}` | owner or administrator | §5.6 |

## 7. M1 endpoint inventory (implemented by their modules with this framework)

| Module | Endpoints | Mapped before T1.19 |
|---|---|---|
| IDN | auth (sign-in, refresh, sign-out, change-password, me), users, sessions | No |
| MOD | types, capabilities, i18n bundles, symbols | No (MOD-I-003/004 test `ITypeResources` until T1.19) |
| REG | site, locations, tags, tag families, servers, devices, children, links, discovered items, registry batch, device patch | No |
| STA | device state, state batch, states query, aggregates | No |
| IRT | packages, connections and their actions | Packages only (T1.15, `PackageEndpoints`, without the permission filter; the POSTs in Development only, IRT §10); connections with T1.16 |
| API | system, jobs | No (host health endpoints `/health/*`, `/metrics` are HST §6) |

T1.19 moved the early endpoints onto the permission filter and problem details. As built for IDN: sign-in, refresh,
sign-out, change-password, me, MFA verify and TOTP enrolment, sessions (list, revoke) and user reads; user changes,
self-service MFA, step-up and lockout endpoints come with their IDN tasks.

## 8. Live hub (T1.20)

### 8.1 Connection

- SignalR hub at `/hub/v1`, transports WebSocket (preferred), Server-Sent Events and long
  polling (automatic fallback), **MessagePack** protocol (JSON allowed for debugging in
  Development).
- Authentication: access token at negotiate/connect (`access_token` query, standard for
  SignalR WebSockets). The connection is bound to the session.
- Token lifetime: the client calls `RenewToken(accessToken)` before expiry (recommended at 80 %
  of lifetime). If the token expires without renewal, the server sends `Closing("tokenExpired")`
  after a 60 s grace and closes.
- Keep-alive 15 s; client timeout 30 s.
- Protocol version: the client passes `?v=1`; unsupported → connection refused with HTTP 400
  `api.live.protocol_unsupported`.

### 8.2 Client → server methods

```csharp
public interface ILiveHubServer
{
    Task<SubscribeResult> Subscribe(SubscriptionRequest request);
    Task<SubscribeResult> UpdateSubscription(string subscriptionId, SubscriptionRequest request);  // e.g. viewport change
    Task Unsubscribe(string subscriptionId);
    Task RenewToken(string accessToken);
    Task<long> Ping(long clientTime);          // returns server time (ms) for clock offset display
}

public sealed record SubscriptionRequest(string Topic, JsonElement Filter, long? ResumeFrom /* last sequence seen */,
    string? ClientSubscriptionId);
public sealed record SubscribeResult(string SubscriptionId, bool Resumed, long Sequence, Error? Error);
```

### 8.3 Server → client messages

```csharp
public interface ILiveHubClient
{
    Task Snapshot(SnapshotMessage message);    // sent after Subscribe (unless resumed), possibly in chunks
    Task Delta(DeltaMessage message);
    Task Resync(ResyncMessage message);        // client must drop local state of the subscription; a new Snapshot follows
    Task Notice(NoticeMessage message);        // system notices: maintenance, licence banners, session warnings
    Task Closing(ClosingMessage message);      // the server is about to close: reason code
}

public sealed record SnapshotMessage(string SubscriptionId, long Sequence, int Chunk, int ChunkCount, ReadOnlyMemory<byte> ItemsMsgPack);
public sealed record DeltaMessage(string SubscriptionId, long Sequence, ReadOnlyMemory<byte> ItemsMsgPack);
public sealed record ResyncMessage(string SubscriptionId, string Reason /* overflow | rightsChanged | filterInvalidated | sequenceGap */);
public sealed record NoticeMessage(string Code, IReadOnlyDictionary<string, object?> Args, string Severity);
public sealed record ClosingMessage(string Reason /* sessionRevoked | tokenExpired | maintenance | overloaded | shutdown */);
```

Items are topic-specific DTOs serialised with MessagePack (contractless resolver disabled;
generated formatters) — schemas listed per topic (§8.5) and exported as TypeScript types.

### 8.4 Sequences and resume

- Each topic defines the meaning of `Sequence`: for `devices` and `aggregates` it is the STA change
  sequence (event-log sequence); for `alarms`/`events`/`commands` (M2) the log sequence of the
  record; for `connections`/`system`/`jobs` a per-node counter (resume not supported: always
  snapshot).
- `ResumeFrom` given and the topic can serve changes since that sequence (STA
  `GetChangesSince`) → `Resumed=true`, then deltas; otherwise snapshot.
- Deltas of one subscription are delivered **in sequence order**; the client ignores deltas with a
  sequence ≤ the last one applied.

### 8.5 Topics (M1)

| Topic | Filter | Snapshot items | Delta items | Limits |
|---|---|---|---|---|
| `devices` | `{ "ids": [...] }` (≤ 2,000) **or** `{ "query": <REG DeviceQuery JSON> }` (result ≤ 5,000 devices; above → `api.live.too_many_devices`) | `DeviceStateDto` (STA §7.1 shape, plus `displayName`) | `DeviceStateDeltaDto { id, seq, changed: { path: PropertyStateDto }, health?, overall?, serverUnavailable? }` | 20 `devices` subscriptions per connection |
| `aggregates` | `{ "scopes": [{ kind, id }] }` (≤ 500) | `AggregateDto { scope, total, byState }` | same (only changed scopes) | |
| `connections` | `{}` or `{ "ids": [...] }` | `ConnectionStatusDto` | same | config permission |
| `system` | `{}` | node info, maintenance status, licence banner state | changes | always available |
| `jobs` | `{ "ids": [...] }` | job status | progress changes | own jobs |

Modules add topics (M2: `alarms`, `events`, `commands`) through:

```csharp
public interface ILiveTopic
{
    string Name { get; }
    PermissionCode Permission { get; }
    ValueTask<Result<ILiveSubscription>> CreateAsync(ICurrentUser user, JsonElement filter, long? resumeFrom, ILiveSink sink, CancellationToken ct);
    bool Coalescable { get; }            // true: deltas keyed by item id can replace pending ones
}
public interface ILiveSubscription : IAsyncDisposable
{
    ValueTask<SubscribeOutcome> StartAsync(CancellationToken ct);       // sends snapshot or resumes
    ValueTask<Result> UpdateFilterAsync(JsonElement filter, CancellationToken ct);
}
public interface ILiveSink
{
    void Snapshot(long sequence, IReadOnlyList<object> items, bool last);
    void Delta(long sequence, object item, string? coalesceKey);       // coalesceKey e.g. device id
    void Resync(string reason);
}
```

### 8.6 Outbound queue, coalescing, overload

- One `OutboundQueue` per connection: ordered queue of pending messages; for coalescable topics,
  a pending delta with the same `(subscriptionId, coalesceKey)` is **merged** (changed properties
  union, latest values win) instead of enqueued twice.
- Send loop writes to SignalR; message rate limit 500 messages/s per connection (excess stays
  queued and is coalesced).
- Queue length > 10,000 messages: coalescable subscriptions get `Resync("overflow")` (their pending
  deltas dropped); if non-coalescable messages alone exceed 10,000 → `Closing("overloaded")` (the
  client reconnects and resumes or resyncs).
- **Rights changes** (RGT event `rights.changed` for a user) → all subscriptions of that user's
  connections re-validated: filters re-resolved with the new visible scope → `Resync("rightsChanged")`.
- **Session revoked** (`session.revoked`) → `Closing("sessionRevoked")` then close.
- **Maintenance** (MNT, M3) → `Closing("maintenance")` for operation-side sessions.
- Shutdown → `Closing("shutdown")`.

### 8.7 Live limits

| Limit | Value | Error |
|---|---|---|
| Connections per session | 4 | `api.live.too_many_connections` |
| Subscriptions per connection | 50 | `api.live.too_many_subscriptions` |
| Devices per `devices` subscription | 2,000 ids / 5,000 by query | `api.live.too_many_devices` |
| Unknown topic | — | `api.live.topic_unknown` |
| Invalid filter | — | `api.live.filter_invalid` (field errors) |

## 9. Settings

| Key | Default |
|---|---|
| `Http:TrustedProxies` | empty |
| `Api:RateLimits:*` | §4.2 |
| `Api:MaxBodyBytes` | 10 MB |
| `Live:OutboundQueueMax` | 10000 |
| `Live:MessagesPerSecond` | 500 |
| `Live:TokenExpiryGrace` | `PT60S` |
| `Live:AllowJsonProtocol` | false (true in Development) |

## 10. Errors, observability, security

| Code | Kind |
|---|---|
| `api.client_kind.not_allowed` | Forbidden |
| `common.version.required` | PreconditionFailed (428) |
| `api.idempotency.mismatch` / `in_progress` | Unprocessable / Conflict |
| `api.idempotency.key_invalid` | Validation (key not 1–64 of `[A-Za-z0-9_-]`) |
| `api.request.invalid` | Validation (malformed body, JSON depth > 32, wrong parameter type) |
| `api.request.too_large` | 413 (args `max`) |
| `api.job.not_found` | NotFound |
| `api.live.protocol_unsupported` / `topic_unknown` / `filter_invalid` / `too_many_connections` / `too_many_subscriptions` / `too_many_devices` | Validation / Conflict |

| EventId | Level | Message |
|---|---|---|
| 2001 | Error | Unhandled exception for {Method} {Route} trace {TraceId} |
| 2002 | Information/Debug | {Method} {Route} → {Status} in {DurationMs} ms (user {UserId}) |
| 2010 | Information | Live connection {ConnectionId} opened for session {SessionId} ({ClientKind}) |
| 2011 | Information | Live connection {ConnectionId} closed: {Reason} |
| 2012 | Warning | Live connection {ConnectionId} overflowed: {Pending} pending |
| 2013 | Warning | Live connection {ConnectionId} closed as overloaded |

Metrics (`Watchpoint.Api`): `watchpoint.api.requests` (counter by `route`, `status`),
`watchpoint.api.request.duration` (histogram by `route`), `watchpoint.api.live.connections`
(gauge by `clientKind`), `watchpoint.api.live.subscriptions` (gauge by `topic`),
`watchpoint.api.live.messages` (counter by `topic`, `kind`), `watchpoint.api.live.queue`
(histogram of queue length), `watchpoint.api.live.resyncs` (counter by `reason`).

Security: tokens never logged (query string scrubbing tested; the host keeps the ASP.NET Core categories
`Microsoft.AspNetCore.Hosting.Diagnostics` and `Microsoft.AspNetCore.Routing`, which log raw paths and query strings, at
Warning); no CORS; strict JSON parsing;
per-object authorisation responsibility documented per endpoint; problem details never contain
stack traces.

## 11. Tests

Unit: `tests/service/Watchpoint.Api.Tests` (WebApplicationFactory with fake modules).
Integration: `Watchpoint.IntegrationTests/Api` (full host with PostgreSQL, NATS, simulator).

| Id | Layer | Test |
|---|---|---|
| API-U-001 | U | Result mapping for every ErrorKind to status codes |
| API-U-002 | U | Problem details shape (code, messageKey, args, errors, traceId); no DeveloperDetail |
| API-U-003 | U | Unhandled exception → 500 `common.internal`, logged with trace id |
| API-U-004 | U | Request log contains route template, never query `access_token` or body |
| API-U-005 | U | Anonymous endpoint without `AllowAnonymous` rejected at startup (convention check) |
| API-U-006 | U | Missing/invalid/expired token → 401 with `identity.token.*` code |
| API-U-007 | U | Revoked session token → 401 `identity.token.revoked` |
| API-U-008 | U | M1 permission fallback: `config.*` requires admin; `operate.*` authenticated |
| API-U-009 | U | Permission filter uses RGT when registered (M3 swap) |
| API-U-010 | U | Configuration endpoint from `web` or `mobile` token → 403 `api.client_kind.not_allowed` |
| API-U-011 | U | Versioned PATCH without If-Match → 428 `common.version.required` |
| API-U-012 | U | ETag written on GET of versioned resource; stale If-Match → 412 |
| API-U-013 | U | Idempotency replay returns stored response with header |
| API-U-014 | U | Idempotency key reuse with different body → 422 |
| API-U-015 | U | Concurrent duplicate → 409 in progress |
| API-U-016 | U | Idempotency records purged after 24 h (TestClock) |
| API-U-017 | U | Paging binding: limit bounds, cursor validation, sort parsing errors |
| API-U-018 | U | Rate limits: anonymous and authenticated policies with Retry-After |
| API-U-019 | U | Heavy policy concurrency 2 + queue 4 → 7th request 429 |
| API-U-020 | U | Body size limit → 413 |
| API-U-021 | U | JSON depth 33 → 400 |
| API-U-022 | U | Jobs: 202 + Location; status transitions; cancel |
| API-U-023 | U | Jobs visible only to owner or admin |
| API-U-024 | U | System info anonymous and contains no secrets |
| API-U-025 | U | Session touch called once per request (throttling delegated to IDN) |
| API-U-026 | U | Forwarded headers ignored unless trusted proxies configured |
| API-U-027 | U | HSTS only in Production |
| API-U-028 | U | Compression for large JSON responses |
| API-U-029 | U | Operation ids unique and follow naming rule |
| API-U-030 | U | Every endpoint has RequirePermission or AllowAnonymous (reflection over endpoint metadata) |
| API-U-031 | U | Hub refuses unsupported protocol version |
| API-U-032 | U | Hub connection bound to session; 5th connection for same session refused |
| API-U-033 | U | RenewToken extends connection; expiry without renewal → Closing("tokenExpired") after grace (TestClock) |
| API-U-034 | U | Subscribe unknown topic → error result |
| API-U-035 | U | Invalid filter → field errors |
| API-U-036 | U | Subscription limit 50 |
| API-U-037 | U | Devices topic: ids snapshot then deltas in sequence order |
| API-U-038 | U | Devices topic: query filter resolved via REG with visible scope |
| API-U-039 | U | Devices topic: > 5,000 matches → `too_many_devices` |
| API-U-040 | U | UpdateSubscription changes the set: removed devices stop, added devices snapshot |
| API-U-041 | U | Resume within ring → Resumed=true, no snapshot, missed deltas delivered |
| API-U-042 | U | Resume outside ring → snapshot |
| API-U-043 | U | Coalescing merges deltas of same device (union of paths, latest values) |
| API-U-044 | U | Non-coalescable messages kept in order |
| API-U-045 | U | Overflow → Resync for coalescable subscriptions |
| API-U-046 | U | Non-coalescable overflow → Closing("overloaded") |
| API-U-047 | U | Message rate limit 500/s holds excess and coalesces |
| API-U-048 | U | Rights changed → Resync("rightsChanged") with re-resolved filter |
| API-U-049 | U | Session revoked → Closing("sessionRevoked") |
| API-U-050 | U | Shutdown → Closing("shutdown") to all |
| API-U-051 | U | Aggregates topic snapshot and deltas only for changed scopes |
| API-U-052 | U | Connections topic requires config permission |
| API-U-053 | U | Jobs topic pushes progress |
| API-U-054 | U | System topic notices delivered |
| API-U-055 | U | MessagePack DTO round-trip for every item type (schema snapshot test) |
| API-I-001 | I | Sign-in then GET devices with token through real host |
| API-I-002 | I | Device PATCH with ETag through real host |
| API-I-003 | I | Problem details for registry validation error with field paths |
| API-I-004 | I | Idempotency table persisted in PostgreSQL |
| API-I-005 | I | Discovery job through IRT returns 202 and completes |
| API-I-006 | I | Config endpoint refused for web client token |
| API-I-007 | I | Package upload 500 MB limit honoured (chunked test with 501 MB → 413) |
| API-I-008 | I | Rate limit with real middleware |
| API-I-009 | I | Token in hub query string not present in log files |
| API-I-010 | I | `/system/info`, `/system/ca` |
| API-I-011 | I | HTTPS with site certificate (SEC) |
| API-I-012 | I | Management port isolation (health/metrics) |
| API-I-013 | I | Concurrent clients (50) listing devices within latency target p95 < 100 ms |
| API-I-014 | I | Error codes have `errors.<code>` keys in en/fr/ar bundles (scan of registered codes vs UI bundles) |
| API-I-015 | I | OpenAPI document generated equals committed file |
| API-I-016 | I | Live: SignalR client (Microsoft.AspNetCore.SignalR.Client, MessagePack) subscribes to devices and receives simulator changes |
| API-I-017 | I | Live: WebSocket blocked → SSE fallback works |
| API-I-018 | I | Live: reconnect with ResumeFrom after 10 s network cut → missed deltas, no duplicates |
| API-I-019 | I | Live: node restart → client resubscribes; resume or snapshot correct |
| API-I-020 | I | Live: 50 connections × 2,000 devices with simulator stress → no overload closures; p95 field-to-client latency < 500 ms |
| API-I-021 | I | Live: slow client (reads paused) → Resync overflow, other clients unaffected |
| API-I-022 | I | Live: session revoked from admin API → client receives Closing then disconnect |
| API-I-023 | I | Live: token renewal over 30 minutes with 10-minute tokens |
| API-I-024 | I | Live: aggregates topic reflects simulator door forced in location counts |
| API-I-025 | I | Live: connections topic shows simulator stop/start transitions |

## 12. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.19 | §4–§7, §9, §10 (REST parts) | API-U-001…API-U-030, API-I-001…API-I-015, REG-I-008, REG-I-010 |
| T1.20 | §8, §9, §10 (live parts) | API-U-031…API-U-055, API-I-016…API-I-025 |
