# Self-service portal (task D2, module 4)

Covers module 4 of `docs/requirements.md`:

- a search-first home ("How can we help?") across the catalog, the knowledge base and open tickets;
- My tickets with status tracking and a progress bar;
- responsive pages, and raising a ticket in three clicks or fewer, within 60 seconds.

Login by UAE PASS, single sign-on, email code and anonymous complaints already came with A2, D3 and C6. Tenant branding comes with F1.

Product owner decision (2026-11-24): **My approvals** comes with the approvals of task C3. My tickets says so until then.

## Code

| What | Where |
| --- | --- |
| Portal search and My tickets API | `src/api/Zelvo.Api/Portal/PortalEndpoints.cs` |
| Service requests in the search index (type `request`) | `src/api/Zelvo.Tickets/RequestSearch.cs` |
| Home page, My tickets, System status | `src/web/src/app/[locale]/{page.tsx,my-tickets,status}` |
| Search box and ticket cards | `src/web/src/components/portal` |

## API

### `GET /portal/search?q=`

The home page's search. It is open to visitors (through the web app's visitor paths), in English and Arabic with the A4 analysers. It returns at most five of each:

- **services** of the catalog the caller may see (C2 access rules; visitors see public ones), linked to `/catalog/{id}`;
- **articles** the caller may read (visitors: external articles when the tenant allows it), linked to `/help/articles/{id}`, or `/knowledge/{id}` for internal ones;
- **tickets**: only the caller's own tickets, found with their own user reader. This holds for staff too: role readers are not used, so an agent does not find everyone's incidents on the home page. Open ones come first, each with its progress and portal page.

### `GET /me/tickets?open=&type=`

"My tickets": every ticket the caller asked for, of every record type (incidents, service requests, complaints, inquiries, suggestions). The most recently changed come first, paged. Each ticket has:

- **progress**:
  - service requests use their service's own steps, with the current step as on the request page (C2);
  - every other ticket uses received → being handled → resolved → closed;
- **team**: the group that has it, never the person (product owner decision 2026-11-24, module 4's "who has it"). The ticket's own page shows the same team to its requester: incidents, service requests and customer voice records now give requesters `group`, never `assignee`.
- **path**: the portal page that opens it: `/my-incidents/{id}`, `/my-requests/{id}` or `/my-voice/{id}`.

## Search documents for service requests

Service requests are now indexed. Readers are the requester, whoever made the request for them, the assignee and roles with `requests.read`. They are indexed on:

- creation, every change and conversion from an inquiry;
- auto-close;
- the demo seeder;
- "Rebuild index" (`RequestSearchSource`).

Existing tenants find their older requests after a rebuild in Settings → Search.

## Web

- **Home (`/`).**
  - "How can we help?" with the same question in the other language. Results appear as the person types (300 ms), in three labelled groups, with a polite live count. When nothing is found, it offers to report a problem.
  - Three actions: report a problem, request a service, contact us. Raising an incident takes two clicks: the action, then Send.
  - The person's open tickets with progress bars, or a sign-in invitation for visitors (and "Follow an anonymous message" when the tenant allows anonymous messages).
  - Popular services (the catalog's top-level services, first six).
  - Today in both calendars, links to the help centre and system status, and the portal happiness meter (C6).
- **System status (`/status`).** The live service status of the starter welcome page moved here.
- **My tickets (`/my-tickets`).** Filters open/all and by record type, cards with a progress bar (role `progressbar` with a text value), step list, team and last change, pagination.
- **Header.** The product name links home. "My tickets" replaces My incidents and My requests; those pages remain and open from each card. "Contact us" stays.
- **Layout.** Every layout is a responsive grid with logical properties, and works in Arabic right to left.

## Tests

- `PortalApiTests`: My tickets across record types, open and type filters, progress steps, portal paths, visitors refused, an empty search refused.
- `PortalSearchTests`: services, articles and only the caller's own tickets; staff do not find others' tickets; visitors find public content only. They need the `zelvo/opensearch` image, like the other search tests, which CI builds.
- Web: `portal.test.tsx` (progress in both languages, search groups and paths, nothing found), visitor paths.
- End to end: `portal.spec.ts` (two clicks and under a minute to raise an incident, search, My tickets, accessibility in both languages on desktop and phone); `welcome.spec.ts` follows the status page.

## Left out

- **My approvals**: with C3.
- **Branding** (logo, colours, domain): with F1.
- **A step journey for incidents and customer voice records beyond the four generic steps**: service steps for requests come from C2 and C4.
