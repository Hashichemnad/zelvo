# Customer voice: complaints, inquiries and suggestions (task C6, module 3)

Covers module 3 of `docs/requirements.md` and the UAE requirements GOV-2 (happiness meter) and GOV-3 (happiness and customer-voice reporting):

- three record types with their own forms, categories, SLAs and closure flows;
- anonymous submission, configurable per tenant;
- the happiness meter on resolved tickets and in the portal;
- a customer voice and happiness report.

Product owner decisions (2026-11-24):

- **Happiness meter.** Three faces by default (unhappy, neutral, happy); five points as an option. The question and labels are wording the administrator sets to the entity's customer-happiness charter. It is asked when any of the requester's tickets is resolved (incidents, service requests, complaints, inquiries, suggestions), and the portal has a general "Rate our services".
- **Anonymous submission.** Complaints, inquiries and suggestions only; anonymous service requests stay out. The visitor gets the number and a one-time tracking code and follows the record with them in the portal.
- **Unanswered complaints.** A resolved complaint the customer does not confirm closes after the tenant's auto-close period and is recorded as "not confirmed by the customer". Until then the customer can reject the resolution, which reopens it.

- **No email for anonymous senders** (2026-11-24). Anonymous submissions ask for no email or other personal details. The tracking reference (the number) and the private access code, shown once, are how the person checks status, replies and confirms closure. The code is stored only as a hash; submission and tracking are rate-limited.
- **Notifications approved** (2026-11-24): the seven events below, in the recipient's language, each switchable by administrators.
- **The team, never the agent** (2026-11-24): requesters see the team that has their record, on My tickets and on the record's page, never an agent's name.

## Code

| What | Where |
| --- | --- |
| Record types `Complaint` (`CMP-000123`), `Inquiry` (`INQ-`), `Suggestion` (`SUG-`) on the ticket core; table `customer_voice_records` (priority, severity, root cause, confirmation, review and feedback, conversion, service, anonymous, tracking code hash); table `happiness_ratings`; migration `CustomerVoice` with row-level security on both | `src/api/Zelvo.Data/Voice`, `Tickets/Ticket.cs` |
| Permissions, search documents (`voice`), the anonymous account | `src/api/Zelvo.Tickets/VoiceSearch.cs` |
| API: records, conversation, transitions, assignment, confirmation, review, conversion; anonymous submission and tracking; happiness; report; settings | `src/api/Zelvo.Api/Voice` |
| Notifications | `VoiceNotificationSource` in `src/api/Zelvo.Notifications/NotificationSources.cs`, templates in `Texts/en.json` and `ar.json` |
| Demo seeder `c6-customer-voice` | `src/api/Zelvo.Api/Demo/DemoCustomerVoiceSeeder.cs` |
| Web pages: Contact us, sending, tracking, My messages, staff list and page, report, Settings → Customer voice; the meter on My incidents and My requests | `src/web/src/app/[locale]/{contact,track,my-voice,customer-voice,settings/customer-voice}`, `src/web/src/components/voice` |

## Records

The three record types share the ticket core: numbers per type (prefixes in the number prefix setting; existing tenants get the defaults), states, B2 routing, B3 SLA clocks and the B4 conversation (`ticket_comments`).

- **Forms and categories.** C1's form bindings take the record type, so each type has its own forms per category of the shared tree. Settings → Forms now chooses the record type of a binding.
- **SLAs.** B3 policies match on record type; Settings → SLA policies now chooses it.
  - The SLA priority of a complaint is its severity's (Settings: High P2, Medium P3, Low P4 by default).
  - Inquiries (P3) and suggestions (P4) have a fixed priority each.
  - `SlaTracker` reads the priority from `customer_voice_records` when the ticket has no incident details.
- **Service.** A record can name the catalog service it is about, for reporting per service.
- **Who sees what.** As for service requests:
  - staff with `voice.read` see every record;
  - requesters see their own (and those logged for them), everyone else gets 404;
  - requesters see the team (group) that has it, never the assignee (decision 2026-11-24); they do not see SLA clocks or the root cause.

### Closure flows

| Type | How it closes |
| --- | --- |
| Complaint | Staff set the **root cause** (Settings → Customer voice) and write the **resolution summary** (resolution notes) to resolve. The confirmation becomes Pending and staff cannot close it. The requester **accepts** (Closed, Confirmed) or **rejects with a reason** (Reopened, Rejected, the reason in the conversation). Unanswered, the auto-close job closes it as NotConfirmed. |
| Inquiry | **Fast answer:** while the requester types the question, matching knowledge articles are suggested (search, type `article`). Staff answer in the conversation and resolve with the answer. The requester may reopen a resolved inquiry. Staff can **convert** an open inquiry to a complaint, or to a service request for a service offered to the requester (without form answers). The inquiry is resolved with "Converted to …", and both records link to each other. Anonymous inquiries cannot become service requests. |
| Suggestion | The **review** is Under review (new), Accepted, Implemented or Declined, with feedback to the submitter. Implemented and Declined need feedback and resolve the suggestion, with the feedback as resolution notes. Suggestions are not resolved by a plain state change (409 `review-required`). |

## Anonymous submission

- **Setting.** Off by default; Settings → Customer voice turns it on.
- **What visitors send.** `POST /public/customer-voice` takes type, title, description and optionally a public service. Forms and categories need a signed-in person, and staff classify the record.
- **The requester.** The tenant's **anonymous account** (`system:anonymous`). It is disabled, has no email or roles and is never listed in Settings → Users.
- **The answer.** The number and a **tracking code**: 12 characters from 31 unambiguous symbols, about 59 bits, from `RandomNumberGenerator`. Only its SHA-256 is stored, and the code is shown once.
- **Following it.** `POST /public/customer-voice/track` with number and code (case, spaces and hyphens are ignored) answers the record and its public replies. Wrong pairs answer 404 alike and are compared in constant time.
  - With the same pair the visitor replies (`/track/comments`), confirms or rejects a complaint's resolution (`/track/confirmation`) and rates it (`/track/happiness`).
  - **Rate limits** of their own, on top of the API's general ones: 5 submissions per client address and hour, and 30 tracking calls in ten minutes per client address and per number (so a code cannot be guessed for one record from many addresses either). Over the limit: 429 `rate-limited` with `Retry-After`.
- **Notifications.** Nobody is notified on the requester side of an anonymous record.
- **Web proxy.** Visitors reach these paths, `GET /settings/customer-voice` and `POST /happiness/portal` through the visitor paths.

## Happiness meter (GOV-2)

- **Settings.** Settings → Customer voice: on or off; 3 or 5 points with a bilingual label each (worst first); the ticket question and the portal question; asked on resolved tickets; shown in the portal.
- **On tickets.** `POST /tickets/{id}/happiness` works for every record type. It takes the requester's score (1 to the points) and an optional comment, once per ticket (unique index) and only once the ticket is resolved or closed.
  - The rating keeps the record type, channel and service for reporting.
  - `GET /tickets/{id}/happiness` returns the rating and whether the caller may still rate.
- **In the portal.** `POST /happiness/portal` takes one rating per signed-in person per day, or per device for visitors (a random id kept on the device, stored as a hash).
- **On screen.** A radio group with one labelled choice per point; the faces are decorative.

## Report (GOV-3)

`GET /customer-voice/report?from=&to=` needs `voice.report` and is audited. The period is the last 12 months by default, at most two years. It returns:

- per record type: total, open, resolved, anonymous, and the average hours to resolve;
- records per state and per channel;
- per service: complaints, inquiries, suggestions, ratings and happiness;
- complaints per severity, root cause and confirmation;
- suggestion reviews;
- happiness: the number of ratings, the percentage, the scores, and per record type (with the portal) and channel;
- a month-by-month trend.

**Happiness** is the average score as a share of the scale: worst 0 %, best 100 %. Ratings on 3- and 5-point scales combine.

`GET /customer-voice/report/csv` exports the monthly trend (UTF-8 with BOM, for Excel). The web page shows the same figures as text, tables and plain bars. Dashboards and scheduled reports come with F2. The open point in the requirements, which formats regulators expect, remains open.

## API

| Endpoint | What |
| --- | --- |
| `POST /customer-voice` | Send one (type, title, description, category, service, severity for complaints, the form's answers). Staff with `voice.edit` log one for someone else (`requesterId`, `channel`) |
| `GET /customer-voice` | Own ones, or all with `voice.read`. Filters: type, mine, open, state, number, assignee, group, service |
| `GET /customer-voice/{id}`, `PATCH` | One record. Staff change the title, severity (the priority follows), root cause, category and service |
| `GET …/form`, `…/attachments/{id}/content` | Form answers; files once scanned (audited) |
| `GET`, `POST …/comments` | Conversation: public replies and internal notes (staff) |
| `POST …/transitions` | States, as described above |
| `PUT …/assignment`, `POST …/route` | Group and assignee (`voice.assign`) |
| `POST …/confirmation` | The requester accepts or rejects a resolved complaint |
| `POST …/review` | A suggestion's review (`voice.edit`) |
| `POST …/conversion` | Convert an inquiry (`voice.edit`) |
| `POST /public/customer-voice`, `…/track`, `…/track/comments`, `…/track/confirmation`, `…/track/happiness` | Anonymous submission and tracking |
| `GET`, `POST /tickets/{id}/happiness`, `POST /happiness/portal` | Happiness meter |
| `GET /customer-voice/report`, `…/report/csv` | Report (`voice.report`) |
| `GET`, `PUT /settings/customer-voice` | Settings (anyone reads; `voice.manage` changes) |

Every change is audited by the audit log's change capture. Report views and exports, and file downloads, are audited by name.

## Notifications

New events, each with bilingual templates (formal Arabic, approved terms شكوى / استفسار / اقتراح) that administrators can change in Settings → Notifications:

| Event | To |
| --- | --- |
| `voice.received` | The requester |
| `voice.replied` | The requester |
| `complaint.resolved` | The requester, asking them to confirm |
| `inquiry.answered` | The requester |
| `suggestion.reviewed` | The requester, with the review state and feedback |
| `voice.assigned` | Staff |
| `voice.queued` | Staff |

- Links go to `/my-voice/{id}` for requesters and `/customer-voice/{id}` for staff.
- Placeholders `ticket.type` and `review.state` are words in the recipient's language.
- Approved by the product owner on 2026-11-24 and listed with B5's events in `notifications.md`.

## Permissions

| Permission | What | Roles by default |
| --- | --- | --- |
| `voice.read` | See every complaint, inquiry and suggestion | Agent, team lead, tenant administrator, executive |
| `voice.edit` | Work on them | Agent, team lead, tenant administrator |
| `voice.assign` | Assign them | Agent, team lead, tenant administrator |
| `voice.report` | The report | Team lead, tenant administrator, executive |
| `voice.manage` | Settings → Customer voice | Tenant administrator |

## Demo

Seeder `c6-customer-voice` (order 160) creates, for each sample organisation:

- 27 complaints, inquiries and suggestions over six months, in English and Arabic, in every closure state;
- ratings on most of them, on some incidents and requests, and 36 portal ratings, improving over the months;
- anonymous messages, which the government entity allows.

## Tests

- `CustomerVoiceApiTests` (PostgreSQL):
  - the complaint flow with root cause, rejection and confirmation, rating once, and notifications;
  - unanswered complaints closing as not confirmed;
  - inquiries answered, reopened and converted;
  - suggestion reviews;
  - anonymous submission (off by default, tracking code, replies, confirmation, rating, no person listed);
  - settings checks and portal ratings once a day;
  - the report and its permission.
- `DemoModeTests`, `IdentityApiTests` updated.
- Web: `voice.test.tsx` (meter, anonymous submission, confirmation, progress and facts, settings), visitor paths, permission labels, Settings sections.
- End to end: `customer-voice.spec.ts`, in its own project after live chat.

## Left out

- **Anonymous submissions with a form.** Visitors send title and description; staff classify them.
- **Customer voice in the agent workspace (B4).** Staff work on these records on their own pages; the workspace is for incidents.
- **AI.** Duplicate and sentiment detection and suggested replies come with E5.
- **More in the report.** Dashboards, scheduled reports and regulator formats come with F2; field-level rules for these records, as for service requests.
