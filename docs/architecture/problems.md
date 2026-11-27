# Problem management (task H1, module 16)

Covers the core of module 16 of `docs/requirements.md`: problems raised from one or many incidents or by hand, the stages New, Assessment, Root-cause analysis, Known error, Resolved and Closed, the root-cause tools (5 Whys, fishbone, the timeline of linked incidents), the known error database with workarounds that agents see on matching incidents and that reach the portal through the knowledge base, and links to incidents, configuration items and knowledge articles. AI suggestions and trend detection come with H2; changes join the timeline with H3.

Product owner decisions of 2026-11-26 that apply:

- **Decision 9.** Known errors reach the portal as **external knowledge articles through D1's four-eyes review**: Zelvo makes the draft, a publisher other than its author approves it.
- **Decision 11.** The **problem manager is a permission**, `problems.manage`, held by team leads and tenant administrators by default; the six personas stay.

## Code

| Piece | Where |
| --- | --- |
| Record type `Problem` (`PRB-000123`) on the ticket core; tables `problems`, `known_errors`, `known_error_number_sequences`, `problem_incidents`, `problem_articles` (row-level security; migration `ProblemManagement`) | `src/api/Zelvo.Data/Problems`, `ZelvoDbContext.Problems.cs`, `Tickets/Ticket.cs` |
| Permissions, stages, search documents `problem` and `known-error` | `src/api/Zelvo.Tickets/ProblemSearch.cs` |
| API: problems, stages, assignment, analysis, notes, incident and article links, timeline; known errors; Settings → Problems | `src/api/Zelvo.Api/Problems` |
| Notifications | `src/api/Zelvo.Notifications/ProblemNotificationSource.cs`, templates in `Texts/en.json` and `ar.json` |
| Demo seeder `h1-problems` | `src/api/Zelvo.Api/Demo/DemoProblemsSeeder.cs` |
| Pages `/problems`, `/problems/new`, `/problems/[id]`, `/known-errors`, Settings → Problems; the known errors panel on staff incident pages and in the workspace; "Raise a problem" in the workspace's bulk actions | `src/web/src/app/[locale]/{problems,known-errors,settings/problems}`, `src/web/src/components/problems` |

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

Every change is audited by the audit log's change capture; links and the article draft also by name.

## Demo

Seeder `h1-problems`: in each sample organisation a problem raised from the outage incidents that G1 linked to the CMDB (the e-services portal for the government entity, in Arabic; email for the company, in English), assigned to the team lead, at the Known error stage with its 5 Whys, fishbone, the configuration items, a note and a known error with a workaround in both languages, so the agent sees it on those incidents; and a second problem from two other incidents, new in the group's queue.

## Not in H1

- AI suggestions of problems from similar incidents and proactive problems from trends: H2.
- Changes on the timeline and links from problems to changes: H3.
- A drawn fishbone diagram: the list grouped by category shows the same and is accessible; a drawing can follow if the product owner wants one.
