# Notifications

Notifications reach people outside the Watchpoint clients, or alert client users when the
client is in the background.

## 1. Channels

| Channel | Implementation | Offline sites |
|---|---|---|
| E-mail | SMTP via MailKit (TLS, authentication, OAuth for Microsoft 365/Google) | Works with an on-premise mail relay |
| SMS | Provider plug-ins over HTTP (Twilio, Vonage, local operators) **or** a GSM modem/gateway connected to a node (AT commands over serial/USB or IP) | GSM modem works without internet |
| Mobile push | Through the **Watchpoint push relay** to Apple (APNs) and Google (FCM) | Requires internet; the payload contains no sensitive data ("New Critical alarm — open the app"); the app then fetches details from the site |
| Mobile in-app | Live stream of the mobile app when it can reach the site (Wi-Fi/VPN) | Works on site networks |
| Desktop OS notifications | Electron notifications when the client is minimised | Yes |
| Webhooks | HTTP POST (JSON, HMAC-signed) to customer systems (ticketing, Teams/Slack incoming webhooks) | On local networks |
| Voice call (later) | SIP call with text-to-speech message and DTMF acknowledgement | Through on-site SIP server |

> **Push relay**: Apple and Google only accept push messages from the app publisher's
> credentials. Watchpoint (the company) therefore operates a small push relay service.
> Sites that allow outbound internet register with it; sites without internet use in-app
> live notifications, SMS by GSM modem and e-mail via local relay instead.

## 2. Recipients and routing

- Recipients: users, user groups, external contacts (not Watchpoint users), on-call
  rosters (calendars saying who is on duty).
- **Subscriptions**: users can subscribe themselves to notification kinds within their
  rights (e.g. "Critical alarms of Building A, by push, 24/7").
- **Rules**: from alarm actions, escalation steps, automations and scripts.
- Quiet hours per user (except Critical), language per recipient, time zone per recipient.

## 3. Templates

- Per channel and language, with placeholders (alarm priority, source name, location path,
  time in recipient's time zone, link to open the alarm in the web or mobile client).
- Preview with real data in the editor; validation of placeholders.

## 4. Reliability

- Queue per channel with retries, back-off, and a dead-letter list visible in *System →
  Notifications*.
- Quotas per channel (e.g. SMS per hour) to avoid cost explosions; alarm floods are
  summarised ("12 new alarms in Building A") instead of sending 12 messages.
- Delivery status recorded (sent, delivered when the provider reports it, failed) in the
  alarm timeline.
- Acknowledgement from notifications: the mobile app can acknowledge; e-mail/SMS include a
  link to the web client.
