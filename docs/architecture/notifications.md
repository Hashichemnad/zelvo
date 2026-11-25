# Notifications (task B5)

Covers the notification part of **module 10**, **LANG-6** (notifications in the recipient's language) and **GOV-8** (Arabic official correspondence) of `docs/requirements.md`. Product owner decisions of 2026-11-24:

1. **Events of B5:** requesters hear about their own incidents (received, waiting for them, resolved, closed, major-incident updates meant for requesters); staff hear about incidents assigned to them or arriving in their group's queue; SLA alerts recorded by B3 are delivered; publishers hear about knowledge articles waiting for review. Administrators switch each event on or off and change its wording. The no-code rule builder (C3) adds more.
2. **People's choices:** in-app notifications are always on. Each person can turn the **email** of an event off under Account → Notifications. **SLA alerts always send email.**
3. **Mail server:** one per installation, set by the operator (like Keycloak's). The sender name is the organisation's name in the email's language. A tenant's own sender domain with SPF, DKIM and DMARC comes with branding (F1).
4. **Arabic terms** of the formal correspondence guidelines approved: المتعامل, طلب, شكوى / استفسار / اقتراح, اعتماد (`docs/guidelines/arabic-formal-correspondence.md`).

Code: `src/api/Zelvo.Data/Notifications` (entities), `src/api/Zelvo.Notifications` (events, sources, interceptor, renderer, composer, SMTP sender, worker jobs, the default wording in `Texts/en.json` and `Texts/ar.json`), `src/api/Zelvo.Api/Notifications` (API, missing translations), `src/api/Zelvo.Api/Demo/DemoNotificationsSeeder.cs`, `src/web/src/components/notifications` and the pages `notifications`, `settings/notifications` and `settings/notifications/deliveries`.

## How a notification travels

```
change saved ──► NotificationInterceptor asks INotificationSources ──► notifications (Pending), same transaction
                                                                            │
worker NotificationDispatcher (every 2 s, per tenant, lease + SKIP LOCKED)  ▼
  render: recipient's language, calendar, digits; tenant template or default ──► Delivered (in-app list)
                                                                            │  email wanted?
                                                                            ▼
                                                        notification_deliveries (Pending) ──► SMTP, retries ──► Sent / Failed
```

1. **Queued with the change.** `NotificationInterceptor` runs before every save (like webhooks and search). Each `INotificationSource` reads the change tracker and returns who to notify and the placeholder values. The interceptor adds one `notifications` row per recipient in the same transaction, so a notification exists if and only if its change was saved. The same person gets one notification per event and subject in a save.
2. **Who and when** (sources in `NotificationSources.cs`). Nobody is told about their own action, except the confirmation that an incident was received.

   | Event | Recipients | Raised when |
   | --- | --- | --- |
   | `incident.received` | The requester | An incident is logged (also when staff log it on their behalf) |
   | `incident.awaiting-requester` | The requester | It is put on hold with the reason `awaiting-requester` |
   | `incident.resolved` | The requester | It is resolved (also when its parent resolves it) |
   | `incident.closed` | The requester | It is closed (also by the auto-close job) |
   | `incident.major-update` | Requesters of the major incident and of each child incident, each linked to their own incident | A timeline update with audience *Requesters* is posted |
   | `incident.assigned` | The new assignee | The assignee changes (on creation too), unless they assigned themselves |
   | `incident.queued` | Members of the group | An incident arrives in a group with nobody assigned (created, or moved to another group) |
   | `sla.threshold`, `sla.breached` | The recipients B3 recorded on the `sla_alerts` row | A threshold is reached; 100 % is the breach |
   | `knowledge.review-requested` | Active people with `knowledge.publish`, except the version's creator, last editor and submitter (four-eyes rule) | A version is submitted for review |
   | `incident.replied` (B4) | The requester | A staff member posts a public reply on their incident (from the workspace or by email; not for chat transcripts) |
   | `incident.mentioned` (B4) | The people mentioned | Someone @mentions them in a reply or internal note (links to the workspace) |
   | `incident.requester-frustrated` (E5) | The team leads of the incident's group, or the assignee when the group has none | The AI's sentiment reading of the requester turns to frustrated (product owner decision 2026-11-24; links to the workspace; see `ai-features.md`) |
   | `voice.received` (C6) | The requester | A complaint, inquiry or suggestion is sent (not for anonymous ones) |
   | `voice.replied` (C6) | The requester | Staff reply publicly on their complaint, inquiry or suggestion |
   | `complaint.resolved` (C6) | The requester | Their complaint is resolved; it asks them to confirm or reject the resolution |
   | `inquiry.answered` (C6) | The requester | Their inquiry is resolved with the answer |
   | `suggestion.reviewed` (C6) | The requester | Their suggestion's review changes, with the feedback |
   | `voice.assigned` (C6) | The new assignee | A complaint, inquiry or suggestion is assigned to them, unless they assigned themselves |
   | `voice.queued` (C6) | Members of the group | One arrives in a group with nobody assigned |

   The seven C6 events were approved by the product owner on 2026-11-24. Like every event, they are rendered in the recipient's language and administrators switch each on or off, with or without email, in Settings → Notifications.

3. **Rendered for the recipient** (`NotificationProcessor`, in the worker). The event must be on and the recipient active, otherwise the notification is *Suppressed*. The language is the recipient's own, then the tenant default, then English (LANG-2, LANG-6); calendar and digits likewise (LANG-8); dates are shown in the time zone of the tenant's default business calendar (`Asia/Dubai` without one). The wording is the tenant's template for that language, field by field, else Zelvo's default. The in-app title and text are stored in the recipient's language; later language changes do not rewrite old notifications, so the list marks them with their own language and direction.
4. **Email.** When the event sends email, the recipient has an address and (for events that allow it) has not turned it off, a `notification_deliveries` row holds the rendered subject, HTML and plain text. The dispatcher sends due emails (four at a time per tenant) and retries after 1 min, 5 min, 15 min, 1 h, 3 h, 6 h and 12 h (`Notifications:RetryDelays`); answers that will not improve (a refused address or message) fail at once. An SLA alert counts as delivered (`sla_alerts.delivered_at`) when its first notification is rendered.

Several workers can run: rows are claimed with a two-minute lease (`FOR UPDATE SKIP LOCKED`), and a stopped worker's work is picked up when the lease ends. A notification that cannot be rendered is tried five times, then suppressed and logged.

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `notifications` | Event, recipient, placeholder values (JSON, bilingual values keep both languages), link path, ticket / article / SLA alert, status (Pending, Delivered, Suppressed), language, title, text, read at | Queue and in-app list. Not audited (Zelvo writes them; the change that caused them is audited). Kept 90 days (`Notifications:InAppRetention`) |
| `notification_deliveries` | Address, language, subject, HTML and text, status (Pending, Sent, Failed), attempts, next attempt, last error, manual | Email queue and delivery log. Kept 30 days (`Notifications:DeliveryRetention`) |
| `notification_templates` | Bilingual subject, email body, in-app text and link label per event | Only events the tenant changed; audited; missing languages in Settings → Missing translations |
| `notification_preferences` | A person's email choice per event | Only events they changed; audited |

All four tables have forced row-level security; the guard test covers them. Events on or off and their email are a tenant setting, `notifications.events` (JSON; events not listed are on, with email). The worker finds tenants with work through the `SECURITY DEFINER` function `zelvo_notification_due_tenants()` (ids and slugs only), and `zelvo_purge_notifications(deliveries_before, notifications_before)` removes old rows hourly.

## Templates and wording (LANG-5, GOV-8)

- Zelvo's default wording lives in translation files, `src/api/Zelvo.Notifications/Texts/en.json` and `ar.json`: templates per event, the email layout, month names of both calendars, fixed terms (for example *response* / *الاستجابة*) and sample values for previews. Nothing user-facing is written in code; a test checks every event has wording in both languages using only its placeholders, and that the Arabic follows the guidelines (no exclamation marks, emojis or tatweel; Arabic commas; no dialect; "مع خالص التحية،").
- A template has four fields in each language: **subject** (also the in-app title), **in-app text**, **email body** (plain text; blank lines separate paragraphs) and the **link label**. A language left empty uses Zelvo's wording in that language and is listed as a missing translation.
- **Placeholders** are `{{name}}` with the names listed per event (`NotificationEvents`), plus `{{recipient.name}}` and `{{tenant.name}}` everywhere. There is no template logic. Saving refuses unknown names and stray `{{`/`}}`.
- **Formatting is the engine's job**, as the guidelines ask: bilingual values in the recipient's language; numbers and percentages in their digits (٧٥٪); dates in their calendar, naming it ("٢٤ نوفمبر ٢٠٢٦م، ١٠:٣٠ ص", "٣ جمادى الآخرة ١٤٤٨هـ", "24 November 2026, 10:30 AM"), with the 12-hour clock and ص / م. References such as `INC-000123` stay as written.
- **Email layout:** a letterhead with the organisation's name in the email's language and, when it has one, its name in the other language below (the logo comes with branding, F1); the body right to left with `lang="ar" dir="rtl"` for Arabic; every placeholder value HTML-encoded and isolated in `<bdi>` so Latin references keep their direction; a button and the plain link; the note that the email is automatic, a link to Account → Notifications and the **"Powered by Zelvo" / "بتقنية زيلفو"** footer, which templates cannot remove. Inline styles and table layout for mail apps; alignment follows the direction explicitly because mail apps ignore logical CSS properties (the web app's rule). Every email also has a plain-text part.
- **Headers:** `Auto-Submitted: auto-generated` (RFC 3834) and `X-Auto-Response-Suppress: All`, so out-of-office replies and email-to-ticket (E2) leave them alone; `Content-Language`; a stable `Message-ID`.

## API

All under `/api/v1`, documented in the API explorer.

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET /me/notifications?unreadOnly=` | Signed-in people | Own in-app notifications, newest first, paged |
| `GET /me/notifications/unread-count` | Signed-in people | Unread count (the header's bell checks it every 30 s) |
| `POST /me/notifications/{id}/read`, `POST /me/notifications/read-all` | Signed-in people | Mark read; others' notifications are 404 |
| `GET`, `PUT /me/notification-preferences` | Signed-in people | Events that can reach them (by what they may do) and their email choice; SLA alerts cannot be turned off (400) |
| `GET /notifications/settings` | `notifications.manage` | Every event with switches, placeholders, the tenant's and Zelvo's wording, whether a mail server is set |
| `PUT /notifications/settings/events/{event}` | `notifications.manage` | On or off, email or not (audited as a tenant setting) |
| `PUT`, `DELETE /notifications/templates/{event}` | `notifications.manage` | Save the tenant's wording or go back to Zelvo's (audited) |
| `POST /notifications/templates/{event}/preview` | `notifications.manage` | Render saved or unsaved wording with sample values in a language |
| `POST /notifications/test-email` | `notifications.manage` | Queue a test email to one's own address (audited) |
| `GET /notifications/deliveries`, `GET …/{id}`, `POST …/{id}/resend` | `notifications.manage` | Delivery log with masked addresses; opening one shows the address and content and is audited as a view of personal data; "Send again" (audited) |

`notifications.manage` belongs to tenant administrators. It is **not available to integrations** (API keys and OAuth clients), added with B5 for the product owner's approval: templates reach every person's mailbox, so a leaked key could turn them into convincing phishing.

## Screens

- **Header:** a "Notifications" link with the unread count for signed-in people; changes are announced politely to screen readers.
- **Notifications** (`/[locale]/notifications`): the list with unread marking, "Show unread only", "Mark as read" and "Mark all as read"; links only to paths inside the web app.
- **Account → Notifications:** email choice per event, grouped as *your incidents*, *your work* and *knowledge reviews*; events switched off by the organisation, or app-only, say so.
- **Settings → Notifications** (`notifications.manage`): a warning when the installation has no mail server; per event *On*, *Send email* and *Edit wording* with English and Arabic side by side (Arabic fields right to left), the placeholders, previews in either language (the email in a sandboxed `iframe`), "Use Zelvo's wording"; "Send a test email to me". **Email delivery log** tab: status filter, masked addresses, error and next attempt, "View" and "Send again".

All screens are in English and Arabic with logical CSS properties.

## Configuration (operator)

| Setting | Default | Meaning |
| --- | --- | --- |
| `Notifications:WebUrl` | `http://localhost:3000/` | Address of the web app in email links (custom domains per tenant: F1) |
| `Notifications:Smtp:Host` | empty | Mail server; empty means no email (in-app still works; emails fail with a clear error in the log) |
| `Notifications:Smtp:Port`, `Security` | 587, `Auto` | `Auto` (TLS on 465, else STARTTLS when offered), `StartTls`, `SslOnConnect`, `None` (local relays only) |
| `Notifications:Smtp:User`, `Password` | empty | From environment variables or secrets, never committed |
| `Notifications:Smtp:From` | `no-reply@zelvo.test` | Sender address |
| `Notifications:PollInterval`, `Concurrency`, `RetryDelays`, `DeliveryRetention`, `InAppRetention` | 2 s, 4, see above, 30 days, 90 days | Worker behaviour |

Compose sends to Mailpit (`http://localhost:8025`) without TLS; `ZELVO_SMTP_*` in `.env` points the API and the worker at another server.

## Demo

Seeder `b5-notifications` queues "received" for each sample incident's requester and "assigned" for each assignee, so every persona finds notifications; the worker writes them and emails them to Mailpit. When "Reset demo" runs in the API, saving the sample incidents already raised them and the seeder adds nothing.

## Email tickets (task E2)

Requester notifications of tickets that came in by email go out **from the ticket's mailbox** in the same email thread (`outbound_emails`, sent by the worker's mail job) instead of the installation's mail server, with the same wording in the requester's language; staff replies (`incident.replied`) go out with the reply's own `Message-ID` even when that event's email is switched off. See `channels.md`.

## WhatsApp, SMS and Teams (task E3)

`IEmailSender` is the email channel. Since E3, the same pipeline also queues **WhatsApp, SMS and Teams** messages when it renders a notification (`MessagingNotifications`, `channel_messages`, sent by the worker's messaging job): back through the WhatsApp, SMS or Teams conversation a requester's ticket came in by, and otherwise to the channels each person chose under Account → Notifications (WhatsApp and SMS to their profile's phone, opt-in; Teams once they used the tenant's Teams app). The text is the rendered subject, in-app text and link in the recipient's language, so the event's wording (one template per event) serves every channel; WhatsApp's Meta-approved templates carry it as their variable. Administrators switch WhatsApp, SMS and Teams per event (`messaging`, on by default, next to email). Approvals reach approvers in Teams as a card with Approve and Reject that decides there; WhatsApp and SMS carry the two links. See `messaging.md`. The bell follows live updates over SignalR since B4 (`workspace.md`): it hears when the person's notifications change and polls only as a fallback.

## Not in B5

- Notification rules from the no-code builder (C3); approvals by email (C3).
- Logo letterhead, a tenant's own sender domain and branded colours (F1); both calendars on official letters (F1 letterhead option).
- Digests and quiet hours (not in the requirements).
- Notifications for B4's public replies and mentions: added with B4 (`incident.replied`, `incident.mentioned`; see `workspace.md`).
- Notifications for B4's internal notes and mentions (added with B4; public replies came with E2).

## Branding (task F1)

The letterhead follows the tenant's published branding: brand colour for the heading, links and button, the light logo embedded as `cid:zelvo-logo`, and the product name beside "Powered by Zelvo". With a verified sender domain, emails go out from the tenant's own address, signed with its DKIM key. See `branding.md`.
