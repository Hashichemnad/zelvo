# Custom record types (task K2, module 20)

Covers the custom record types of module 20 of `docs/requirements.md`: administrators make their own record types, such as facilities or HR requests, on the same platform as incidents and service requests. Product owner decision 20 of 2026-11-26: record types live **on the ticket core** with their **states mapped onto the core's state categories**; apps that group them (with data lists and JSON packages) come with K3, and custom code with module 28 in Phase 3.

## Code

| Piece | Where |
| --- | --- |
| Record type `Custom` on the ticket core, `tickets.record_type_id`; tables `record_types` and `custom_records` (row-level security; migration `CustomRecordTypes`); `form_bindings`, `sla_policies` and `workflow_rules` gain `custom_record_type_id` | `src/api/Zelvo.Data/Records`, `ZelvoDbContext.Records.cs`, `Tickets/Ticket.cs` |
| States and their categories, per-type permissions, search documents `record`, the interceptor that keeps a record's state in step with the core | `src/api/Zelvo.Tickets/CustomRecords.cs` |
| API: Settings → Record types (`/record-types`) and the generic record endpoints (`/records/{type}`) | `src/api/Zelvo.Api/Records` |
| Notifications | `src/api/Zelvo.Notifications/RecordNotificationSource.cs`, templates in `Texts/en.json` and `ar.json` |
| Demo seeder `k2-record-types` | `src/api/Zelvo.Api/Demo/DemoRecordTypesSeeder.cs` |
| Pages `/records`, `/records/[type]`, `/records/[type]/new`, `/records/[type]/[id]`, Settings → Record types; the custom type pickers in Settings → SLA policies, Forms and Workflow rules; labels of per-type permissions in Settings → Roles | `src/web/src/app/[locale]/{records,settings/record-types}`, `src/web/src/components/records` |

## A record type

`record_types` (Settings → Record types, `recordtypes.manage`):

- **Key** (`facilities`; lower-case letters, digits and hyphens, never changes): in addresses (`/records/facilities`) and permissions.
- **Name and description** in English and Arabic (missing languages are listed in Settings → Missing translations).
- **Number prefix** (`FAC`): its own series `FAC-000123`, never reused; different from the built-in record types' prefixes (Settings → Incidents) and from every other custom type's (409 `prefix-taken`). A changed prefix applies to new numbers.
- **States**, 3 to 20 in order, each with a key, a bilingual name and a **state category** of the ticket core: `New`, `InProgress`, `OnHold`, `Resolved` or `Closed`. At least one New, one Resolved and one Closed state; new records start in the first New state. States in use keep their category and cannot be removed.
- **Portal entry:** requesters raise records of the type themselves; otherwise only staff log them.
- **Default priority** 1–5 of new records, for SLA targets.
- **Active:** inactive types keep their records but take no new ones.

At most 50 types per tenant.

## States on the ticket core

A record is a ticket of type `Custom` (with `record_type_id`) and a row in `custom_records` holding its **state of the type** and its priority. The ticket's own state follows the category:

| Category | Ticket state |
| --- | --- |
| New | New, or Assigned once someone has it |
| InProgress | In progress (Reopened when moved back from Resolved) |
| OnHold | On hold, with one of B1's on-hold reasons (clocks pause when the reason says so) |
| Resolved | Resolved, with resolution notes |
| Closed | Closed (final) |

`POST /records/{type}/{id}/transitions` takes a `stateKey`. A move within the same category (two "in progress" states such as *Scheduled* and *On site*) changes only the record's state; a move to another category goes through the ticket core's own checks (an assignee to start work, an on-hold reason, resolution notes, Closed only from Resolved). Staff with the type's edit permission move records; the requester reopens their own resolved record (to the first InProgress state).

Whenever the ticket's state changes without the record's state — assignment, auto-close of resolved records, approvals — `CustomRecordStateInterceptor` (every save of the API and the worker) moves the record to the **first state of the new category**. So auto-close, reports and SLA clocks work for every type without knowing its states.

## Everything else from the ticket core

- **Forms (C1):** Settings → Forms binds a form to a custom type and category (`form_bindings.custom_record_type_id`); `GET /forms/resolve?recordType=Custom&customRecordTypeId=` returns it and the record's answers are checked against it.
- **Routing (B2):** the assignment rules route new records (by category, location and language) and "Route again".
- **SLAs (B3):** Settings → SLA policies chooses the record type *Custom records* and, optionally, one custom type; the record's priority picks the targets.
- **Workflow rules and approvals (C3):** rules choose *Custom records* and optionally one type; rules on custom records may **ask for approval** when they are raised, like service requests: the record waits (clocks paused, no moves) until the approvers decide, then the assignment rules route it; a rejected one is closed with the reason.
- **Conversation (B4):** public replies and internal notes; a staff member's first public reply stops the response clock; the requester's answer moves a record waiting for them back to work.
- **Notifications (B5):** `record.received`, `record.replied`, `record.resolved` (the requester) and `record.assigned`, `record.queued` (staff), with the **type's own name** as `{{ticket.type}}` in the recipient's language (formal Arabic templates), switchable and reworded in Settings → Notifications.
- **Search:** documents `record`, read by the requester, the assignee and the roles with the type's read permission.
- **Portal:** records appear in **My tickets** with their type's name and open on the record page; `/records` lists the types a person may raise or read.
- **Dashboards (F2):** custom records count together as *Custom records* for people who see the whole tenant's reports (`reports.all`).

## Permissions

Each type makes three permissions: `records.<key>.read` (see every record of the type), `records.<key>.edit` (work on them, log them for others, set the priority, internal notes) and `records.<key>.assign` (groups and assignees). Making a type grants all three to the tenant administrators and to the roles named in `grantRoles` (by default agents and team leads). They are granted and removed in Settings → Roles like the others (`GET /permissions` lists them with the type's name; permissions of types that do not exist are refused) and may be given to integrations as scopes. Requesters always see and answer their own records; others get 404.

`recordtypes.manage` (tenant administrators; **not for integrations**, for the product owner's approval, as making a type grants permissions to roles) makes and changes types.

## API

| Endpoint | What |
| --- | --- |
| `GET`, `POST /record-types`, `GET`, `PATCH /record-types/{key}` | Types; `access` says what the caller may do (`read`, `edit`, `assign`, `raise`) |
| `POST /records/{type}`, `GET /records/{type}` | Raise one (requester or staff for someone else), list (all with read, else own; filters state, open, number, assignee, group) |
| `GET`, `PATCH /records/{type}/{id}`, `GET …/form`, `GET …/attachments/{id}/content` | One record (requesters see the team, never the agent, and no clocks), changes, answers and files |
| `POST …/transitions`, `PUT …/assignment`, `POST …/route`, `GET /records/{type}/assignees` | States and assignment |
| `GET`, `POST …/comments` | Conversation |

The record endpoints are generic: documented once in the OpenAPI document with the type's key as a path parameter.

## Demo

Seeder `k2-record-types`: in each sample organisation the type *Facilities request* / *طلب مرافق* (`FAC`) with the states New, Scheduled, On site, Waiting for parts, Done and Closed, a portal entry, its permissions for agents, team leads and administrators, an SLA policy of its own, and three records from the sample requester: new, on site with the agent, and done.

## Not in K2

- Apps grouping record types with data lists, menus and JSON packages: K3.
- Dashboards per custom type and per-type read permissions in dashboards (custom records count together for `reports.all`).
- Retention periods per custom type (F5) and custom records in the workspace (the generic staff pages are used instead).
- Custom code (module 28, Phase 3).
