# WhatsApp, SMS and Microsoft Teams (task E3)

Covers the rest of **module 6** (omnichannel intake) of `docs/requirements.md`: WhatsApp Business (create and update tickets, receive notifications, voice notes transcribed once the AI is on), Microsoft Teams (raise tickets and approve requests) and SMS gateways; and the SMS, WhatsApp and Teams channels of **module 10**'s notifications ("Notifications by email, SMS, WhatsApp, Teams and in-app"), including the Teams and WhatsApp approvals that C3 left for E3.

Product owner decisions of 2026-11-25:

1. **WhatsApp through Meta's Cloud API** directly (Meta ended the on-premises API in 2025). Incoming messages arrive by a webhook Meta signs with the app's secret, so the installation needs a public HTTPS address for `/hooks/`. Messages sent more than **24 hours** after the person last wrote must use a **template Meta approved**; the administrator names the template per notification event (or one for all) and Meta's language code per Zelvo language.
2. **Approvals:** **Teams buttons decide** in Teams, because Microsoft signs every Teams activity with the person's Entra identity, which Zelvo links to the Zelvo account once. **WhatsApp (and SMS) carry the two links of the email**, which open the approval in Zelvo where the approver signs in, because a phone number is not a sign-in.
3. **Voice notes:** transcription is an **AI feature of its own, off by default**, with its own provider and model (OpenAI, Azure OpenAI, Gemini or an OpenAI-compatible server such as a self-hosted Whisper; not Anthropic, whose API takes no audio). The audio goes to the provider **unmasked** (audio cannot be masked; Settings says so); the transcript is masked like any text for later AI use. Without it, the voice note is attached to the ticket.
4. **SMS gateways: Twilio and a generic HTTP gateway** set up on screen (URL, method, authentication, body template with `{{to}}`, `{{text}}` and `{{from}}`, fields of incoming messages), which covers e&, du, Unifonic, Infobip and others. UAE senders need a TDRA-registered sender ID with any provider.

Code: `src/api/Zelvo.Data/Channels/MessagingEntities.cs` and `ZelvoDbContext.Messaging.cs` (data, migration `Messaging`), `src/api/Zelvo.Channels/Messaging` (provider clients, signature and token checks, intake, sender and worker job), `src/api/Zelvo.Notifications/MessagingNotifications.cs` (notifications by WhatsApp, SMS and Teams), `src/api/Zelvo.Ai` (`TranscribeAsync`, `VoiceTranscriber`, AI job `Transcription`), `src/api/Zelvo.Api/Channels/MessagingEndpoints.cs` (Settings → Messaging, Account → Notifications channels, Teams app package) and `MessagingHooks.cs` (webhooks, Teams approvals), `src/api/Zelvo.Api/Demo/DemoMessagingSeeder.cs`, `src/web/src/components/channels/MessagingPanel.tsx`, `src/web/src/components/notifications/NotificationChannelsForm.tsx`, the conversation (`Conversation.tsx`), the page `settings/messaging`, and the proxy route in `deploy/compose/caddy/Caddyfile`.

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `messaging_channels` | Name, kind (WhatsApp, Sms, Teams) and provider (WhatsAppCloud, Twilio, GenericHttp, TeamsBot; a check constraint pairs them), account (WhatsApp phone number id, Twilio account SID, Teams app id), sender, directory (WhatsApp business account, Teams' Entra tenant), settings (JSON: the generic gateway's request and answer mapping, WhatsApp templates), the **credential** and the **webhook secrets** (WhatsApp app secret and verify token, gateway token), both **encrypted** with `Zelvo.Secrets`, who may write in, category or group of new tickets, on/off, last message and last error | Audited (secrets masked) |
| `messaging_contacts` | One person on one channel: phone number in E.164 or the Teams account's Entra object id, the Zelvo account, the ticket their messages go to (a plain id), when they last wrote (WhatsApp's window), Teams' service address, conversation and member id | Not audited (the link is: `channels.messaging.contact-linked`) |
| `channel_messages` | The channel log and outgoing queue: direction, kind (Text, Voice, Media, Template, Card, Action, Event), provider id (incoming messages taken once), address, text, template or card (JSON), language, status (Received, Ignored, Rejected, Pending, Sent, Delivered, Read, Failed), reason, attempts, next attempt, lease, ticket, comment, notification, approval decision | Not audited. Kept 30 days (`zelvo_purge_channel_messages`) |
| `voice_notes` | A WhatsApp voice note: its comment, ticket, recording (an attachment), transcript status (Pending, Done, Failed, Off), transcript, language, AI call | Comments never change, so the transcript lives here |

Also: `tickets.messaging_contact_id` (the conversation a ticket came in through; its updates go back there), `ticket_comments.messaging_channel_id` and the comment sources `WhatsApp`, `Sms`, `Teams` (always public, B4's check constraint), the ticket channels `Sms` and `Teams`, `users.notify_whats_app`, `notify_sms` (opt-in, off) and `notify_teams` (on), the AI job kind `Transcription`, and the audio file types ogg, amr, mp3, m4a and wav (`FileTypes`). All four new tables have forced row-level security; the guard test covers them. The worker finds tenants with messages due through `zelvo_messaging_due_tenants()` (ids and slugs only).

## Reaching Zelvo: `/hooks/`

Providers post to `{Channels:PublicUrl}hooks/{tenant}/{whatsapp|sms|teams}/{channel id}`. The reverse proxy (Caddy) passes `/hooks/{tenant}/…` to the API's `/api/v1/hooks/…` **with the raw body** and sets `X-Zelvo-Tenant` from the path (replacing any header the caller sent); every other path still goes to the web app only. The webhooks carry no Zelvo sign-in; each is trusted only with the provider's proof:

| Provider | Proof | Then |
| --- | --- | --- |
| WhatsApp (Meta) | `GET`: `hub.verify_token` equals the channel's verify token (Zelvo makes it; shown to administrators to enter in Meta). `POST`: `X-Hub-Signature-256` is the HMAC-SHA256 of the raw body with the **app secret**, compared in constant time | Messages of the channel's phone number id only; delivery reports (sent, delivered, read, failed) update the outgoing message, never stepping back |
| Twilio | `X-Twilio-Signature`: HMAC-SHA1 (Twilio's algorithm) of the public address plus the sorted form fields, with the **auth token** | Text messages and status callbacks; Zelvo answers empty TwiML (no automatic reply) |
| Generic gateway | The channel's token in the address (`?token=`) or `X-Zelvo-Token`, compared in constant time | The fields named in the settings (form or dotted JSON paths) |
| Microsoft Teams | A JSON Web Token signed by **Bot Framework** (keys from its OpenID metadata, issuer `https://api.botframework.com`), audience the Teams app's id, still valid, whose `serviceurl` claim is the activity's service address; the activity's Entra tenant must be the channel's directory, and only personal chats are taken | Messages, the app being added, and card buttons |

The API's own bearer handler ignores `Authorization` on `/api/v1/hooks` (Teams sends Microsoft's token there). Teams' service address must be one of Microsoft's (`Channels:TeamsServiceHosts`), so Zelvo never sends its Bot Framework token elsewhere. Refused requests answer 401 `signature-invalid` and change nothing.

## Incoming messages (`MessagingIntake`)

1. **Taken once** per channel and provider id; a channel that is off logs `channel-off`; receipts and kinds Zelvo does not take are `Ignored`.
2. **Who wrote.** The contact's account, else: WhatsApp and SMS, the user with that phone number (E.164; UAE local numbers are converted); Teams, the user with the email or sign-in name Teams reports for the member in the customer's directory, **linked once**. Unknown senders get a requester account (a `contact:` account, as E2's, with the phone and WhatsApp and SMS updates on) when the channel accepts anyone; otherwise `Rejected` with **no automatic reply** (as for email). Disabled accounts are rejected.
3. **Which ticket.** A message that starts with the number of **one of the sender's own tickets** (`INC-000123 …`) goes to that ticket. Otherwise WhatsApp and SMS messages go to the contact's latest ticket while it is not closed (a phone conversation is one thread); Teams messages raise a **new incident each** (staff often raise several). A message to a closed ticket becomes a **follow-up** incident, as for email. A requester's answer moves a ticket waiting for them back to In progress (B3 clocks resume).
4. **New incidents** get the channel (WhatsApp, Sms, Teams), the first line as title, the text as description, the channel's category (then B2's rules) or group, B3's clocks and the search index.
5. **Files.** Voice notes are kept as the recording (Ogg, AMR, MP3, M4A, WAV; content checked; accepted whatever the allowlist, as they are the message; within the size limit) and scanned by ClamAV like any attachment; images and documents follow the tenant's attachment rules. A voice note is a public comment without text plus its `voice_notes` row; when transcription is on, an AI job (`Transcription`) waits until the recording is scanned clean, sends it through the AI gateway and stores the transcript, which staff and the requester see with the comment.

## Outgoing messages

`MessagingNotifications` runs when B5 renders a notification for its recipient (in their language) and queues `channel_messages`; the worker's `MessagingJob` sends them (four providers' thin clients, `MessagingClients`, through the channels' outgoing-request policy), with retries after 1 min, 5 min, 15 min, 1 h, 3 h, 6 h and 12 h; refusals that will not improve (a refused number, outside WhatsApp's window, no Teams conversation yet) fail at once and show in the log with the reason; "Send again" in the log.

- **The ticket's conversation:** a requester's notifications about a ticket that came in by WhatsApp, SMS or Teams go back **there** (and not to their other channels). Staff public replies (`incident.replied`, `voice.replied`) go out as `INC-000123: the reply` even when the event is switched off, as they are the conversation itself; staff see whether each was sent, delivered or read.
- **People's own choice** (Account → Notifications): WhatsApp and SMS to the phone of their profile (opt-in, needs a phone), Teams once they have used the tenant's Teams app (on by default). Administrators switch WhatsApp, SMS and Teams per event in Settings → Notifications (`messaging`, on by default).
- **Text:** the notification's subject, in-app text and link; approvals carry the Approve and Reject links of the email.
- **WhatsApp's window:** within 24 hours of the person's last message, text; after it, the event's template (or the default template) with the text as its one variable `{{1}}` (line breaks removed, as Meta requires) in Meta's language code for the person's language; without a template the message is logged `no-template` and not sent.
- **Teams approvals:** the approver gets an **Adaptive Card** with the request, a reason box, **Approve**, **Reject** and a link to the approval. A button press arrives signed by Bot Framework for the linked person; Zelvo checks they are the decision's approver and records it through C3's `ApprovalService` (`via` = `teams`, audited as that person; rejecting needs the reason), then answers in the chat (recorded, rejected, reason needed, already decided, not yours, not linked).

## Teams app

Administrators register a **single-tenant bot** in their Entra directory (app id and client secret; messaging endpoint = the channel's webhook address) and download the **Teams app package** from the channel (`GET /messaging/channels/{id}/teams-app`): manifest 1.17 with the organisation's name in English and Arabic (`ar.json`), personal scope, and icons. They upload it in the Teams admin centre. When someone adds the app, Zelvo stores the conversation, links the account and sends a welcome in their language. Zelvo answers Teams with a token for `https://api.botframework.com/.default` from the customer's own directory (`{MicrosoftLoginUrl}{directory}/oauth2/v2.0/token`), cached until a minute before it expires.

## API

All under `/api/v1`, in the API explorer (tag *Channels*).

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET`, `POST /messaging/channels`, `GET`, `PATCH`, `DELETE /messaging/channels/{id}` | `channels.manage` | Channels; secrets never returned; a new account, number, directory or gateway address needs the credential again |
| `POST /messaging/channels/{id}/test` | `channels.manage` | Checks the account with the provider; with `to`, queues a test message (WhatsApp: Meta's `hello_world` template) |
| `GET /messaging/channels/{id}/log`, `POST …/log/{messageId}/resend` | `channels.manage` | The channel log (numbers masked, text lengths only); send again |
| `GET /messaging/channels/{id}/teams-app` | `channels.manage` | The Teams app package |
| `GET`, `PUT /me/notification-channels` | Signed-in people | WhatsApp, SMS and Teams for their notifications |
| `GET`, `POST /hooks/whatsapp/{id}`, `POST /hooks/sms/{id}`, `POST /hooks/teams/{id}` | The providers (proof above) | Incoming messages, reports and Teams activities |

Comments (`GET /incidents/{id}/comments`) carry `voiceNote` (recording, status, transcript, language) and, for staff, `messageDelivery`. `PUT /notifications/settings/events/{event}` takes `messaging`. Problem codes: `channel-not-found`, `message-not-found`, `not-failed`, `signature-invalid`, `verify-token-wrong`. The AI feature `transcription` is refused with an Anthropic provider.

## Configuration (operator)

| Setting | Default | Meaning |
| --- | --- | --- |
| `Channels:PublicUrl` | `Channels:WebUrl` | The installation's public address providers post to (and Twilio signs) |
| `Channels:WhatsAppGraphUrl`, `TwilioUrl` | Meta's Graph API v23.0, Twilio's API | For tests or other regions |
| `Channels:BotFrameworkOpenIdUrl`, `BotFrameworkIssuer`, `TeamsServiceHosts` | Microsoft's public cloud | Sovereign clouds |
| `Channels:MaxMediaBytes` | 16 MB | Largest voice note or file taken |
| `Channels:MessageRetention` | 30 days | Channel log |
| `Channels:Network:AllowedPrivateNetworks` | none | Internal networks gateways may be on (shared with mailboxes) |

Compose sets `Channels__PublicUrl` to `https://{ZELVO_PUBLIC_HOST}/` and the proxy routes `/hooks/`. For providers to reach an installation, its public host must be reachable from the internet; offline installations can still use SMS through an internal gateway and Teams only with internet access.

## Demo

Seeder `e3-messaging` (order 250): a WhatsApp number, an SMS gateway and a Teams app per sample organisation, all **turned off** (no real provider is reached), and an incident from the sample requester (fictional number) that came in by WhatsApp with a transcribed voice note, the agent's reply that went back and was read, and the channel log.

## Tests

- Integration against PostgreSQL (`MessagingChannelTests`): WhatsApp from Meta's verify token and signatures to an incident with a phone-number account, the acknowledgement on WhatsApp, the next message in the same ticket, a voice note kept with transcription off, the staff reply sent as the reply and its read report, the 24-hour window without and with a template, secrets never returned or audited; Twilio and generic gateways only with their proof, UAE local numbers stored in E.164, internal gateway addresses refused; Teams tokens (wrong app, wrong key refused), an incident for the linked person, the approval card, a rejection without reason and someone else's press refused, approval on the card audited as the approver with `via` teams, other directories ignored, the app package; people's own WhatsApp and SMS choices needing a phone, an event switched off for messaging, the approval links.
- Unit (`MessagingProviderTests`): WhatsApp text and template requests, permanent and temporary provider errors, Twilio's form and messaging service, the generic gateway's template with escaping and header authentication, Teams' token and activity and refused service addresses, Meta's and Twilio's signatures.
- Web (`messaging.test.tsx`): the channel list, webhook address and test, adding a generic gateway (Arabic), voice notes with transcripts and delivery states in the conversation, Account → Notifications channels.

## Not in E3

- Rich WhatsApp messages (buttons, lists) and media in outgoing messages; group chats and channels in Teams (personal chats only); Teams messaging extensions and tabs: came with task J4 (below).
- SMS from other named providers (they use the generic gateway); two-way phone-number verification of profiles (phone numbers come from profiles, imports and identity connectors).
- Not tried against real Meta, Twilio or Microsoft tenants in this session (no access); the clients follow the providers' documented APIs and are tested against recorded requests.

## The Teams app of module 19 (task J4)

Product owner decision 17 of 2026-11-26: the E3 Teams app gets a **personal tab**, a **"Raise a ticket" dialog** and a **message action**; notifications and approvals stay as E3 built them. The app package is now **version 1.1.0** (`TeamsAppPackage.Version`; administrators upload it again in the Teams admin centre). Code: `src/api/Zelvo.Api/Channels/TeamsApp.cs` (cards, invokes, commands), `MessagingHooks.TeamsAsync` (invokes and commands), `TeamsAppPackage` in `MessagingEndpoints.cs`; the web app's `src/app/[locale]/teams`, `src/app/auth/teams`, `src/components/teams`, `src/lib/teams.ts`.

- **Personal tabs** (`staticTabs`): *My tickets*, *My work* (the agent's workspace) and *Approvals*, each the Zelvo web app inside Teams with J1's phone layouts. The tab's address is `/auth/teams/tab?view=…&locale={locale}` (Teams fills in its language; Arabic opens `/ar/…`, anything else `/en/…`), which opens `/{lang}/teams/tab`. Signed in, that page goes on to the chosen page; otherwise it offers **sign-in in a pop-up** (Teams JS SDK `authentication.authenticate`, `@microsoft/teams-js` 2.59.0, MIT, new dependency): the pop-up is a window of its own, so it uses the usual first-party sign-in (`/auth/teams/start` → `/auth/login` → `/{lang}/teams/auth-end`), whose last page asks `POST /auth/teams/handoff` (same origin, CSRF token) for a **one-time code** (random, only its hash kept two minutes) and hands it to the tab with `notifySuccess`. The tab exchanges it once at `POST /auth/teams/session` for its own session cookie `__Host-zelvo-teams`: `SameSite=None; Secure; HttpOnly; Partitioned` (CHIPS), naming the same server-side session (Keycloak's refresh tokens are single-use, so the two cannot be separate sessions; signing out in either ends both). Inside Teams only that cookie is sent; outside Teams it does not exist. Signing out in the tab comes back to the tab.
- **Framing.** `frame-ancestors` lists Teams' addresses only (`https://teams.microsoft.com`, `https://*.teams.microsoft.com`, `https://teams.cloud.microsoft`, `https://*.teams.cloud.microsoft`; `TEAMS_FRAME_ANCESTORS` replaces them, for example for sovereign clouds) on the tab page and on any page requested with the Teams tab's cookie; every other page keeps `frame-ancestors 'none'`. Browsers that block partitioned cookies in frames (Safari's web client) cannot keep the tab signed in; the Teams desktop and mobile apps can.
- **Commands** (`commandLists`, English and Arabic): *Raise a ticket* / «رفع طلب» answers with a card whose button opens the dialog; *My tickets* / «طلباتي» answers with a **status card** per open ticket (at most 5, newest first); *Help* / «مساعدة» explains the app. These words are commands, not new tickets; every other message still raises or continues a ticket as in E3.
- **The dialog** (Teams task module, invokes `task/fetch` and `task/submit`): title, details and the category (every active category, shown with its path). When the category's form (C1, `FormResolver`) has fields, a second card asks for the fields a card can show (text, number, date, dropdown, checkbox, radio, rich text as several lines); files, people, locations, signatures, tables and configuration items cannot, so a **required** one of those sends the person to the full form in Zelvo (`/{lang}/my-incidents/new?category=…`), and optional ones are left for later with a note. Answers go through the **form engine on the server** (`FormSubmission.PrepareAsync`: rules, checks, formats, personal-data answers kept apart); a refusal shows the card again with the reasons. The incident is raised for the person (B2 routing, B3 clocks, search) with the channel Teams through `IncidentIntake`, audited as the person (`via` teams, `channels.teams.ticket-raised`), and the dialog closes with the number.
- **The message action** (`composeExtensions`, command `raiseFromMessage`, context message; invokes `composeExtension/fetchTask` and `composeExtension/submitAction`): on any Teams message, also in channels and group chats, *Raise a ticket* opens the same dialog with the message's text as title and details (HTML removed) and a link back to the message added to the description. The ticket is the **person who used the action**'s, not the message author's. A channel or group conversation is never stored as the person's chat, so notifications stay in their personal chat with the app. Invokes from other directories are ignored like messages; people not linked to an account are told so and nothing is raised.
- **Status cards**: number, title, state, the team that has it (never a person, decision 2026-11-24) and "Open in Zelvo" (`/my-incidents/{id}`, `/my-requests/{id}`, else `/my-tickets`), after a ticket is raised from the dialog or the message action, and for *My tickets*.
- **Package** (manifest 1.17): `staticTabs` (three), the bot's `commandLists`, `composeExtensions` with the message action, `validDomains` the installation's host; `ar.json` names the tabs, commands and action in Arabic (`staticTabs[0].name`, `bots[0].commandLists[0].commands[0].title`, `composeExtensions[0].commands[0].title` and their descriptions). The welcome and the app's description mention the new commands.
- **Tests:** `TeamsAppTests` (the command card, the dialog with a category's form, the form engine's refusal, the incident with its answers and the status card, *My tickets*, the message action from a channel in Arabic, strangers and other directories, the package in both languages), web tests for the tab, the hand-off (one-time, partitioned `SameSite=None` cookie, other origins refused), the tab address and `frame-ancestors`.
- **Not tried in a real Microsoft 365 tenant yet** (no Teams environment here); Teams' adaptive card and task module behaviour follows Microsoft's documentation. Not built: tabs in channels and group chats, link unfurling, the approvals of the tab outside the web app's own page.
