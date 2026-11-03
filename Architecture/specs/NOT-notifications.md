# NOT — Notifications

| | |
|---|---|
| Spec id | NOT |
| Projects | `src/service/Watchpoint.Notifications` |
| Milestone | M5 (push channel in M10 via PSH) |
| Tasks | T5.04 (§4–§8), T5.05 (§9–§11) |
| Event id range | 3100–3199 |
| Design docs | [Notifications](../../docs/modules/notifications.md) |

## 1. Purpose and scope

Deliver messages to people **outside the clients**: e-mail, SMS (HTTP providers or a GSM
modem), webhooks, and (M10) mobile push. Inputs: alarm actions and escalation steps (ALM),
maintenance mode (MNT), system events, scheduled reports (RPT), automations and scripts (M8),
and user **subscriptions**.

- Recipients (users, groups, external contacts, on-call rosters), routing, quiet hours,
  language and time zone per recipient.
- Templates per kind × channel × language with placeholders, preview.
- Durable outbox with retries, back-off, dead letters, delivery status (into the alarm
  timeline), quotas and flood summaries.

Not in scope: in-client notices (API live `Notice`), desktop OS notifications (DSK), mobile push
transport (PSH, M10 — NOT has the channel slot).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, STO (`wp_notifications`), SEC (SMTP/OAuth/provider secrets, webhook HMAC keys), IDN (users: e-mail, phone, language, time zone), RGT (user groups, subscription scope filter), SCH (calendars for quiet hours and rosters), PIP (`IPartitionDispatcher` for alarm timeline records, WP_SYS), FIL (attachments), CFG (object types), AUD, MailKit (MIT), System.IO.Ports (MIT), Polly (BSD-3) | ALM (`IAlarmNotificationHook`), MNT, RPT, AUT/SCR (M8), HLT (system notifications), PSH/MOB (M10), UIF (editors) |

## 3. Layout

```
Watchpoint.Notifications/
├── Contracts/
│   ├── NotificationRequest.cs, Recipient.cs, NotificationKind.cs, INotificationService.cs
│   ├── Channels/INotificationChannel.cs, ChannelMessage.cs, DeliveryResult.cs
│   ├── Templates/NotificationTemplate.cs, ITemplateRenderer.cs
│   ├── Contacts/ExternalContact.cs, Rosters/OnCallRoster.cs, Subscriptions/Subscription.cs
│   └── NotificationErrors.cs
├── Internal/
│   ├── Routing/NotificationRouter.cs, RecipientResolver.cs, QuietHours.cs, SubscriptionMatcher.cs
│   ├── Templates/TemplateRenderer.cs (placeholder engine), TemplateValidator.cs, DefaultTemplates/ (en, fr, ar)
│   ├── Outbox/OutboxStore.cs, DeliveryWorker.cs (per channel), RetryPolicy.cs, DeadLetters.cs
│   ├── Flood/FloodSummariser.cs, Quotas/QuotaLimiter.cs
│   ├── Channels/Email/SmtpChannel.cs (MailKit), OAuthTokenProvider.cs
│   ├── Channels/Webhook/WebhookChannel.cs
│   ├── Channels/Sms/SmsChannel.cs, Providers/HttpSmsProvider.cs (generic), TwilioProvider.cs, VonageProvider.cs, Modem/GsmModemProvider.cs, Modem/AtCommandSession.cs, Modem/PduEncoder.cs
│   ├── Adapters/AlarmNotificationHook.cs, MaintenanceNotifier.cs, SystemEventNotifier.cs
│   └── Storage/NotificationsDbContext.cs
├── NotificationsModule.cs
└── NotificationsLog.cs
```

## 4. Requests and routing (T5.04)

```csharp
public sealed record NotificationRequest(
    NotificationKind Kind,                         // alarm.raised, alarm.escalated, alarm.reminder, maintenance.started, report.ready, system.event, custom
    IReadOnlyList<RecipientRef> Recipients,        // may be empty when only subscriptions apply
    IReadOnlyList<ChannelKind>? Channels,          // null = each recipient's preferred channels for this kind
    string? TemplateKey,                           // null = default template for the kind
    JsonElement Model,                             // placeholder data (§6.2)
    NotificationPriority Priority,                 // Low | Normal | High | Critical (alarm priority bands map here)
    NotificationContext Context,                   // AlarmId?, DeviceId?, LocationId?, CorrelationId, Issuer (module/user/automation)
    IReadOnlyList<FileId>? Attachments = null,     // e-mail only
    string? DeduplicationKey = null);              // same key within 10 min → ignored

public abstract record RecipientRef
{
    public sealed record User(UserId Id) : RecipientRef;
    public sealed record Group(UserGroupId Id) : RecipientRef;
    public sealed record Contact(ContactId Id) : RecipientRef;
    public sealed record Roster(RosterId Id) : RecipientRef;
    public sealed record Address(ChannelKind Channel, string Value) : RecipientRef;   // ad-hoc (webhook URL id, e-mail) — permission-limited
}
public enum ChannelKind { Email, Sms, Webhook, Push /* M10 */ }

public interface INotificationService
{
    Task<Result<NotificationTicket>> SendAsync(NotificationRequest request, CancellationToken ct);   // returns after durable enqueue
    Task<Page<NotificationMessageDto>> QueryAsync(NotificationQuery query, PageRequest page, CancellationToken ct);
}
```

Router algorithm:

1. Deduplicate (`DeduplicationKey`, 10 min window).
2. **Maintenance policy** (MNT): `Suppress` → only kinds `maintenance.*` and `system.*` pass;
   `CriticalOnly` → alarm kinds pass only if priority Critical.
3. Expand recipients: groups → members; rosters → on-duty entries at `now` (§10); users →
   only enabled, non-template users; add **subscriptions** matching the request (§4.1).
4. **Visibility check** for alarm kinds: a user receives an alarm notification only if RGT
   `alarms.view` allows the alarm source and priority (same rule as the console — notifications
   never leak alarms a user could not see). External contacts and rosters' contacts have no
   rights check: they are explicitly configured by a configurator with `config.notifications.edit`.
5. Per recipient × channel: address available (e-mail/phone verified flags for users), quiet hours
   (§4.2), language and time zone; render template (§6) → `ChannelMessage`.
6. Flood/quota (§11) → enqueue in the outbox (one row per message) in a transaction; worker
   wakes.

### 4.1 Subscriptions

```csharp
public sealed record Subscription(SubscriptionId Id, UserId User, IReadOnlyList<NotificationKind> Kinds,
    SubscriptionFilter Filter /* min priority, locations subtree, categories, device types */,
    IReadOnlyList<ChannelKind> Channels, string? QuietCalendar /* SCH key; null = user default */, bool Enabled);
```

Users manage their own subscriptions (`operate.notifications.subscribe` — added to RGT
catalogue, family operation.alarms); configurators can manage others' with
`config.notifications.edit`. Filters are intersected with the user's rights at send time (§4
step 4).

### 4.2 Quiet hours

Per user: calendar key (SCH) during which only `Critical` priority is delivered (others are
**dropped**, or **delayed** to the end of the quiet period if the user chose "deliver later";
default drop for alarm kinds, delay for report kinds).

## 5. Contacts, user addresses

- `ExternalContact`: name, e-mail, phone (E.164), language, time zone, preferred channels, notes;
  CFG object type `notifications.contact`. Personal data (privacy, HIS §12 erasure applies).
- Users: e-mail and mobile phone in IDN profile with **verification** (code sent; unverified
  addresses not used except for verification itself); preferred channels per kind in user
  settings.

## 6. Templates (T5.04)

### 6.1 Model

```csharp
public sealed record NotificationTemplate(string Key, NotificationKind Kind, ChannelKind Channel, string Culture,
    string? Subject /* e-mail */, string Body, TemplateFormat Format /* Text | Html (e-mail) | Json (webhook) */, long Version);
```

Default templates for every kind × channel in en/fr/ar (shipped, overridable per site: CFG object
type `notifications.template`). Arabic templates RTL in HTML e-mail (`dir="rtl"`).

### 6.2 Placeholders

Mustache-like syntax implemented by `TemplateRenderer` (no code execution): `{{alarm.priorityLabel}}`,
`{{alarm.title}}`, `{{source.name}}`, `{{source.locationPath}}`, `{{alarm.raisedAt | time}}`
(recipient's time zone and culture, NodaTime patterns), `{{link.web}}` (deep link to the web
client alarm page), `{{link.mobile}}` (app link), `{{site.name}}`, sections `{{#alarm.procedure}}…{{/…}}`,
filters `upper`, `truncate:n`, `time`, `date`, `duration`. HTML bodies escape values by default;
JSON bodies JSON-escape. Unknown placeholders → validation error at template save; missing values
at render → empty string + warning counter. SMS bodies: GSM-7 vs UCS-2 length computed in the
editor (preview shows segment count).

Models per kind are documented JSON schemas (`schemas/notification-models/*.json`) used by
validation and the editor's completion.

### 6.3 Preview

`POST /api/v1/notification-templates/preview { template, kind, sample: "latest" | alarmId }` →
rendered subject/body per culture with real data.

## 7. Outbox and delivery (T5.04)

- `wp_notifications.messages` rows: id, request id, recipient (kind, id, address — personal data
  subject to retention and privacy erasure, §13), channel, rendered subject/body (≤ 64 KB), priority, status
  (`queued | sending | sent | delivered | failed | deadLetter | suppressed`), attempts,
  next_attempt_at, provider_message_id, error, created_at, updated_at, context (alarm id…).
- One `DeliveryWorker` per channel instance; priority order (Critical first), batch fetch with
  `FOR UPDATE SKIP LOCKED` (cluster-safe in M9).
- Retry policy: transient failures retried at 10 s, 30 s, 2 min, 10 min, 30 min, then dead letter
  (Critical: also after 3 failures fall back to the next channel of the recipient if any —
  e-mail → SMS); permanent failures (invalid address, 4xx from provider except 429) → failed
  immediately.
- Status tracking: provider delivery callbacks (Twilio/Vonage status webhooks via `IInboundEndpoints`-like
  node endpoint `/api/v1/notifications/callbacks/{provider}` with signature validation) update
  `delivered`.
- **Alarm timeline**: for alarm contexts, status transitions `sent`, `delivered`, `failed` are
  posted as records `alarm.notificationStatus { alarmId, recipientLabel, channel, status, error? }`
  into the alarm's partition through `IPartitionDispatcher` (ALM ignores them for lifecycle; HIS
  stores them in the alarm timeline).
- Dead letters visible in *System → Notifications* with retry/discard (`config.notifications.edit`).

### 7.1 Channels interface

```csharp
public interface INotificationChannel
{
    ChannelKind Kind { get; }
    string InstanceId { get; }                         // "email-default", "sms-twilio", "sms-modem-1"
    ValueTask<DeliveryResult> SendAsync(ChannelMessage message, CancellationToken ct);
    ValueTask<ChannelTestResult> TestAsync(string address, CancellationToken ct);
}
public sealed record DeliveryResult(DeliveryOutcome Outcome /* Sent | Transient | Permanent */, string? ProviderMessageId, string? Error);
```

Channel instances are configured as CFG object type `notifications.channel` (settings +
secrets, like connections).

## 8. E-mail and webhook channels (T5.04)

### 8.1 SMTP (MailKit)

Settings: host, port, security (`startTls` default, `sslOnConnect`, `none` only with
explicit flag), authentication (`none`, `password`, `oauth2` with provider `microsoft365` |
`google` | `generic` — client credentials or refresh token stored in SEC), from address and
display name, reply-to, max message size, TLS validation (system/custom CA/pin).
MIME: multipart/alternative (text + HTML) with inline logo; attachments from FIL (≤ 20 MB total);
`Message-ID` with site domain; headers `Auto-Submitted: auto-generated`, `X-Watchpoint-Alarm`.
Connection pooling: one SMTP session reused for up to 50 messages or 60 s.

### 8.2 Webhook

Settings per endpoint: URL (https, or http with explicit flag), method POST, headers, body
template (JSON), HMAC-SHA256 signature header `X-Watchpoint-Signature: t=<unix>,v1=<hex>` over
`t.body` with a per-endpoint secret, timeout 10 s, TLS options. 2xx = sent; 429/5xx/timeouts =
transient; other 4xx = permanent. Presets: Microsoft Teams (Adaptive Card JSON), Slack
(blocks), generic.

## 9. SMS channel (T5.05)

### 9.1 HTTP providers

`ISmsProvider` implementations: **generic HTTP** (configurable URL, method, form/JSON body
template with `{{to}}`, `{{text}}`, auth header/basic, success JSON path), **Twilio**
(Messages API), **Vonage** (SMS API). Each declares status callback parsing. Phone numbers E.164
validated (libphonenumber-csharp, Apache-2.0).

### 9.2 GSM modem

- Transports: serial (`System.IO.Ports`: port, baud, parity, flow control) or TCP (serial-over-IP
  gateways: host, port).
- `AtCommandSession`: init `AT`, `ATE0`, `AT+CMEE=2`, SIM PIN (`AT+CPIN`, PIN in SEC), network
  registration `AT+CREG?`, signal `AT+CSQ`, PDU mode `AT+CMGF=0`; send `AT+CMGS=<len>` with PDU
  (`PduEncoder`: GSM-7 packing, UCS-2 for non-GSM text such as Arabic, concatenated SMS with UDH
  for long messages, ≤ 6 segments); response timeouts 30 s; serialised one command at a time.
- Health: signal, registration, SIM status every 60 s → HLT health of the channel; failures →
  system event `notifications.modemFault`.
- Optional **inbound SMS acknowledgement** (disabled by default): messages `ACK <code>` from a
  phone number belonging to a user with `alarms.acknowledge` on that alarm acknowledge it (code =
  6-character per-message token included in the SMS); audited; replies "Acknowledged" /
  "Not allowed".

## 10. On-call rosters (T5.05)

```csharp
public sealed record OnCallRoster(RosterId Id, LocalizedName Name, string TimeZone,
    IReadOnlyList<RosterEntry> Entries, IReadOnlyList<RecipientRef> Fallback, long Version);
public sealed record RosterEntry(string? Calendar /* SCH key */, IReadOnlyList<WeeklyRange>? Ranges, DateOnly? From, DateOnly? To,
    IReadOnlyList<RecipientRef> Recipients /* users or contacts, ordered: escalation order */, int Order);
```

- On duty at instant t = entries whose calendar/ranges/dates include t (union); if none →
  `Fallback`. Overrides (one-off swaps) are entries with dates and lower `Order` value.
- **Escalation within a roster** (optional per request, used by ALM escalation steps): notify the
  first recipient; if the alarm is not acknowledged after `step` (default 5 min), the next, then
  fallback — implemented as scheduled follow-up requests cancelled on acknowledgement (NOT
  listens to `alarm.acknowledged` records on WP_LOG via a lightweight subscriber, Order 450).
- API `GET /api/v1/rosters/{id}/on-duty?at=` for UI.

## 11. Flood summariser and quotas (T5.05)

- **Flood**: per (recipient, channel), if more than `Notifications:Flood:Threshold` (default 5)
  alarm notifications within `Window` (2 min), subsequent ones are buffered for the rest of the
  window and sent as one **summary** message ("12 new alarms in Building A: 3 High, 9 Medium …"
  with up to 5 titles) using template kind `alarm.summary`. Critical alarms are always sent
  individually (but count toward the threshold).
- **Quotas** per channel instance: per hour and per day (default SMS 100/h, 500/day; e-mail
  2,000/h); per recipient per hour (default SMS 20). Over quota → message `suppressed` with
  reason, system event `notifications.quotaReached` (once per hour), Critical messages may exceed
  per-recipient quota but not the channel daily quota.

## 12. Data, API

Tables (`wp_notifications`): `messages`, `requests` (id, kind, context, created_at),
`channels`, `templates`, `contacts`, `rosters`, `subscriptions`, `flood_windows` (transient),
`quota_counters` (channel, window start, count), `inbound_sms`.

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/notifications/messages?status=&channel=&alarmId=&from=&to=` | `config.notifications.view` | Outbox / history |
| `POST /api/v1/notifications/messages/{id}/retry`, `DELETE …` | `config.notifications.edit` | Dead letters |
| `POST /api/v1/notifications/test` `{ channelInstance, address }` | `config.notifications.edit` | Test message |
| `GET/POST/PATCH/DELETE /api/v1/notification-channels…`, `…/templates…`, `…/contacts…`, `/api/v1/rosters…` | `config.notifications.view/edit` | CFG-backed CRUD (quick edit or drafts) |
| `POST /api/v1/notification-templates/preview` | `config.notifications.edit` | §6.3 |
| `GET/POST/PATCH/DELETE /api/v1/me/subscriptions…` | `operate.notifications.subscribe` | Own subscriptions |
| `POST /api/v1/me/contact-verification` `{ channel }` / `…/confirm { code }` | authenticated | Verify own e-mail/phone |
| `POST /api/v1/notifications/callbacks/{provider}` | provider signature | Delivery status callbacks |

## 13. Settings, errors, security

| Key | Default |
|---|---|
| `Notifications:Flood:Threshold` / `Window` | 5 / 2 min |
| `Notifications:Retry:Delays` | 10 s, 30 s, 2 min, 10 min, 30 min |
| `Notifications:Retention` | 90 days (messages), then purged |
| `Notifications:Dedup:Window` | 10 min |

Errors: `notifications.channel.not_found`, `notifications.channel.unavailable`,
`notifications.template.invalid` (args placeholder, line), `notifications.recipient.no_address`,
`notifications.address.unverified`, `notifications.address.invalid`,
`notifications.quota.exceeded`, `notifications.callback.invalid_signature`,
`notifications.modem.not_ready`.

Security: message bodies never contain secrets or signed file URLs; personal data (addresses,
bodies) purged after retention and included in privacy erasure (HIS §12 hooks); webhook targets
restricted to configured endpoints (no ad-hoc URLs from scripts in M8 unless allow-listed);
inbound SMS ack off by default and limited to verified phones.

## 14. Observability

| EventId | Level | Message |
|---|---|---|
| 3100 | Debug | Notification request {RequestId} kind {Kind}: {Messages} messages |
| 3110 | Information | Message {MessageId} sent via {Channel} |
| 3111 | Warning | Message {MessageId} via {Channel} failed ({Outcome}): {Error}; attempt {Attempt} |
| 3112 | Error | Message {MessageId} dead-lettered |
| 3120 | Warning | Quota reached for {Channel}: {Quota} |
| 3121 | Information | Flood summary sent to {Recipient}: {Count} alarms |
| 3130 | Warning | GSM modem {Instance}: {Status} (signal {Signal}) |

Metrics: `watchpoint.notifications.messages` (counter: channel, outcome),
`watchpoint.notifications.queue.depth` (gauge per channel), `watchpoint.notifications.latency`
(histogram request → sent).

## 15. Tests

| Id | Test |
|---|---|
| NOT-U-001 | Request validation; dedup window |
| NOT-U-002 | Recipient expansion: users, groups, contacts, ad-hoc addresses (permission) |
| NOT-U-003 | Disabled/template users excluded |
| NOT-U-004 | Alarm visibility check per user (scope and priority range) |
| NOT-U-005 | Subscriptions matching (kinds, priority, locations, categories, types) intersected with rights |
| NOT-U-006 | Quiet hours: drop vs delay; Critical always delivered |
| NOT-U-007 | Maintenance policy Suppress / CriticalOnly |
| NOT-U-008 | Language and time zone per recipient in rendering |
| NOT-U-009 | Unverified addresses skipped (except verification) |
| NOT-U-010 | Template renderer: placeholders, sections, filters, escaping HTML/JSON |
| NOT-U-011 | Template validation: unknown placeholders with line numbers |
| NOT-U-012 | Default templates exist for all kind × channel × culture; Arabic HTML RTL |
| NOT-U-013 | SMS segment computation GSM-7/UCS-2 |
| NOT-U-014 | Preview with latest alarm data |
| NOT-U-015 | Outbox enqueue transactional with request |
| NOT-U-016 | Worker priority order and SKIP LOCKED batching |
| NOT-U-017 | Retry delays; permanent vs transient classification |
| NOT-U-018 | Dead letter after last retry; retry/discard API |
| NOT-U-019 | Critical fallback to next channel |
| NOT-U-020 | Alarm timeline status records posted to the alarm partition |
| NOT-U-021 | Provider callbacks update delivered status (signature validated) |
| NOT-U-022 | SMTP: MIME structure, headers, attachments limit, inline logo |
| NOT-U-023 | SMTP security modes and TLS validation options |
| NOT-U-024 | SMTP OAuth2 token acquisition and refresh (fake token endpoint) |
| NOT-U-025 | SMTP session reuse limits |
| NOT-U-026 | Webhook signature format and verification sample |
| NOT-U-027 | Webhook status classification |
| NOT-U-028 | Teams/Slack presets render valid JSON |
| NOT-U-029 | Channel instances as CFG object types (validation, secrets) |
| NOT-U-030 | ALM hook: Notify action → request with alarm model |
| NOT-U-031 | ALM escalation step → notify groups |
| NOT-U-032 | MNT notifier: start/reminders/end messages to notify group |
| NOT-U-033 | System event notifier rules (health, quota, modem) |
| NOT-U-034 | Contacts CRUD and privacy flags |
| NOT-U-035 | Contact verification flow (codes, expiry, attempts) |
| NOT-U-036 | Messages query filters and permission |
| NOT-U-037 | Retention purge |
| NOT-U-038 | Deep links use configured public web URL |
| NOT-U-039 | Translations coverage of templates and errors |
| NOT-U-040 | Test endpoint per channel |
| NOT-U-041 | Generic HTTP SMS provider: templates, auth, success path |
| NOT-U-042 | Twilio request format and status callback parsing |
| NOT-U-043 | Vonage request format and status callback parsing |
| NOT-U-044 | E.164 validation |
| NOT-U-045 | GSM PDU encoding GSM-7 (spec examples) |
| NOT-U-046 | GSM PDU encoding UCS-2 (Arabic) and concatenated UDH |
| NOT-U-047 | AT session: init sequence, PIN, registration, errors (+CMS/+CME) |
| NOT-U-048 | AT session timeouts and serialisation |
| NOT-U-049 | Modem health reporting |
| NOT-U-050 | Modem over TCP transport |
| NOT-U-051 | Inbound SMS ack: valid code and authorised phone → acknowledge; others refused; audited |
| NOT-U-052 | Inbound SMS ack disabled by default |
| NOT-U-053 | Roster on-duty: calendars, ranges, dates, overrides by order, fallback |
| NOT-U-054 | Roster escalation sequence; cancelled on acknowledgement |
| NOT-U-055 | Roster on-duty API |
| NOT-U-056 | Flood threshold and summary content |
| NOT-U-057 | Critical alarms bypass summarising |
| NOT-U-058 | Quotas per channel per hour/day |
| NOT-U-059 | Per-recipient quota; Critical exceeds recipient quota only |
| NOT-U-060 | Quota reached system event once per hour |
| NOT-U-061…070 | Subscriptions API for self and for others, permission checks, filters UI contract (JSON schema), time-zone edge cases for quiet hours (DST), delayed delivery at end of quiet period |
| NOT-I-001 | SMTP to a MailHog-compatible test server (Mailpit container, MIT): alarm e-mail content, attachments, RTL |
| NOT-I-002 | Webhook to a test receiver: signature verified, retries on 503 |
| NOT-I-003 | Node restart with queued messages: delivered once after restart |
| NOT-I-004 | Simulator alarm → e-mail + timeline status records in alarm history |
| NOT-I-005 | Two concurrent workers on the same channel (single node, simulating M9) never send a message twice |
| NOT-I-006 | Load: 1,000 alarms in 1 min to 20 recipients → flood summaries, quotas respected, no crash |

## 16. Tasks

| Task | Sections | Tests |
|---|---|---|
| T5.04 | §4–§8, §12–§14 | NOT-U-001…NOT-U-040, NOT-I-001…NOT-I-006 |
| T5.05 | §9–§11 | NOT-U-041…NOT-U-070 |
