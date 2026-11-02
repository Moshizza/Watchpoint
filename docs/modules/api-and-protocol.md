# API and Live Protocol

> One versioned API for everything outside the node: desktop client, web client, mobile
> app, remote integration hosts, federation links and customer integrations.

## 1. Two channels

| Channel | Technology | Used for |
|---|---|---|
| **REST API** | HTTPS, JSON, OpenAPI 3.1 contract, under `/api/v1` | Queries (lists, details, history), configuration, commands (create), alarm actions, files |
| **Live stream** | SignalR over WebSocket (MessagePack), one connection per client | Pushes: state changes, events, alarms, command progress, configuration changes, notifications |

The client always connects **outbound** to the node. No client opens a listening port; no
firewall rule or URL registration is needed on workstations.

## 2. REST conventions

- Resources: `/servers`, `/devices`, `/device-types`, `/locations`, `/tags`, `/alarms`,
  `/events`, `/commands`, `/synoptics`, `/automations`, `/scripts`, `/users`, `/user-groups`,
  `/rights`, `/connections`, `/packages`, `/history/...`, `/reports`, `/system/...`.
- Filtering, sorting and cursor-based paging on all lists; field selection to keep payloads
  small.
- Optimistic concurrency with `ETag`/`If-Match` on updates.
- Idempotency keys on command creation and other non-idempotent POSTs.
- Errors as RFC 9457 *problem details* with a stable error code, translation key and
  parameters (the client renders the message in the user's language).
- Every endpoint declares its required permission; authorisation is enforced per object
  (scopes), not just per endpoint.
- Configuration endpoints are only accepted from **desktop client sessions** of users with
  configuration permissions (client kind is part of the session, bound to the client's
  refresh token and, when enrolled, the workstation certificate). The web and mobile
  clients cannot reach configuration even with a valid user token.

## 3. Live stream protocol

### 3.1 Subscriptions

The client declares what it wants; the server filters with the user's rights:

| Subscription | Content |
|---|---|
| `alarms` (filter: priority range, locations, states) | Alarm snapshots and changes |
| `events` (filter) | Event journal feed |
| `devices` (explicit list of ids, or a scope) | Property and overall-state changes — clients subscribe to **what is on screen** (visible tree nodes, synoptic viewport, open device pages) |
| `aggregates` (locations, tags, servers) | Counters per state |
| `commands` (mine / all) | Command lifecycle |
| `config` | Changes of configuration objects the client has loaded |
| `system` | Health, maintenance mode, licence banners, session messages |

### 3.2 Ordering, resume and resync

- Each subscription stream has a **sequence number**. Messages carry it.
- On reconnect, the client sends its last sequence per subscription. The server replays
  what was missed (from the event log) if within the retention window; otherwise it sends a
  **resync** (fresh snapshot of the subscription's scope) followed by live changes.
- Every subscription starts with a **snapshot** then deltas, so a client never needs to load
  the whole database.

### 3.3 Flow control

- State updates are **coalesced** per device for slow clients (only the latest values are
  sent); alarms, events and command results are never coalesced.
- Server-side limits per session (subscriptions, devices per subscription, messages per
  second) protect the node; clients adapt (e.g. the synoptic viewer subscribes per
  viewport).

### 3.4 Versioning

- REST: major version in the path (`/api/v1`); minor changes are additive only.
- Live stream: protocol version negotiated at connection; message schemas are versioned
  and additive.
- Clients and nodes of different minor versions must interoperate (rolling upgrades of
  clusters and of client fleets).
- TypeScript client libraries are **generated** from the OpenAPI contract and the live
  message schemas, shared by desktop, web and mobile.

## 4. Other machine-to-machine interfaces

| Interface | Purpose |
|---|---|
| gRPC (mTLS) | Remote integration hosts ↔ cluster; federation links between sites; node ↔ node control |
| Webhooks (outbound) | Notify external systems of events/alarms (signed with HMAC, retries) |
| Webhooks (inbound) | Authenticated endpoints that trigger automations/scripts or feed connector events |
| Public REST for third parties | Same REST API with API keys / OAuth client credentials for customer integrations (ticketing, BI), with read-only scopes by default |

## 5. Security of the API

- TLS 1.2+ only (1.3 preferred); HSTS for the web client.
- Access tokens are short-lived JWTs signed by the node (keys rotated, shared in the
  cluster); refresh tokens rotated and bound to the client instance.
- Rate limiting on sign-in, per session and per API key.
- CORS: only the node's own web client origin.
- Request audit for all mutating calls.
