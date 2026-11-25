# Bundles and proactive services (task C5, module 2)

Covers module 2's **bundle services** and **proactive services** in `docs/requirements.md`:

- bundles: one combined form, a child request per service, mandatory and optional services, in sequence or in parallel, one tracking screen;
- proactive services: started by a date, a schedule or an event from another system through the API; the administrator chooses to notify and ask to confirm, or to start automatically.

Product owner decisions (2026-11-25):

- **Where dates come from.** A date answer on the person's completed request of another service (for example "Expiry date" on "Trade licence"), and dates another system sends through the API for a person.
- **Notify and confirm opens the form filled in.** The offer (email and in-app) opens the service's form with what Zelvo knows filled in. The person checks it, adds documents and submits, or declines. Offers expire after a number of days the administrator sets.

Notifications by SMS and WhatsApp come with E3; C5 notifies by email and in the portal (B5).

## Code

| Piece | Where |
| --- | --- |
| Table `proactive_runs`; `service_requests.is_bundle`, `bundle_id`, `bundle_stage`, `bundle_optional`, `hold`, `proactive_run_id`; `tickets.held`; channel `Proactive` (migration `BundlesProactive`) | `src/api/Zelvo.Data/Catalog/CatalogEntities.cs`, `Tickets/Ticket.cs` |
| The card's `kind`, `bundle` and `proactive` sections and their checks | `src/api/Zelvo.Api/Catalog/ServiceCard.cs`, `CatalogCore.cs` |
| Making requests (one service or a bundle), used by the portal, the API, MCP and proactive starts | `src/api/Zelvo.Api/Catalog/ServiceRequestCreator.cs` |
| Bundle progress | `src/api/Zelvo.Api/Catalog/Bundles.cs` |
| Proactive engine, its API and the catalog job | `src/api/Zelvo.Api/Catalog/Proactive.cs`, `ProactiveEndpoints.cs`, `CatalogJobs.cs` |
| Notification `proactive.offered` | `src/api/Zelvo.Notifications/NotificationEvents.cs`, `Texts/*.json` |
| Demo seeder `c5-bundles-proactive` | `src/api/Zelvo.Api/Demo/DemoBundlesSeeder.cs` |
| Bundle and proactive sections of the service editor; the combined form; the bundle's parts; offers on My tickets | `src/web/src/components/catalog/{BundleProactiveEditor,BundleRequestForm,ServiceOffers}.tsx`, `ServiceRequests.tsx` (`BundleParts`, `BundleLink`) |

## Bundles

`card.kind` is `Service` (default) or `Bundle`. A bundle lists its services in `card.bundle.items`:

| Field | What |
| --- | --- |
| `serviceId` | A single, internal, not retired service (not a bundle) |
| `required` | Mandatory (always requested) or optional (the requester chooses) |
| `parallelWithPrevious` | Starts together with the service before it instead of after it is complete |

The stage of each item follows the C4 task rule: an item "together with the one before" shares its stage, the others start a new one.

A bundle has no group, SLA targets, tasks, approvals, documents or fee of its own. Each service keeps its own; the editor hides those sections. It may have its own form, for questions about the whole bundle. At most 20 services.

### The combined form

`GET /catalog/published/{id}/bundle-form` returns the bundle's own form and, for each service the requester may request, its form (with the requester's defaults), documents and fee. Mandatory services the requester may not request (hidden, suspended, retired) make the bundle unavailable.

- The requester fills **one set of answers**. A field several forms share (same key) is asked once; each form takes the answers of its own fields.
- An answer that no form has is refused (`field-unknown`).
- Each service's documents are uploaded and checked as for a single request (`items.<code>.documents.<key>` on errors).
- Prerequisites are checked per service; a prerequisite that is another service of the same bundle counts as met.

`POST /service-requests` with the bundle's `serviceId`, `formVersionId` (its own form), `formAnswers` (all answers) and `items` (`{ serviceId, formVersionId, documents }` per service; optional services only when chosen). AI agents through MCP request bundles with their mandatory services only.

### What is made

- The **bundle request**: a service request (`REQ-…`) with `is_bundle`. It has no group, SLA clocks or tasks; it is not routed and workflow rules do not assign it. Requesters get one `request.received` for it.
- A **part** per service: a child request (`tickets.parent_id` and `service_requests.bundle_id` = the bundle) with its own form answers, documents, team, approvals (C3), SLA and tasks (C4).
  - Parts of the first stage start at once: approval before assignment, or released to the service desk.
  - Later parts are **held** (`tickets.held`, `service_requests.hold = Bundle`): not routed, clocks paused, not changed by hand (409 `request-held`).

### Moving on

When a part becomes resolved or closed, `BundleProgress` (bundle row locked, so parts finishing together start the next stage once):

1. starts the next stage when every part of the stages before it is complete (resolved or closed), as above;
2. keeps the bundle's state in step: In progress while any part is open; **Resolved when every part is complete**, with a note listing the parts in the requester's language (`request.bundle-done`), which sends `request.resolved`; Closed when every part is closed; Reopened when a part is reopened.

It runs after a part changes through the API (transitions, the last task, approval decisions and cancellation) and every minute in the catalog job (for changes made elsewhere, for example the worker's auto-close). The bundle itself cannot be moved by hand (409 `bundle-managed`).

### One tracking screen

`GET /service-requests/{id}` of a bundle has `isBundle` and `parts`: each part's number, service, state, whether it is optional, held or waiting for approval, its team and its step progress. A part has `bundle` (the bundle's id and number). My requests and My tickets list bundles, not their parts (`GET /service-requests?parts=false`; `/me/tickets` leaves parts out); the bundle's page shows them all.

## Proactive services

`card.proactive`:

| Field | What |
| --- | --- |
| `enabled` | On or off |
| `mode` | `Offer`: tell the person and ask them to confirm. `AutoStart`: start the request. |
| `offerDays` | Days an offer stays open (1–90, default 14) |
| `triggers` | 1 to 10, each with a camelCase `key` and a `kind` |

| Kind | Settings | Due |
| --- | --- | --- |
| `Date` | `sourceServiceId` and `sourceFieldKey` (a date field of that service's published form), or neither (dates come through the API only); `offsetDays` (−3650 to 3650, negative before) | The date plus the offset. From requests: each **completed** request (resolved at least once) of the source service whose date has not passed yet, one run per request and date. |
| `Schedule` | `startOn`, `every` (1–365) `unit` (Day, Week, Month, Year), `people` (groups, roles, departments, users) | Each occurrence from the first date (months keep the day, or the month's last day), for the active people named, at most 5,000. Occurrences before the version was published are skipped. |
| `Event` | — | When another system sends the event |

External services cannot be proactive. A bundle can (the demo's new joiner package).

### Runs

Each time a service is due for a person is a run (`proactive_runs`), unique per service, trigger, person and occurrence (the source request and date, the schedule's date, or `api:<reference>`), so the same date, occurrence or event never offers or starts a service twice.

| State | Meaning |
| --- | --- |
| Scheduled | Due later |
| Offered | The person was told (`proactive.offered`); the offer expires after `offerDays` |
| Started | The request was started automatically |
| Accepted | The person requested the service from the offer |
| Declined | The person declined |
| Expired | Not answered in time |
| Failed | Not offered: the person is not active, the service is not offered to them (hidden services stay hidden), or it is no longer proactive with this trigger. The reason is kept. |

When a run is due:

- **AutoStart** makes the request for the person (channel `Proactive`, no `created_by`), with the answers it has (from the source request's non-personal answers or the event, limited to the form's fields) and the person's profile defaults. When those do not pass the form's checks, or the service needs documents, the run becomes an **offer** instead, with the reason.
- **Offer** sends `proactive.offered` (in-app and email, in the person's language; formal Arabic default wording) linking to `/catalog/{service}/request?offer={run}`. The form opens with the answers filled in; the person submits with `offerId` (the run becomes Accepted and records the request) or declines.
- A suspended service waits until it is active again.

Every offer, start, event and decline is in the audit log (`catalog.proactive.*`); the requests are audited as any other.

### API

| Endpoint | What |
| --- | --- |
| `POST /proactive/events` | `{ service (id or code), trigger, personId or personEmail, reference, date?, answers? }`; needs `proactive.trigger` (new; tenant administrators; allowed for integrations such as the HR system). 201 with the run, or 200 with the existing run for a reference already sent. |
| `GET /proactive/runs?serviceId=&state=` | Runs, newest first (`catalog.manage`); shown in the service editor |
| `GET /me/offers`, `GET /me/offers/{id}` | The caller's open offers; one with its answers |
| `POST /me/offers/{id}/decline` | Decline |

### The catalog job

`CatalogJobs` runs in the API (requests are made there, with the embedded approval engine), every minute (`Catalog:Jobs:Interval`; `Catalog:Jobs:Enabled=false` turns it off, as the tests do). For each active tenant with active services (`zelvo_catalog_job_tenants()`), under a PostgreSQL advisory lock so one API instance works on a tenant at a time: discovers dates and schedule occurrences, processes due runs one at a time (row locked, `SKIP LOCKED`), expires offers and moves bundles on.

## Demo data

Seeder `c5-bundles-proactive` (order 180), for each sample organisation:

- **New joiner package** (`HR-JOINER`): New employee onboarding and system access together, then an optional monitor; offered when the HR system sends `newJoiner`;
- **Yearly access review** (`IT-REVIEW`): started automatically each year (from 15 January next year) for the service desk;
- an open offer of the package for the sample requester, shown on My tickets.

## Tests

- **Integration against PostgreSQL** (`BundleProactiveApiTests`): bundle problems; the combined form with a shared answer; parts per stage, held parts paused and not changed by hand; one "received"; the next stage starting when the first is done; My requests without parts; the bundle resolved with its note and notification; an event starting a request once (idempotent reference, permission, unknown trigger); a date on a completed request making an offer, filled answers, hidden from others, accepted with `offerId`, declined and expired offers; a schedule for a group's members, once per occurrence; latest occurrences of schedules.
- **Web** (`src/web/src/components/catalog/bundles.test.tsx`): stages, the bundle editor, shared questions asked once, the parts screen in Arabic, the proactive editor and runs, offers and declining.

## Not in C5

- SMS and WhatsApp offers (E3).
- Fees of bundles' services are paid per part (E8).
- Lookups of dates in other systems by Zelvo (E7 connectors); other systems send them.
