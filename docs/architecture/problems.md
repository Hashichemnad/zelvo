# Problem management (tasks H1 and H2, module 16)

Covers the core of module 16 of `docs/requirements.md`: problems raised from one or many incidents or by hand, the stages New, Assessment, Root-cause analysis, Known error, Resolved and Closed, the root-cause tools (5 Whys, fishbone, the timeline of linked incidents), the known error database with workarounds that agents see on matching incidents and that reach the portal through the knowledge base, and links to incidents, configuration items and knowledge articles. Task H2 adds AI suggestions of problems from similar incidents and proactive problems from trends (section **Problem detection**); changes join the timeline with H3.

Product owner decisions of 2026-11-26 that apply:

- **Decision 9.** Known errors reach the portal as **external knowledge articles through D1's four-eyes review**: Zelvo makes the draft, a publisher other than its author approves it.
- **Decision 11.** The **problem manager is a permission**, `problems.manage`, held by team leads and tenant administrators by default; the six personas stay.
- **Decision 10 (H2).** **Trend detection**: at least 10 incidents in a month and at least twice the average of the three months before, editable in Settings; suggestions only.

## Code

| Piece | Where |
| --- | --- |
| Record type `Problem` (`PRB-000123`) on the ticket core; tables `problems`, `known_errors`, `known_error_number_sequences`, `problem_incidents`, `problem_articles` (row-level security; migration `ProblemManagement`) | `src/api/Zelvo.Data/Problems`, `ZelvoDbContext.Problems.cs`, `Tickets/Ticket.cs` |
| Permissions, stages, search documents `problem` and `known-error` | `src/api/Zelvo.Tickets/ProblemSearch.cs` |
| API: problems, stages, assignment, analysis, notes, incident and article links, timeline; known errors; Settings → Problems | `src/api/Zelvo.Api/Problems` |
| Notifications | `src/api/Zelvo.Notifications/ProblemNotificationSource.cs`, templates in `Texts/en.json` and `ar.json` |
| Demo seeder `h1-problems` | `src/api/Zelvo.Api/Demo/DemoProblemsSeeder.cs` |
| Pages `/problems`, `/problems/new`, `/problems/[id]`, `/known-errors`, Settings → Problems; the known errors panel on staff incident pages and in the workspace; "Raise a problem" in the workspace's bulk actions | `src/web/src/app/[locale]/{problems,known-errors,settings/problems}`, `src/web/src/components/problems` |
| H2: tables `problem_suggestions`, `problem_suggestion_incidents` (row-level security; migration `ProblemDetection`, with `zelvo_problem_detection_tenants()`) | `src/api/Zelvo.Data/Problems/ProblemSuggestionEntities.cs`, `ZelvoDbContext.Problems.cs` |
| H2: similar-incident groups and the AI judge (feature `problemDetection`) | `src/api/Zelvo.Ai/Features/ProblemGroups.cs` |
| H2: detection (`ProblemDetector`, `ProblemTrendDetector`), the job `ProblemDetectionJobs`, the queue and Settings → Problems → Problem detection | `src/api/Zelvo.Api/Problems/ProblemDetection.cs`, `ProblemSuggestionEndpoints.cs` |
| H2: demo seeder `h2-problem-detection` | `src/api/Zelvo.Api/Demo/DemoProblemDetectionSeeder.cs` |
| H2: `/problems/suggestions`, the detection settings, the link on `/problems` | `src/web/src/app/[locale]/problems/suggestions`, `src/web/src/components/problems/ProblemSuggestions.tsx`, `ProblemDetectionSettingsForm.tsx` |

## Problems on the ticket core

A problem is a ticket of record type `Problem` with its details in `problems`. It shares numbers (prefix in Settings → Incidents → number prefixes, `PRB` by default; existing tenants get it), B2 routing ("Route again"), B3 SLA clocks (Settings → SLA policies chooses the record type; the problem's own priority 1–5 picks the targets), the conversation table (internal notes only: a problem has no customer) and the audit log's field history.

- **Requester.** Problems have no customer; the person who raised it stands in as requester and is told when it is resolved.
- **Raised from incidents.** `POST /problems` takes up to 100 `incidentIds`. When the category is not given and the incidents share one, the problem takes it; when the priority is not given it takes the incidents' highest (else 3). The workspace's bulk bar ("Raise a problem") and each staff incident page open the new problem form with the incidents filled in.

### Stages

Module 16 names six stages. The ticket core's state decides three of them and the problem's **phase** the rest, so auto-close, SLA clocks, reports and search keep working unchanged:

| Stage | Ticket state | Phase |
| --- | --- | --- |
| New | New, Assigned | — |
| Assessment | In progress, On hold, Reopened | Assessment |
| Root-cause analysis | In progress, On hold, Reopened | RootCauseAnalysis |
| Known error | In progress, On hold, Reopened | KnownError |
| Resolved | Resolved | — |
| Closed | Closed | — |

`POST /problems/{id}/transitions` takes `to`:

| To | Needs | What happens |
| --- | --- | --- |
| `Assessment`, `RootCauseAnalysis` | `problems.edit`, an assignee | Starts work (In progress) when needed and sets the phase |
| `KnownError` | `problems.manage`, an **active known error** (else 409 `known-error-required`) | As above |
| `OnHold` | An active on-hold reason (B1's list) | On hold; clocks pause when the reason says so |
| `Resolved` | `problems.manage`, resolution notes | Resolved; with `resolveIncidents` the open linked incidents and their open children are resolved with the notes, prefixed by the problem's number (they follow their own SLA clocks, notifications and search) |
| `Closed`, `Reopened` | `problems.manage`, from Resolved | As for incidents; resolved problems also close after the tenant's auto-close period |

Assignment (`PUT …/assignment`, a member of the group whose roles hold `problems.edit`) and "Route again" need `problems.manage`. Changes send If-Match with the ETag.

### Root-cause tools

Stored as structured data on `problems` (JSON objects, checked by the API, `PUT …/analysis`):

- **5 Whys:** a problem statement, up to seven *why* / *because* steps and the conclusion (2,000 characters each).
- **Fishbone:** the effect and up to 60 causes, each under a **category of the tenant's template** and optionally marked as the root cause. The template is a setting, Settings → Problems (`problems.settings`): six categories by default (people, process, technology, environment, suppliers, measurement) with bilingual names, up to 12, in order; categories used in an analysis can be made inactive but not removed. The page shows the fishbone as a list grouped by category, which screen-reader and keyboard users read like everyone else; there is no drawing to miss.
- **Root cause:** a free text field on the problem.
- **Timeline** (`GET …/timeline`): when the problem was raised, its known error recorded and it was resolved and closed, and for each linked incident when it was raised, linked, resolved and closed, oldest first. Changes are added with H3.

### Links

- **Incidents:** `problem_incidents` (an incident can belong to several problems), linked and unlinked by number with `problems.edit`, audited on the incident too (`problems.incident.linked`, `…unlinked`). `GET /incidents/{id}/problems` lists an incident's problems.
- **Configuration items:** G1's `ticket_configuration_items`, under `/problems/{id}/configuration-items` (`problems.read` to see, `problems.edit` to change).
- **Knowledge articles:** `problem_articles`, linked by id or by number.

## Known error database

One known error per problem (`known_errors`, `KE-000123`, prefix in Settings → Problems, numbers never reused): a title, symptoms and the **workaround**, each in English and Arabic (title and workaround need at least one language; missing ones are listed in Settings → Missing translations). Written with `problems.edit` (`PUT /problems/{id}/known-error`), retired and offered again with `problems.manage`. Searchable by number and by text in either language (`GET /known-errors?q=`) and in the search index (type `known-error`, readers with `problems.read`).

- **On matching incidents.** `GET /incidents/{id}/known-errors` (`incidents.read`) lists the active known errors whose problem is **linked to the incident**, **shares a configuration item** with it, or is in **the incident's category or a category above it**, each with why it matches, linked problems first. The staff incident page and the workspace show them; in the workspace **"Insert into reply"** pastes the workaround, in the language chosen, into the composer as a public reply draft that the agent reviews and sends; on the incident page it is copied.
- **Telling the agents.** When a known error is recorded (or offered again), the assignees of the problem's open incidents get `problem.known-error`, linked to the incident in the workspace.
- **In the portal (decision 9).** `POST /known-errors/{id}/article` (`problems.manage` and `knowledge.write`) makes an **external** knowledge article **draft** in both languages (symptoms and workaround under headings, the problem's category, the known error's number as keyword) linked to the problem. It is published only through the knowledge base's four-eyes review; once published, requesters find it in the portal's search and help pages like any external article.

## Problem detection (task H2)

Zelvo looks for problems worth investigating and puts them in the **problem managers' queue** (`/problems/suggestions`, `problems.manage`). It **never raises a problem by itself**: a problem manager accepts a suggestion (the problem is raised with the incidents linked) or dismisses it with a reason.

### Similar incidents (AI)

The AI feature **`problemDetection`** (Settings → AI → Features; off until set up, like every AI feature) extends E5's duplicate shortlist from "one new incident against the open ones of 14 days" to **groups of similar incidents across 30–90 days**:

1. **Zelvo groups the incidents first** (`SimilarIncidentGroups`): every incident of the period (default 60 days, setting 30–90) that is not linked to a problem is compared with every other by its English and Arabic words, with the same word forms as E5 (diacritics, alef, teh marbuta, alef maqsura and hamza forms, Arabic-Indic digits, `ال`, common words); two incidents are similar at a word overlap (Jaccard) of 0.3, plus 0.05 for the same category. The incident most others resemble seeds a group with them, then the next among those not yet taken, so each incident is in one group at most. Groups smaller than the minimum (default 5, setting 3–100) are dropped. At most 3,000 incidents are compared in one run.
2. **The AI judges each group** (`ProblemGroupJudge`) through the E4 gateway: up to 40 of its incidents (title and 300 characters of description, under references `i1`, `i2` …, requesters' names and email addresses masked, injection guards), asked whether they share **one underlying cause** rather than a similar topic, with a short title and reason in the incidents' language and the incidents that do not fit. Confidence below 60 % or a "no" makes the group **not confirmed**: it is kept but never shown. At most five groups are judged per run, largest first.
3. **Not suggested again.** A group mostly made of an earlier suggestion's incidents (half or more) is not judged again: new incidents (raised after it) join an open suggestion; dismissed and accepted ones stay decided; one the AI did not confirm is judged again only once the group has doubled. Incidents linked to a problem are left out of the comparison altogether.

### Trends (decision 10, no AI)

`ProblemTrendDetector` counts incidents over the **last 30 days** against the **average of the three 30-day periods before**, per:

| Subject | Counts |
| --- | --- |
| Category | Incidents of that category |
| Configuration item | Incidents linked to the item (G1's `ticket_configuration_items`), except business services |
| Business service | Incidents linked to the service, or to an item whose fault reaches it within three steps (G1's impact traversal, for the 200 busiest items) |

A subject with **at least 10 incidents** and **at least twice its usual level** (both settings: 2–10,000 and 1.1–10 times) is suggested, unless an **open problem already covers it** (same category, or linked to the item or service) or it was **suggested in the last 30 days** (whatever was decided); an open trend suggestion is brought up to date instead. A trend names its newest 100 incidents; accepting it links them and the trend's configuration item or service to the problem. The relationships are today's, also for the periods before.

### The job and "Look now"

`ProblemDetectionJobs` in the API runs every `Problems:Detection:Interval` (6 hours) for the tenants that have detection on (`zelvo_problem_detection_tenants()`, ids and slugs only), under an advisory lock per tenant so several API instances never run it twice, with the system as actor (`problems.detection`). The F2 figures it uses are plain aggregate queries on the tenant's connection, so row-level security applies. "Look now" (`POST /problem-suggestions/detect`) runs it at once for the problem manager (409 `detection-running` while a run is going on). When the AI feature is off or the monthly limit is reached, only trends are looked for, and the screens say why.

### Deciding

| Action | What happens |
| --- | --- |
| **Raise the problem** (`POST /problem-suggestions/{id}/accept`, `problems.manage` and `problems.edit`) | `POST /problems`' own steps: number, routing, SLA clocks, search; the incidents still there are linked (up to 100) and audited on each incident; title (the AI's, or "Rising incidents: …" / "ارتفاع في البلاغات: …" for trends, editable), the shared or trend category, the incidents' highest priority, the reason as description; a trend's item or service linked. The suggestion records the problem |
| **Dismiss** (`POST …/dismiss`, a reason of up to 1,000 characters) | Kept with the reason and who decided; the same group or trend is not suggested again |

One decision only: the row version makes a second one answer 409 `suggestion-decided` (If-Match is accepted too). Every suggestion made, accepted and dismissed is audited (`problems.suggestion.made`, `.accepted`, `.dismissed`; the reason by its length), "Look now" too (`problems.detection.requested`), and every AI call is in the AI log.

### Settings → Problems → Problem detection

`GET`/`PUT /settings/problems/detection` (`problems.manage`): similar incidents on or off, days compared (30–90, default 60), fewest similar incidents (3–100, default 5), trends on or off, fewest incidents in 30 days (default 10) and times the usual level (default 2). The screen links to Settings → AI → Features while the AI feature is not set up.

### Demo

Seeder `h2-problem-detection`: in each sample organisation ten incidents of one category over the last weeks (the customer happiness centre's printer for the government entity, in Arabic; Wi-Fi in the meeting rooms for the company, in English), six of them reporting the same fault, with a suggestion for those six as the AI would make it (the demo has AI off, so without an AI call), and the trend Zelvo's own detection finds in that category against F2's six months of history.

## Notifications

New events with English and formal Arabic templates (switchable and reworded in Settings → Notifications; staff audience):

| Event | To |
| --- | --- |
| `problem.assigned` | The new assignee, unless they assigned themselves |
| `problem.queued` | Members of the group, when a problem arrives there without an assignee |
| `problem.known-error` | Assignees of the problem's open incidents (one notification per incident) |
| `problem.resolved` | Assignees of the problem's incidents and the person who raised it |

Arabic uses مشكلة for problem, بلاغ for incident and خطأ معروف / حل مؤقت for known error and workaround.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `problems.read` | See problems, their analysis, links, timeline and notes, and the known error database | Agent, team lead, executive / auditor, tenant administrator |
| `problems.edit` | Raise problems, write the analysis, details and known errors, link incidents, items and articles, Assessment and Root-cause analysis, put on hold, notes | Agent, team lead, tenant administrator |
| `problems.manage` | The problem manager: assign and route, Known error, resolve (with the incidents), close, reopen, retire and publish known errors, Settings → Problems | Team lead, tenant administrator |

Like other Phase 2 modules, problem management is not a separately licensed module yet (for the product owner).

## API

| Endpoint | What |
| --- | --- |
| `POST /problems`, `GET /problems`, `GET`/`PATCH /problems/{id}` | Raise, list (stage, open, number, assignee, group, category, incidentId), read and change |
| `POST /problems/{id}/transitions`, `PUT …/assignment`, `POST …/route`, `GET /problems/assignees` | Stages and assignment |
| `PUT /problems/{id}/analysis` | 5 Whys and fishbone |
| `GET`/`POST /problems/{id}/comments` | Notes |
| `GET`/`POST /problems/{id}/incidents`, `DELETE …/incidents/{incidentId}`, `GET /incidents/{id}/problems` | Incident links |
| `GET`/`POST /problems/{id}/articles`, `PUT`/`DELETE …/articles/{articleId}` | Article links |
| `GET /problems/{id}/timeline` | Timeline |
| `GET`/`PUT`/`DELETE /problems/{id}/configuration-items…` | Configuration items (G1) |
| `PUT /problems/{id}/known-error`, `GET /known-errors`, `GET /known-errors/{id}`, `POST …/retire`, `…/activate`, `…/article`, `GET /incidents/{id}/known-errors` | Known errors |
| `GET`/`PUT /settings/problems` | Settings → Problems |
| `GET /problem-suggestions` (status, kind), `GET /problem-suggestions/{id}`, `POST …/accept`, `POST …/dismiss`, `POST /problem-suggestions/detect` | Suggested problems (H2, `problems.manage`) |
| `GET`/`PUT /settings/problems/detection` | Settings → Problems → Problem detection (H2) |

Every change is audited by the audit log's change capture; links and the article draft also by name.

## Demo

Seeder `h1-problems`: in each sample organisation a problem raised from the outage incidents that G1 linked to the CMDB (the e-services portal for the government entity, in Arabic; email for the company, in English), assigned to the team lead, at the Known error stage with its 5 Whys, fishbone, the configuration items, a note and a known error with a workaround in both languages, so the agent sees it on those incidents; and a second problem from two other incidents, new in the group's queue.

## Not in H1 and H2

- AI suggestions of problems from similar incidents and proactive problems from trends came with H2 (above). No notification tells problem managers about new suggestions; the count shows on `/problems` (for the product owner, if one is wanted).
- Embeddings (pgvector) for similar incidents: E5's word forms and the AI's judgement were enough, as for duplicates.
- Changes on the timeline and links from problems to changes: H3.
- A drawn fishbone diagram: the list grouped by category shows the same and is accessible; a drawing can follow if the product owner wants one.
