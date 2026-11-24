# Email and live chat (task E2)

Covers the email and live chat parts of **module 6** (omnichannel intake) of `docs/requirements.md`: email-to-ticket with threading and several mailboxes per tenant (Exchange / Microsoft 365, Gmail, IMAP/SMTP), and a live chat widget for the portal and external websites. WhatsApp, Teams and SMS come with E3; the AI virtual agent and its handover to a person with E5.

Product owner decisions of 2026-11-24:

1. **A shared conversation on tickets.** E2 was to add the public conversation; B4 was merged first with the ticket's conversation in `ticket_comments` (public replies and internal notes, never changed, the first staff public reply as the SLA response), so **email and chat use B4's conversation**: E2 adds where a comment came from (web, email, chat), its email `Message-ID` and threading headers, and its files. There is one conversation view, B4's (workspace and the requester's incident page).
2. **Mailbox connections:** Microsoft 365 through Microsoft Graph (app registration with a client secret), Gmail through the Gmail API (Google Workspace service account with domain-wide delegation), IMAP + SMTP with a password (Exchange on-premises and other servers), and an **administrator's sign-in** to Microsoft or Google (OAuth authorization code). Zelvo **polls** every mailbox (30 seconds); no push, because many installations are offline.
3. **Senders without an account:** a setting per mailbox: *anyone* (default; a requester account is made), *allowed email domains*, or *known users only*. Rejected emails get **no automatic reply** and are listed in the mailbox log.
4. **A reply to a closed ticket** becomes a **follow-up** incident linked to the closed one (as in B1).
5. **Chats become tickets when the agent decides** ("Make a ticket", or add the transcript to an open incident of the same person); a message left while nobody is online always becomes a ticket.
6. **Visitors give their name and email** before chatting; signed-in people do not. A ticket made from a chat belongs to that email (account made as for email). The visitor only ever sees their own chat.
7. **Chats wait in the widget's group;** available members who may answer chats accept them, up to the group's limit of open chats each (default 3), and can pass them to a colleague or another group. With nobody online, the widget offers "leave a message".
8. **Widget protection:** each widget lists the websites that may embed it; the chat runs in a frame from Zelvo's address; rate limits per visitor address; no captcha.

Code: `src/api/Zelvo.Data/Channels` (entities), `src/api/Zelvo.Channels` (email parsing, mailbox clients, OAuth tokens, intake, worker jobs), `src/api/Zelvo.Tickets/IncidentIntake.cs` (incidents from channels, contact accounts) and `MessageIds.cs`, `src/api/Zelvo.Api/Channels` (mailbox and chat API), `src/api/Zelvo.Api/Workspace` (B4's comments and live updates, which E2 extends), `src/api/Zelvo.Notifications` (emails from the ticket's mailbox), `src/web/src/components/channels` and the pages `chats`, `settings/mailboxes`, `settings/chat`, `chat-widget/[locale]/[widgetId]`, `chat-widget.js`, `auth/mailbox-callback`.

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `mailboxes` | Name, address (unique per tenant), provider, sign-in method, IMAP/SMTP servers, ports and TLS, user name, app client and directory ids, the credential **encrypted** (`Zelvo.Secrets`, purpose per mailbox), a pending sign-in (encrypted), who may raise tickets, allowed domains, category or group of new tickets, on/off, read status (last read, error, failures, next read, lease) | Audited (credential masked); read status written without the change tracker so polling does not fill the audit log |
| `inbound_emails` | The mailbox log: provider id (read once), `Message-ID`, sender, subject, outcome (Created, Added, FollowUp, Rejected, Ignored, Failed), reason, attempts, ticket and comment | Not audited (the ticket and comment are). Kept 90 days |
| `outbound_emails` | Emails to requesters of email tickets, from the ticket's mailbox: staff replies and the requester's notifications, rendered, with `Message-ID`, `In-Reply-To`, `References`, status and retries | Not audited. Kept 30 days |
| `chat_widgets` | Bilingual name and greeting, group, category of tickets, allowed websites (origins), shown on the portal, on/off | Audited; names in the missing-translation report |
| `chat_sessions` | Widget, group, visitor (account, name, email), **SHA-256 of the visitor's token**, language, website, state (Waiting, Active, Ended), agent, ticket, times, who ended it | Audited (email and token hash masked); `xmin` so only one agent accepts |
| `chat_messages` | Sender (Visitor, Agent, System), author, text, system event (accepted, transferred, ended, ticket), a global sequence for "messages after" | Audited with the text's length only |

Also on B4's `ticket_comments`: `source` (Web, Email, Chat; email and chat comments are always public, a check constraint), `mailbox_id`, `email_message_id` (indexed per tenant), `email_in_reply_to` and `chat_session_id` (plain ids without foreign keys, as comments are never changed); whether a staff reply's email went out is read from `outbound_emails` (staff only: Pending, Sent, Failed). And `tickets.mailbox_id` (the mailbox replies go out from), `ticket_attachments.comment_id` (files of an email, only with a ticket), `inbound_emails.comment_id`, `outbound_emails.comment_id`, `notifications.comment_id`, `support_groups.chat_limit` (1–20, default 3). All six new tables have forced row-level security; the guard test covers them. The worker finds tenants with work through `SECURITY DEFINER` functions that return only ids and slugs: `zelvo_mail_due_tenants()`, `zelvo_idle_chat_tenants(before)`, and removes old log rows with `zelvo_purge_mail_logs(inbound_before, outbound_before)`.

## Email

```
worker MailJob (every 5 s, per tenant, mailbox lease + SKIP LOCKED)
  mailbox due ──► IMAP / Graph / Gmail: unread emails of the inbox ──► EmailIntake (one scope per email) ──► mark read
                                                                         │
                         ignored / rejected / new incident / comment in the conversation / follow-up ──► inbound_emails
notification of a requester whose ticket has a mailbox ──► outbound_emails ──► MailJob sends from the mailbox, threaded
```

**Reading.** Each active mailbox is read every `Channels:PollInterval` (30 s; failing mailboxes back off up to 30 minutes and show the error). Zelvo takes the inbox's **unread** emails (IMAP `UNSEEN`, Graph `isRead eq false`, Gmail `is:unread in:inbox`), at most 25 per read, processes each and marks it read; an email is processed once (`inbound_emails` is unique per mailbox and provider id). An email that fails five times is marked read and stays in the log as failed. Emails over 40 MB are rejected unread.

**What becomes of an email** (`EmailIntake`), in order:

1. **Ignored:** automatic mail (RFC 3834 `Auto-Submitted` other than `no`, `X-Autoreply`, `X-Auto-Response-Suppress: All`/`AutoReply`, `Precedence: bulk/junk/list`, `List-Id`, delivery reports, `mailer-daemon`); Zelvo's own emails and mail from one of the tenant's mailboxes (loops); the same `Message-ID` already received through another mailbox.
2. **Rejected:** no single sender; `Authentication-Results` of the receiving server says `dmarc=fail` (a sender can add such a header only to its own harm; Zelvo never trusts a "pass"); a disabled account; a sender the mailbox does not accept; no subject and no text.
3. **Threading:** the ticket is the one named by Zelvo's own `Message-ID`s in `In-Reply-To`/`References` (`zelvo.t{ticket}.{random}@{mailbox domain}`, so threads survive log retention), else by earlier emails of the thread (`ticket_comments.email_message_id`, `inbound_emails`, `outbound_emails`), else by the number in brackets in the subject (`[INC-000123]`). **Only the requester and staff** (roles with `incidents.edit`) are added to a ticket; anyone else's email becomes a ticket of its own, so nobody can write into someone else's ticket by guessing its number.
4. **Added:** the new text (quoted earlier emails removed: "On … wrote:", the Arabic "كتب", Outlook's From/Sent block, "-----Original Message-----", trailing `>` quotes) becomes a public comment (source Email, with its `Message-ID` and `In-Reply-To`); staff writing by email count as staff (the first one is the SLA response); a requester's answer moves a ticket on hold *waiting for the requester* back to In progress (B3 clocks resume).
5. **Follow-up:** a reply to a closed incident becomes a new incident with `followUpOfId`.
6. **Created:** a new incident (channel Email) for the sender — a requester account is made when the mailbox accepts anyone or the sender's domain — with the mailbox's category (then the assignment rules route it) or group. Title from the subject without reply prefixes and the number, description from the text.

Attachments follow the tenant's attachment settings (size, types, content check) and are scanned by ClamAV like any upload (B1); files that are not accepted are left out and counted in the log ("files-skipped"). Inline images of signatures are left out; attached emails are kept as `.eml`. HTML-only emails are turned into text; nothing of an email is ever shown as HTML.

**Answering.** Staff reply in B4's conversation (`POST /incidents/{id}/comments`, public); the first public reply counts as the **response** for SLAs (B4). A public reply on a ticket with a mailbox gets its own `Message-ID` and `In-Reply-To` the thread's first email, and goes out by email. A requester's comment from the portal also moves a ticket on hold *waiting for the requester* back to In progress, as an email answer does (added by E2). The requester's notifications (B5 events) are rendered as usual in the requester's language, but for tickets that came in by email they **go out from the ticket's mailbox** instead of the installation's mail server (`outbound_emails`), with the number in brackets in the subject, `In-Reply-To` the last email of the thread and `References` to its earlier ones, so the requester's mail app keeps one thread. Staff replies go out as B4's `incident.replied` notification, from the mailbox, with the reply's own `Message-ID`; they go out even when that notification's email is switched off for the requester (they are the conversation itself). Notifications carry `Auto-Submitted: auto-generated`; staff replies, written by a person, do not. Both carry `X-Auto-Response-Suppress: All`. Sending retries after 1 min, 5 min, 15 min, 1 h, 3 h, 6 h and 12 h; refused recipients fail at once; "Send again" in the mailbox's outgoing log. A turned-off mailbox keeps its emails waiting.

**No new notification events:** E2 uses B4's `incident.replied` (the requester, when staff reply publicly) and `incident.mentioned`; the events `incident.reply` and `incident.requester-replied` planned earlier were dropped with the move to B4's conversation. Chat transcripts copied into a ticket raise no notification: both sides saw them live.

**People who wrote before they ever signed in** get an account made for their address (`users.subject` starts with `contact:`; no Keycloak account). Their first sign-in with that address **verified** (`email_verified` in the token, as with an email code) takes the account over, with its tickets; an unverified token gets an account of its own and none of them.

### Providers

| Provider | Sign-in | Reads | Sends |
| --- | --- | --- | --- |
| IMAP + SMTP (MailKit) | User name and password (or app password) | `UNSEEN` in INBOX, ids `UIDVALIDITY:UID`, `\Seen` flag | SMTP of the mailbox |
| Microsoft 365 (Graph) | App with client secret: client credentials for `https://graph.microsoft.com/.default`, directory id required; limit the app to the mailbox with an application access policy | `/users/{address}/mailFolders/inbox/messages`, MIME through `$value`, `PATCH isRead` | `POST /users/{address}/sendMail` with the MIME message |
| Gmail (API) | Service account key (JSON) with domain-wide delegation for `gmail.modify`, signed JWT assertion with `sub` = the mailbox; the token address is the configured one, never the key file's | `users/me/messages?q=is:unread in:inbox`, `format=raw`, remove `UNREAD` | `users/me/messages/send` |
| Microsoft 365 or Gmail with an administrator's sign-in | Authorization code with PKCE (S256), state bound to the mailbox, 15 minutes, used once; the account must be the mailbox's own address; only the refresh token is kept (encrypted with the client secret), rotated tokens stored | as above, delegated (`/me`) | as above |

The redirect address to register with the app is `{Channels:WebUrl}auth/mailbox-callback` (shown in the mailbox's settings); the web app passes the code and state to `POST /mailboxes/sign-in/complete` with the administrator's session. Access tokens are cached in memory per mailbox until a minute before they expire.

**Network:** every mail connection follows the outgoing-request policy (`OutboundAddressPolicy`) with its own operator setting, `Channels:Network:AllowedPrivateNetworks` (mail servers are often internal; Compose allows its own network for the GreenMail test server). Host names are checked when saved and the address actually connected to after DNS; IMAP/SMTP TLS "Automatic" means TLS on ports 993/465 and STARTTLS **required** otherwise, so passwords never travel in clear unless "No TLS" is chosen. Changing the server, port, provider, sign-in method, app, user name or address **needs the credential again**, so a stored credential is never sent somewhere it was not given for.

## Live chat

```
visitor (portal panel, or frame on another website) ──REST──► API ──► chat_sessions / chat_messages
            ▲                                                   │
            │                                                   └── LiveUpdateInterceptor: pg_notify on commit (ids only)
            └── reloads every 3 s          agents ◄── B4 live hub (/api/v1/live): "chatChanged" { chatId }
agent (/chats) ──REST──► accept, answer, transfer, end, make a ticket
```

- **Widgets** (Settings → Live chat, `channels.manage`): bilingual name and greeting, the answering group, the category of tickets, *show on the portal*, the websites that may embed it (origins such as `https://www.entity.gov.ae`; HTTPS only, `http://localhost` for development), and the code to embed: `<script src="{web app}/chat-widget.js" data-widget="{id}" data-language="ar" async></script>`. The script adds a button and, when pressed, a frame with `/chat-widget/{language}/{id}`; that page may be framed only by the listed websites (`frame-ancestors`, set by the web proxy; every other page keeps `X-Frame-Options: DENY`), and the website never sees the chat. Starting a chat or leaving a message checks the website against the list again.
- **Visitors** give their name and email (signed-in people chat as themselves) and get a random **chat token** (32 bytes; only its SHA-256 is stored), kept in the frame's session storage for the visit and sent as `X-Chat-Token`. Without the token (or the signed-in visitor) a chat answers 404. Visitors read messages after a sequence number, write (20 a minute), and end the chat; a chat whose visitor has not written or looked for 30 minutes ends on its own (`ChatIdleJob`).
- **Online** means a member of the widget's group who is active, **available** (B2's availability), holds `chat.handle` and answers fewer chats than the group's limit. With nobody online, starting a chat answers `chat-offline` and the window offers **leave a message**, which becomes an incident (channel Chat) for the email given.
- **Agents** (`chat.handle`, given to agents and team leads; tenant administrators hold every permission) see the chats waiting in their groups, their own chats and recent ones (`GET /chat/queue`), **accept** a chat (only one agent wins; refused while unavailable or at the limit), answer, **pass it on** to a colleague who handles chats in the group (it stays active) or to another group's queue (it waits again), and end it. **Make a ticket** (also needs `incidents.create`) creates an incident for the visitor — the account of their email, made when there is none — in the chat's group, assigned to the agent when they belong to it, with the transcript copied into the conversation; or **add the transcript** to an open incident of the same person (`incidents.edit`). Opening a transcript is audited as a view of personal data.
- **Real time:** chats use **B4's live updates**: a change to a chat or its messages is announced on commit through PostgreSQL `NOTIFY` (`LiveUpdateInterceptor`, ids only) and B4's hub (`/api/v1/live`) sends `chatChanged { chatId }` to the tenant's signed-in users who hold `chat.handle` (group `chat:{tenant}`); the Chats page reloads through the REST API, which checks access every time, and polls every 5 seconds while the live connection is down. **Visitors** have no live connection (they hold no session): the chat window reloads every 3 seconds while a chat is open. There is no separate chat hub, watch key or Valkey pub/sub.

## API

All under `/api/v1`, in the API explorer (tags *Incidents* and *Channels*).

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET`, `POST /incidents/{id}/comments` (B4) | Staff and the requester | The conversation; comments now carry `source`, `attachments` and, for staff, `emailDelivery` |
| `GET`, `POST /mailboxes`, `GET`, `PATCH`, `DELETE /mailboxes/{id}` | `channels.manage` | Mailboxes (credentials never returned) |
| `POST /mailboxes/{id}/test`, `/check` | `channels.manage` | Connect and count unread emails; read now |
| `GET /mailboxes/{id}/log`, `/outgoing`, `POST …/outgoing/{emailId}/resend` | `channels.manage` | Mailbox log and sent emails (addresses masked); send again |
| `POST /mailboxes/{id}/sign-in`, `POST /mailboxes/sign-in/complete` | `channels.manage` | An administrator's sign-in to the mailbox |
| `GET`, `POST /chat/widgets`, `PATCH`, `DELETE /chat/widgets/{id}` | `channels.manage` | Widgets |
| `GET /chat/portal-widget`, `GET /chat/widgets/{id}/public`, `POST …/sessions`, `POST …/offline-messages` | Anyone | Widget for the portal or a website; start a chat; leave a message |
| `GET /chat/sessions/{id}`, `POST …/messages`, `POST …/end` | The chat's token or signed-in visitor | The visitor's chat |
| `GET /chat/queue`, `GET /chat/sessions/{id}/agent`, `POST …/accept`, `…/agent-messages`, `…/transfer`, `…/close`, `…/ticket`, `…/link` | `chat.handle` | The agent's side |

`channels.manage` belongs to tenant administrators and is **not available to integrations** (added with E2 for the product owner's approval, like `ai.manage`: mailbox settings hold credentials, and an integration could point a mailbox at a server of its own). `chat.handle` is a normal permission. Problem codes: `mailbox-not-found`, `mailbox-sign-in-failed`, `sign-in-not-used`, `widget-not-found`, `widget-in-use`, `origin-not-allowed`, `chat-offline`, `chat-not-found`, `chat-taken`, `chat-limit-reached`, `agent-unavailable`, `chat-not-yours`, `chat-ended`, `chat-has-ticket`, `requester-disabled`, `rate-limited`.

## Screens

- **Conversation** (B4's, in the workspace and on the requester's incident page): each comment also shows where it came from (by email, from a chat), its files, and for staff whether the email of a reply went out. "Make a ticket" on the Chats page opens the new incident in the workspace.
- **Chats** (`/chats`, header link for `chat.handle`): availability switch, waiting chats with "Accept", the agent's chats ("x of limit"), recent chats, and the open chat with answer, pass on, end and "Make a ticket" (title from the visitor's first line).
- **Portal:** a "Chat with us" button at the end of every page when the organisation has a portal widget (not for people who answer chats).
- **Widget frame** (`/chat-widget/{language}/{id}`): the chat only, with "Powered by Zelvo".
- **Settings → Mailboxes** (list, add, and per mailbox: test, read now, sign in with Microsoft or Google, turn on/off, remove, settings, the mailbox log and the sent emails) and **Settings → Live chat** (widgets and their embed code).

English and Arabic, right to left, logical CSS properties; addresses, hosts and codes are shown left to right.

## Configuration (operator)

| Setting | Default | Meaning |
| --- | --- | --- |
| `Channels:PollInterval` | 30 s | How often each mailbox is read |
| `Channels:WorkInterval` | 5 s | How often the worker looks for due mailboxes and emails |
| `Channels:MaxEmailsPerRead` | 25 | Emails per read of one mailbox |
| `Channels:InboundLogRetention`, `OutboundRetention` | 90 days, 30 days | Mailbox log and sent emails |
| `Channels:ChatIdleTimeout` | 30 min | Chats whose visitor left end on their own |
| `Channels:WebUrl` | `Notifications:WebUrl` | Web app address (sign-in redirect) |
| `Channels:Network:AllowedPrivateNetworks` | none | Internal networks mail servers may be on |
| `Channels:MicrosoftLoginUrl`, `MicrosoftGraphUrl`, `GoogleAuthUrl`, `GoogleTokenUrl`, `GmailUrl` | the public services | For sovereign clouds or tests |

Compose: the API and worker get `Channels__WebUrl` and the Compose networks as allowed mail networks; a **GreenMail** test server (Apache 2.0, development only) offers IMAP on `greenmail:3143` and SMTP on `greenmail:3025` without TLS and accepts any password: add a mailbox with provider IMAP, both hosts `greenmail`, security "No TLS", and send mail to it from the host on `localhost:3025`.

## Demo

Seeder `e2-channels`: a support mailbox per sample organisation (IMAP, **turned off**: demo installations read no real mail server; new senders from the organisation's domain), the sample incident that came in by email with a staff reply and the requester's answer and its mailbox log (including an ignored newsletter), a portal chat widget for the agent's group, and an ended chat (in the company, the chat that became the VPN incident, with its transcript as comments).

## Tests

- Integration against PostgreSQL: `EmailChannelTests` (an email becomes an incident with a requester account; "received" goes out from the mailbox threaded on the email; the requester's answer is added without the quote; the agent's reply goes out with `In-Reply-To`/`References` and counts as the response; the number in the subject finds the ticket; a reply to a closed incident becomes a follow-up; masked logs; automatic mail, loops, DMARC failure, sender policies, strangers naming a number and the same email in two mailboxes; credentials never returned and needed again for a new server; internal hosts refused; failing mailboxes back off; contact accounts taken over only with a verified email; a requester's portal answer resumes a waiting incident) and `ChatChannelTests` (widgets and origins, visitor details and tokens, one agent accepts, the group limit, transfer to a group, a ticket with the transcript and no reply notifications, ending, audited views, leaving a message with nobody online, signed-in visitors, chat changes announced on the live channel with ids only, idle chats).
- Unit: `EmailParsingTests` (reply stripping in English and Arabic and Outlook styles, HTML to text, threading ids, titles, language, attachments) and `MailProviderTests` (Graph and Gmail requests through a recording handler, the service-account assertion, PKCE sign-in, TLS choices, provider errors).
- Web: `channels.test.tsx` (conversation, chat window for visitors and signed-in people, leaving a message, mailbox form per provider, embed code, visitor paths of the proxy).

## Not in E2

- Internal notes, @mentions, canned responses and the agent workspace (B4); SignalR for the notification bell (B4).
- The AI virtual agent in the chat and its handover (E5); one-click translation of messages (E5, LANG-7).
- WhatsApp, Teams and SMS (E3).
- Push from Microsoft Graph (subscriptions) or Gmail (Pub/Sub): they need an address the internet can reach; polling works offline.
- Attachments in chats and in staff replies (files are attached to the incident as before).
- Business hours on the widget beyond the agents' availability; a tenant's own sender domain checks (F1).
- Not tried against real Microsoft 365 and Google Workspace tenants in this session (no access); the clients follow the providers' documented APIs and are tested against recorded requests.
