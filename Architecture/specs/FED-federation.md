# FED — Multi-site Federation

| | |
|---|---|
| Spec id | FED |
| Projects | `src/service/Watchpoint.Federation`, `schemas/proto/federation.proto`, UI `src/ui/packages/operation/src/federation/` (central console parts) and `src/ui/packages/configuration/src/federation/` (links) |
| Milestone | M9 |
| Tasks | T9.06 (§4–§7), T9.07 (§8–§10) |
| Event id range | 3600–3699 |
| Design docs | [Redundancy, scale-out and multi-site §4](../../docs/modules/redundancy-and-multi-site.md#4-multi-site-federation) |

## 1. Purpose and scope

Links between **autonomous sites**: a **central** site (head office, monitoring centre) receives
selected alarms, events, states, aggregates, health and a configuration summary from **child**
sites, and sends back alarm actions, commands, on-demand queries and video requests — under
the rights of the central user **and** of the child's **federation identity**. Links resume after
WAN outages from acknowledged sequences. Alarm handling ownership modes (local, central,
handover schedule, fallback). Multi-level federation (country → region → site).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, PIP (durable consumers on WP_LOG/WP_SYS for the outbound stream; records for inbound federated items), SEC (site CA, link certificates), IDN (federation identity user kind `Federation`), RGT (site scopes, constraints), ALM (ownership, actions), CMD, HIS (remote queries), REG (summary), VID (remote stream requests), HLT (link and child health), LIC (`opt.federation.central`, child allowed by edition), AUD, CLU (link endpoint ownership as a singleton per link), Grpc.AspNetCore | UIO (federated console, site map), UIF (links configuration), RPT (federated alarm stats later) |

## 3. Layout

```
Watchpoint.Federation/
├── Contracts/FederationLink.cs (direction, peer, filters, identity, ownership mode), FederatedSite.cs, IFederationService.cs, FedErrors.cs
├── Internal/
│   ├── Links/LinkManager.cs (singleton lease per link), LinkSession.cs (gRPC bidi stream), Pairing.cs
│   ├── Outbound/OutboundPublisher.cs (durable consumer → filtered messages, coalescing), Filters.cs, SummaryBuilder.cs
│   ├── Inbound/InboundIngestor.cs (federated alarms/events/states into local stores as "remote" records), RemoteRegistry.cs
│   ├── Actions/RemoteActionGateway.cs (child side: executes central requests with federation identity), RemoteCalls.cs (central side)
│   ├── Ownership/AlarmOwnershipService.cs (modes, handover schedules, fallback)
│   └── Storage/FederationDbContext.cs
├── FederationModule.cs, FederationLog.cs
```

## 4. Links and pairing (T9.06)

- A link has two ends: **child** (sends data, executes requests) and **central** (receives,
  sends requests). Either end may **initiate** the TCP connection (`initiator: child | central`)
  depending on firewalls; the gRPC roles are fixed (central = gRPC server
  `/watchpoint.federation.v1.Federation/Link` when the child initiates, and vice versa through a
  reverse-dial: the initiator always opens the stream, messages are symmetric envelopes).
- **Pairing**: on the central, `config.federation.edit` creates a link invitation (link id,
  central CA fingerprint, address list, one-time secret, valid 24 h). On the child, the admin
  imports the invitation, sees the central's name and fingerprint, chooses filters and the
  federation identity, confirms → child generates a key pair, sends CSR + secret → central issues a
  **link certificate** signed by the central site CA (SEC `IssueFromCsrAsync`, CN = link id) and
  the child pins the central CA; the child's own site CA certificate is given to the central for
  the reverse direction. mTLS, TLS 1.3.
- Link state machine: `Pending → Connecting → Active ⇄ Degraded (lagging) → Disconnected`,
  `Suspended` (admin), `Revoked`.

## 5. Protocol (`federation.proto`, T9.06)

```proto
service Federation { rpc Link(stream FedEnvelope) returns (stream FedEnvelope); }
message FedEnvelope {
  uint64 seq = 1;            // per direction, persisted
  uint64 ack = 2;            // highest seq received and durably stored from the peer
  oneof body {
    Hello hello = 10;        // link id, site id/name, versions, resume_from (last acked seq of peer stream)
    AlarmUpdate alarm = 11;  // full alarm DTO incl. lifecycle state and timeline entry
    EventItem event = 12;
    StateBatch states = 13;  // coalesced device states (filtered devices only)
    AggregateBatch aggregates = 14;
    HealthSummary health = 15;
    ConfigSummary summary = 16;   // locations tree, devices (id, name, type, location), types used — on start and on change (deltas)
    Request request = 20;    // central → child: alarmAction, command, query, videoStream, synopticFetch
    Response response = 21;
    Heartbeat heartbeat = 30;
  }
}
```

- **Outbound stream (child → central)**: `OutboundPublisher` is a durable JetStream consumer per
  link on WP_LOG + WP_SYS (filter subjects by kind) with its own cursor; records pass the
  **filters** (§6) and are converted to envelopes; states coalesced per device per 1 s; the
  consumer acks records only when the peer acks the envelope seq (mapping table seq → log
  sequence persisted every 5 s), so after any outage the stream **resumes** without loss, in
  order, with original timestamps. Retention needed on the child = JetStream log retention
  (default 7 days; link lag beyond retention → `fed.link.gap` event and a full resync of current
  states/alarms, history gap reported).
- **Inbound (central side)**: items stored as **remote records** in the central's own log under
  subjects `wp.fed.<linkId>.*` (a dedicated stream `WP_FED`, R3) and projected by FED-aware
  consumers: federated alarms into a `fed_alarms` store (with child site id), events into HIS
  with `site_id`, states into a `RemoteStateCache` (memory + snapshot). Central alarm console reads
  local + federated alarms.
- **Requests (central → child)**: each request carries the central user identity (id, display,
  site) and rights context; child executes with §7 rules and answers with `Response` (result or
  error code). Timeouts: actions/commands 30 s, queries 60 s; unacked requests after link loss
  are failed on the central (`fed.link.down`), never replayed automatically.
- Bandwidth: per-link limits (`maxStatesPerSecond`, `maxEventsPerSecond`; beyond → coalescing
  then sampling with counters), compression (gRPC gzip), heartbeat 10 s, lag measurement.

## 6. Filters (T9.06)

Configured on the **child** (data owner), shown read-only on the central:

| Filter | Options |
|---|---|
| Scope | Locations subtrees, categories, types, tags (same `GrantScope` model as RGT) |
| Alarms | Priority range, rules include/exclude, include acknowledged history (yes) |
| Events | Event paths include/exclude (e.g. only `accessEvents.denied`, `armable.*`), severity minimum |
| States | Devices in scope with selected properties only (`overall` always) |
| Health | Child cluster summary yes/no |
| Personal data | Strip personal fields (holder names) — default **on** |

## 7. Rights across sites (T9.06)

- Central side: users get **site scopes** in their grants (`GrantScope.Sites`, RGT §5) — a
  central user sees federated items only for sites (and locations within them, by remote location
  ids) covered by their grants, and may request actions requiring the corresponding permission
  (e.g. `alarms.acknowledge` scoped to site X).
- Child side: the link maps to a **federation identity** (IDN user kind `Federation`, created at
  pairing) with explicit grants (copyable with the rights-copy tool); every request is authorised
  for the federation identity **and** checked against the request's declared central permission
  (the central already checked its user) — both must allow (`fed.rights.denied` with which side
  denied).
- Audit on the child records actor `AuditActor(Kind=Federation, DisplayName="M. Fati @ HQ via
  federation link", Via=linkId)`; central audit records the request too.
- Personal data permission on the central is required to see holder names even if the child
  sends them (filter off).

## 8. Alarm ownership (T9.07)

| Mode | Behaviour |
|---|---|
| `local` | Child operators handle; central observes (actions refused unless `allowCentralActions`) |
| `central` | Central handles; child consoles show "handled by HQ" and can only comment |
| `schedule` | SCH calendar on the child: inside → local, outside → central (e.g. nights/weekends) |
| `fallback` | Local first; if not acknowledged within `fallbackAfter` (e.g. 2 min), ownership moves to central automatically |

- Ownership is part of the child's alarm state (ALM gains `HandlingSite` field with timeline
  entries `alarm.ownershipChanged { from, to, reason }`), so both consoles agree; the change is a
  child-side record → federated to the central.
- Central console sounds/pop-ups only for alarms it owns (and observed ones silently).

## 9. Central console (T9.07)

- **Federated alarm console**: UIO console with a **Site** column (site name, site local time),
  filter by site, ownership badge; actions routed to the child (inline status "sent to site A…",
  result). Alarm details fetched from the child on demand (procedure, timeline, linked video).
- **Site map**: MapLibre map of child sites (coordinates from site settings) with aggregated state
  per site (alarm/fault counts), link status, lag; drill-down → remote location tree (from the
  config summary) → device list (remote states) → remote device page (properties from
  `RemoteStateCache`, commands through requests) → child synoptics fetched on demand
  (`synopticFetch`, rendered read-only with remote states).
- **Site health**: per link: status, lag (seconds and records), throughput, child cluster health
  summary, licence state of the child.
- Video: `videoStream` request → child returns a stream ticket on its own gateway (direct WebRTC
  from the central client to the child gateway when routable) or **proxied** through the link's
  central node (configurable per link: `video: direct | proxied | off`; proxied uses a gRPC
  stream relay with bitrate cap).

## 10. Remote queries and multi-level (T9.07)

- Queries: history (events/alarms/commands of the child within filters), device details, alarm
  details, synoptic documents — paged, size-limited (≤ 5 MB per response), cached 60 s on the
  central.
- **Multi-level**: a site can be child of one central and central of others; outbound streams of
  a regional site can **forward** federated items from its children (`forward: true` per link,
  with the filters applied again), keeping the original site path (`sitePath: [country, region,
  site]`). Loops prevented by site id paths.

## 11. Data, API, settings, errors, observability

Tables `wp_federation`: `links` (id, role child|central, peer site id/name, addresses, cert
thumbprints, filters jsonb, identity_user_id, ownership jsonb, video mode, state, created_at),
`link_cursors` (link_id, direction, seq, log_sequence, acked_at), `fed_alarms` (central: child
site id, alarm DTO jsonb, updated_at), `remote_registry` (central: site id, summary jsonb, version).

| Method and path | Permission | Description |
|---|---|---|
| `GET/POST/PATCH/DELETE /api/v1/federation/links…` | `config.federation.view/edit` | Links (filters, ownership, video mode) |
| `POST /api/v1/federation/invitations` / `POST /api/v1/federation/links/accept` | `config.federation.edit` | Pairing |
| `POST /api/v1/federation/links/{id}/suspend|resume|revoke` | `config.federation.edit` | |
| `GET /api/v1/federation/sites` | `alarms.view` (site scope) | Child sites with state, lag, health |
| `GET /api/v1/federation/sites/{siteId}/tree` / `devices` / `devices/{id}` / `synoptics/{id}` | site-scoped view permissions | Remote browsing |
| `POST /api/v1/federation/sites/{siteId}/alarms/{alarmId}/acknowledge` (and other actions), `…/commands` | as local equivalents, site-scoped | Remote actions |
| `GET /api/v1/federation/sites/{siteId}/history/events?…` | `operate.events.view` site-scoped | Remote queries |

Live topics: `federatedAlarms`, `federationLinks`. Settings: `Federation:MaxRequestBytes` (5 MB),
`Federation:StateCoalesce` (1 s), `Federation:FallbackDefault` (2 min).

Errors: `fed.link.not_found`, `fed.link.down`, `fed.link.gap`, `fed.invitation.invalid`,
`fed.invitation.expired`, `fed.rights.denied` (args side), `fed.request.timeout`,
`fed.request.too_large`, `fed.ownership.not_owner`, `fed.licence.central_required`,
`fed.loop.detected`.

| EventId | Level | Message |
|---|---|---|
| 3600 | Information | Federation link {LinkId} to {Peer} active (resume from {Seq}) |
| 3601 | Warning | Federation link {LinkId} disconnected: {Reason} |
| 3602 | Warning | Federation link {LinkId} lag {LagSeconds} s ({LagRecords} records) |
| 3603 | Error | Federation link {LinkId} gap: log retention exceeded; full resync |
| 3610 | Information | Remote {Action} on {SiteId}/{Target} by {User}: {Outcome} |
| 3620 | Information | Alarm {AlarmId} ownership {From} → {To} ({Reason}) |

Metrics: `watchpoint.federation.lag.seconds` (gauge per link), `watchpoint.federation.messages`
(counter direction/kind), `watchpoint.federation.requests` (counter outcome).

## 12. Tests

| Id | Test |
|---|---|
| FED-U-001…006 | Pairing: invitation contents/expiry, fingerprint confirmation, CSR issuance, pinning, revocation |
| FED-U-007…010 | Link state machine and either-side initiation |
| FED-U-011…016 | Envelope seq/ack persistence; durable consumer acks only after peer ack; resume from cursor; gap detection and full resync |
| FED-U-017…022 | Filters: scope, alarm priorities/rules, event paths/severity, state properties, health, personal data stripping |
| FED-U-023…026 | Coalescing and bandwidth limits with sampling counters |
| FED-U-027…030 | Inbound projection: WP_FED stream, fed_alarms, HIS with site id, RemoteStateCache snapshot |
| FED-U-031…035 | Rights: central site scopes; child federation identity; both-sides check; denial side reported; audit actor format |
| FED-U-036…040 | Requests: timeouts, failure on link loss without replay, size limits, error mapping |
| FED-U-041…046 | Ownership modes local/central/schedule/fallback with FakeTime; timeline entries; console sound rules |
| FED-U-047…052 | Federated console: site column and local time, filters, inline remote action status, alarm details fetch |
| FED-U-053…057 | Site map aggregation, drill-down remote tree/devices/synoptics read-only |
| FED-U-058…061 | Video direct vs proxied vs off; bitrate cap |
| FED-U-062…065 | Remote queries paging, caching, size limits |
| FED-U-066…070 | Multi-level forwarding with site paths; loop detection; filters reapplied |
| FED-I-001 | Two sites (compose: two clusters) paired; alarms of child appear on central with lifecycle |
| FED-I-002 | WAN cut 30 min (Toxiproxy); child keeps working; resume delivers all events in order with original timestamps |
| FED-I-003 | Central acknowledges a child alarm; child timeline shows "via federation link" |
| FED-I-004 | Remote door command passes both rights checks; denied when child identity lacks the right |
| FED-I-005 | Handover schedule switches ownership at calendar edges |
| FED-I-006 | Child failover (CLU) during active link: link resumes from the new owner node |
| FED-I-007 | Proxied video from child gateway to central client |
| FED-I-008 | Three levels: site → region → country forwarding |

## 13. Tasks

| Task | Sections | Tests |
|---|---|---|
| T9.06 | §4–§7, §11 | FED-U-001…FED-U-040, FED-I-001…FED-I-004 |
| T9.07 | §8–§10 | FED-U-041…FED-U-070, FED-I-005…FED-I-008 |
