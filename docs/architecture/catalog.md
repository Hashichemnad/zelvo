# Service catalog builder (task C2, module 2)

Covers the **catalog builder** part of module 2 in `docs/requirements.md`, and LANG-5 for catalog items:

- catalogs, categories and the main service → sub-service hierarchy;
- the full service card;
- the lifecycle (draft → retired), copy and templates;
- access control per service;
- request on behalf;
- the service FAQ;
- Excel and API import and export.

It also adds basic **service requests**, so that services can be requested (and requested on behalf of someone) now. Fulfilment comes with C4: the initial group, service SLAs, fulfilment tasks and the step journey.

Product owner decisions (2026-11-24):

- **Basic requests in C2.** Requesting a service creates a service request ticket (`REQ-000123`) with the service's form and required documents, for yourself or on behalf of someone. The B2 assignment rules route it, and the B3 SLA clocks start. C4 adds fulfilment.
- **Four-eyes, versioned.** As for knowledge articles, a publisher other than the author approves each version. A change to an active service is a new version, reviewed while requesters keep seeing the active one.
- **Per-catalog editors.**
  - `catalog.manage` (tenant administrators) covers the whole catalog.
  - Each catalog can name editors (users or groups) who write and submit that catalog's services only.
  - `catalog.publish` approves (tenant administrators by default).
- **Shared categories.** Services use the B2 category tree shared by all record types, several per service. Catalogs group services per department or audience.
- **Templates, built in and saved.** Zelvo ships ready-made bilingual templates for common services. Administrators can also save any service as a tenant template.
- **Visitors browse; they sign in to request.** Visitors who are not signed in read public services, including steps, fees, documents and the FAQ. They sign in to request (email code, UAE PASS). Anonymous requests come with C6, as C1 decided.

## Code

| Piece | Where |
| --- | --- |
| Tables `service_catalogs`, `catalog_editors`, `catalog_services`, `catalog_service_versions`, `catalog_service_templates`, `service_requests` (row-level security; migration `Catalog`); `ticket_attachments.service_id`; record type `ServiceRequest` | `src/api/Zelvo.Data/Catalog`, `Tickets` |
| The card, its checks, access, search documents, missing translations | `src/api/Zelvo.Api/Catalog/ServiceCard.cs`, `CatalogCore.cs` |
| API: catalogs, services, templates, the catalog for requesters, service requests, bulk type | `src/api/Zelvo.Api/Catalog/*Endpoints.cs`, `ServiceTemplates.cs`, `ServicesBulkType.cs` |
| Demo seeder `c2-catalog` | `src/api/Zelvo.Api/Demo/DemoServiceCatalogSeeder.cs` |
| Settings → Catalog, the service editor, the catalog and service pages, my requests, staff requests | `src/web/src/app/[locale]/{settings/catalog,catalog,my-requests,requests}`, `src/web/src/components/catalog` |

## Catalogs

A catalog (`/api/v1/catalog/catalogs`) has:

- a unique key (`it-services`);
- a bilingual name and description;
- an owning department (from C1's Settings → Departments) and an order;
- whether it is active;
- its **editors**, each a user or a support group.

A service can be in several catalogs. Requesters see only active catalogs. A service that is in catalogs is listed only while one of them is active. Catalogs that services are in cannot be deleted; make them inactive instead. Only `catalog.manage` changes catalogs.

## Services and versions

A service has a unique **code** (`IT-LAPTOP`; capital letters, digits and hyphens) and a state:

```
Draft ──submit──▶ Under review ──approve──▶ Active ⇄ Suspended (message)
  ▲                    │                       │
  └────send back───────┘                       └──retire──▶ Retired ──reopen──▶ new draft
```

- The **card** lives in versions (`catalog_service_versions.content`, JSON). The published version is what requesters see; changes are made in a **working version**.
  - On a published service, the first save starts version 2, 3 and so on.
  - Requesters keep the published card until a publisher approves the new version. The old version becomes *Superseded* and stays as history.
- **Four-eyes:** the reviewer holds `catalog.publish` and neither started nor last changed the version (`CatalogAccess.MayReview`). Sending back needs a comment.
- **Problems** stop a card from being submitted or approved, like form problems (409 `service-has-problems` with the list). They are listed with the service while it is a draft:
  - names, lengths and counts;
  - HTTPS-only external links;
  - fees above zero with two decimals;
  - document keys, and file types within the tenant's allowlist;
  - a restricted service that names nobody;
  - everything the card refers to existing (catalogs, active categories, departments, the owner, groups, roles, locations, users, the form, files, related and prerequisite services);
  - a main service that is not below the service itself (any depth, no circles);
  - the form being published.
- **Suspend** (with a bilingual message requesters see), **resume** and **retire** need `catalog.publish`.
  - Retiring discards a pending working version. Requests already made keep the service.
  - **Reopen** starts a new draft from a retired service's last card.
- **Discard** removes the working version. A service that was never published is deleted with it, as is **Delete**.
  - Codes of published services never change, because requests refer to them.
- **Copy** makes a new draft with a new code from the working (or published) card, without files.
- **Concurrency:** the ETag is the version of the working version, or of the published one when there is no working version. If-Match is checked when sent (412 `version-mismatch`).
- **Audit:** every change is audited automatically. The card's JSON is long, so only its length is recorded (`[AuditLengthOnly]`). Named entries are written too: `catalog.service.submitted`, `published`, `sent-back`, `discarded`, `suspended`, `resumed`, `retired`, `reopened`, `deleted`, `copied`, `catalog.template.saved`, `catalog.file.added` and `removed`.

The service row keeps copies of what lists, the hierarchy and access need: name, main service, catalogs and categories. They come from the published card, or from the working card until the service is first published.

## The service card

Every field of module 2's service card, each in English and Arabic where it holds text:

| Field | In the card |
| --- | --- |
| Name and code | `name`; the code is on the service |
| Description | `summary` (one line for lists) and `description` (Markdown, never HTML) |
| Status | the service's state and suspension message |
| Category | `categoryIds` (several, the shared tree) |
| Main service | `parentId` (any depth) |
| Department and owner | `departmentId`, `ownerId` (an active user) |
| Service type | `type` Internal or External; external services have `externalUrl` (HTTPS) and no form or documents |
| Required documents | `documents`: key, name, description, required or optional, file types, largest size, a downloadable template (a file of the service) |
| Service steps | `steps` in order, each mapped to an internal stage (Submitted, Assigned, In progress, On hold, Resolved, Closed) |
| Service fee | `fee`: paid, amount in AED, notes (payments come with module 15) |
| Number of days | `days`, working days (C4 uses it as the default completion SLA) |
| Delivery channels | `channels`: Portal, Mobile, WhatsApp, Chat, Email, Call centre, Service centre |
| Target audience | `audiences`: Individuals, Businesses, Government entities, Employees, Visitors |
| Service partners | `partners`: a department, or a named entity or vendor |
| Related services | `relatedServiceIds` ("You may also need") and `prerequisiteServiceIds` (completed first) |
| Keywords and icon | `keywords`, `icon` (Zelvo's set) or `imageFileId` |
| FAQ | `faq`: questions and answers |
| Form | `formId`: a C1 form the requester fills |
| Access | `access` (below) and `onBehalfRoleKeys` |

**Files.** A service's image and document templates are uploaded to the service (`POST /catalog/services/{id}/files`).

- They are stored like attachments (`ticket_attachments.service_id`): same size limit and allowlist, content checked against the type, scanned by ClamAV.
- Requesters download them only through a service they may see, and only the files its published card uses.

## Access control

`access.mode` is one of:

- **Everyone** signed in (the default);
- **Public**: also visitors who are not signed in;
- **Restricted**: only the support groups, roles, departments, locations (and the locations below them) and named users listed.

A hidden service is left out of the catalog, search and the API for everyone outside the allowed set: it answers 404, so ids cannot be probed. MCP catalog tools (C4) and the virtual agent (it answers from knowledge articles in E5; catalog services and FAQs come with module 23) go through the same check (`CatalogAccess.MaySee`, `PublishedServices`).

- Each service's access is its own. A sub-service whose main service a person cannot see is shown to them at the top level.
- **Search (A4):** services are the `service` document type, holding their published card only (name, summary, description, FAQ, code and keywords). Readers follow the access rules: `public`, `users`, or `group:`, `role:`, `department:` (new), `location:` (new) and `user:` readers.
  - Callers are now readers of their groups, their department and their location with every location above it (`CatalogScope.Readers`).
  - Integrations still find public documents only, as before.
- **On behalf:** people holding one of the service's `onBehalfRoleKeys` name another active person as the requester. The service must be offered to that person too. Nobody may request on behalf of others unless the service names a role.

## Templates

`GET /catalog/templates` lists Zelvo's ready-made templates (`builtin:new-laptop`, `software-access`, `password-reset`, `salary-certificate`, `permit-renewal`, `trade-licence`, `facility-booking`, `general-inquiry`) and the tenant's saved ones.

- Every ready-made template is complete in English and formal Arabic and has no problems (tested).
- **Save as template** (`catalog.manage`) keeps the card without its code, files, main service, related services, named users and owner.
- `POST /catalog/services` with `templateId` makes a new draft from either kind. Changing a template never changes services made from it.

## The catalog for requesters

- `GET /api/v1/catalog/published` lists the active catalogs and the services the caller may see. Filters: catalog, category (or below), main service, top level, audience and text.
- `GET /api/v1/catalog/published/{id}` returns the service page:
  - description, documents with templates, steps, fee, days, channels, audience and partners with names;
  - the FAQ;
  - the main service and sub-services;
  - related services and prerequisites the caller may see, with whether the caller completed each;
  - whether the caller may request it (also on behalf of someone), or must sign in first.
- Suspended services are listed with their message and cannot be requested. Retired and draft services are not shown.
- Visitors reach these paths and the service files through the web app's visitor proxy paths.

## Service requests

`POST /api/v1/service-requests` takes:

- `serviceId`;
- optionally `requesterId` (on behalf) and `title` (the service's name in the requester's language by default);
- `description`;
- the form's `formVersionId` and `formAnswers`;
- `documents`: a map from document key to file ids.

Files are uploaded first with C1's `/form-uploads`, with field `document` followed by the key with a capital first letter (key `passportCopy` → field `documentPassportCopy`). The server checks, in order:

1. The service is listed, active and internal, and visible to the caller. Otherwise 404, `service-suspended` or `service-external`.
2. On behalf: the caller's role is allowed and the requester may see the service.
3. **Prerequisites:** the requester has a resolved or closed request of each one. Otherwise 409 `prerequisite-missing` with the codes.
4. The **form answers**, with C1's rules, defaults and checks. 409 `form-changed` when the version moved on.
5. The **documents:** required ones present; each file the caller's own unsent upload for that document, not infected, of the document's types and size. Otherwise 400 with `documentErrors`.

The ticket is created as follows:

- type `ServiceRequest`, number `REQ-000123` (prefix in Settings → Incidents' number prefixes; existing tenants get `REQ`), channel Portal;
- category: the service's first category; location: the requester's;
- placed by the B2 assignment rules, with SLA clocks from the B3 policies for service requests.

`service_requests` keeps the service and the version of its card, so the request keeps its steps.

| Endpoint | What |
| --- | --- |
| `GET /service-requests` | Own requests (also made for you), or all with `requests.read`; filters mine, open, state, service, number, assignee, group |
| `GET /service-requests/{id}` | The request with its service, steps and current step (the last step whose stage the ticket reached). Requesters see the team (group) that has it, never the assignee (decision 2026-11-24), nor the SLA clocks |
| `GET /service-requests/{id}/form`, `/attachments`, `/attachments/{id}/content` | Answers (personal answers' staff views audited), documents with their document key, downloads once clean (audited) |
| `POST /service-requests/{id}/transitions` | States of the ticket core; requesters may reopen their resolved requests |
| `PUT /service-requests/{id}/assignment`, `POST …/route` | Group and assignee (`requests.assign`) |

## Import and export

The E1 bulk framework gets the type `services` (`/api/v1/bulk/services/export|import|template`, CSV and Excel; needs `catalog.manage`).

- One row per service, with simple columns: names, catalogs by key, categories by path, main service by code, department by name, owner by email, form by key, fee, days, channels, audience, keywords, icon, access mode and roles, on-behalf roles, related and prerequisite codes.
- Documents, steps, FAQ and partners are JSON cells in the exported shape.
- An import **never publishes**. New codes become drafts; existing services get a working version (or their working version is updated) that goes through review. Services in review are refused.
- Nothing changes while any row has a problem. Rows may refer to services created in the same file.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `catalog.manage` | Catalogs and their editors, every service, templates, bulk import and export | Tenant administrator |
| `catalog.publish` | Approve or send back versions written by others; suspend, resume and retire | Tenant administrator |
| `requests.read` | See every service request | Agent, team lead, tenant administrator, executive |
| `requests.edit` | Work on service requests (states) | Agent, team lead, tenant administrator |
| `requests.assign` | Assign service requests | Agent, team lead, tenant administrator |

Catalog editors need none of these for their catalogs' services. Requesting needs nothing but seeing the service.

## Demo data

Seeder `c2-catalog` (order 150) gives each sample organisation:

- two catalogs: a public one, and an employee one edited by the team lead;
- the "IT services" main service with sub-services: a laptop service with a form, an optional document, on-behalf roles and a vendor partner; software access; password reset; a suspended "Extra monitor";
- a salary certificate (with an FAQ and a related service), a salary advance only the finance department sees, and room booking;
- public services:
  - for the government entity, a trade name reservation (paid), a trade licence that needs it, a permit renewal with documents and fee, and an external visa service;
  - for the company, a quotation and an external support portal;
- a draft and a service waiting for review;
- a saved template;
- two requests by the sample requester, one made for them by the team lead.

## Tests

- **Integration against PostgreSQL** (`CatalogApiTests`):
  - four-eyes publishing and versions;
  - suspend, resume, retire and reopen;
  - problems, codes and hierarchy cycles;
  - hidden services for each kind of access rule, visitors and search readers;
  - catalog editors;
  - requests with form answers and required documents, and on behalf;
  - prerequisites and external services;
  - copy and templates (every ready-made template has no problems);
  - bulk export and import;
  - missing translations;
  - tenant isolation.
- **Demo** (`DemoModeTests`): the seeder runs under `migrate`, every sample card has no problems, and reset still covers every table.
- **Web and end to end:** see `src/web/src/components/catalog/*.test.tsx` and `tests/e2e/specs/catalog.spec.ts`.

## Not in C2

- Fulfilment (C4): the initial group and overrides by answer, ownership and completion SLAs per service, fulfilment tasks, the step journey moved by tasks.
- Catalog request tools of the MCP server (C4), approvals (C3), bundles and proactive services (C5), payments (E8).
- Notifications for service requests (with C3 and C4 rules; B5's events are for incidents).
- Anonymous requests by visitors (C6).
- Service requests in search, and in the portal's search-first home (D2).
- Field-level rules for service requests (the requester's view is fixed, as for incidents).
- A sub-service does not inherit its main service's access. Each service's access is its own.
