# Agent workspace (task B4)

Covers **module 5** (agent workspace) and **LANG-7** (agent and requester each in their own language) of `docs/requirements.md`. Product owner decisions of 2026-11-24:

1. **Live updates:** SignalR, through the web app's backend-for-frontend with Server-Sent Events and long polling, so the browser never holds a token. The approved Valkey backplane turned out not to be needed (see *Live updates*).
2. **Canned responses:** tenant-wide ones by holders of `responses.manage`, group ones by the group's team leads, personal ones by anyone who works on incidents.
3. **Saved views:** everyone saves their own; team leads share views with their groups and holders of `workspace.views.share` with everyone.
4. **Bulk actions:** assign, set group and agent, route again, change state, set impact and urgency, set category or location, and a public reply or internal note to each.

Code: `src/api/Zelvo.Data/Tickets/TicketComment.cs`, `src/api/Zelvo.Data/Workspace` (entities), `src/api/Zelvo.Data/Live` (live update interceptor), `src/api/Zelvo.Api/Workspace` (API, SignalR hub and listener), `src/api/Zelvo.Notifications` (`CommentNotificationSource`), `src/api/Zelvo.Api/Demo/DemoWorkspaceSeeder.cs`, `src/web/src/components/workspace`, `src/web/src/lib/workspace.ts`, `src/web/src/lib/live.ts` and the pages `workspace/[[...ticket]]` and `settings/canned-responses`.

## The screen

`/workspace` (staff with `incidents.read`) is one screen in three columns, and one at a time on small screens:

- **List:** a view picker (built-in views *My open incidents*, *My groups' queue*, *Unassigned*, *At risk*, *Breached*, *All open incidents*, and the saved views the person sees), filters (state, priority, assignee, group, SLA, sort, open only, major), a live SLA timer and the assignee per row, check boxes and the bulk bar, paging.
- **Open incident** (`/workspace/{id}`): number, title, live SLA timers, description, facts, the **conversation** with the composer, the work panel of B1–B3 (assignment, state, impact and urgency, category and location, parent), the major-incident timeline, attachments and the history.
- **Beside it:** the **customer** (name, email when the person's roles show it, language, customer groups, open and total incidents, the ten most recent other incidents, linked into the workspace) and the place of the **AI assistant**, which the AI features fill in task E5.

Choosing an incident, a view, a filter or a page changes the address (`/workspace/{id}?open=true&assignee=me&sort=due`) with Next.js soft navigation: the server renders the new data through the public API and the page never reloads; links can be shared and the back button works. Without filters in the address the view is *My open incidents*; an empty filter set is sent as `sort=created` (every incident, newest first).

**Keyboard shortcuts** (listed under "Keyboard shortcuts" and with `?`): `j` / `k` next and previous incident, `x` select the open incident for a bulk action, `r` write a public reply, `n` write an internal note, `a` go to the assignment and state actions, `/` go to views and filters, `?` show the shortcuts, `Esc` close the dialog. They do nothing while typing in a field or with Ctrl, Alt or Cmd held.

English and Arabic, right to left, WCAG 2.2 AA (axe checks in `tests/e2e/specs/workspace.spec.ts`); the page widens `main` for itself (`data-layout="wide"`).

## Conversation (public replies and internal notes)

| Table | Holds | Notes |
| --- | --- | --- |
| `ticket_comments` | Ticket, visibility (Public or Internal), body (plain text, up to 20,000 characters), language (`en`/`ar`), author, whether staff wrote it, the canned response it started from, time | Never changed: a trigger refuses updates (except the canned-response link being cleared when that response is removed). Audited with the body's length only (`[AuditLengthOnly]`) |
| `ticket_comment_mentions` | Comment, ticket, person mentioned | Audited |

- **Who writes:** staff with `incidents.edit` write public replies and internal notes; the requester writes public replies on their own incident (from "My incidents"). Everyone else gets 404, so ids cannot be probed. Closed incidents take no comments (409 `ticket-closed`). Integrations cannot write comments yet (they record the person).
- **Who reads:** staff with `incidents.read` see everything; the requester sees public replies only, without mentions or canned-response links.
- **Response clock (B3, decision 2026-11-24):** the first public reply by a staff member who is not the requester sets `tickets.responded_at` and stops the response clock, unless work had already started.
- **@mentions:** up to 20 active people whose roles hold `incidents.read`; the composer inserts `@Name` and sends their ids.
- **LANG-7:** each message stores the language it was written in and is shown with `lang` and its own direction. The composer writes in the agent's language by default, shows the requester's own language, and offers canned responses in either language. One-click AI translation comes with E5 (the button is there, turned off).

## Canned responses

`canned_responses`: scope (Personal, Group, Tenant), group or owner, bilingual title and wording (`{{requester.name}}`, `{{ticket.number}}`, `{{ticket.title}}`, `{{agent.name}}`, `{{tenant.name}}`; only these, checked like notification templates), active, audited. Using one (`POST /incidents/{id}/canned-responses/{responseId}/render` with a language) fills the placeholders for the incident on the server and says when the other language had to be used. Settings → Canned responses (anyone with `incidents.edit`) lists the responses the person may change, with English and Arabic side by side and missing languages shown.

## Saved views

`saved_views`: scope (Personal, Group, Tenant), owner, group, bilingual name, filters as checked JSON (the list endpoint's parameters only: `state`, `open`, `priority`, `major`, `assignee`, `group`, `category`, `location`, `requester`, `sla`, `sort`). Everyone keeps up to 50 personal views. Owners change their own views; group views also the group's team leads, tenant views also holders of `workspace.views.share`.

## Bulk actions

`POST /api/v1/incidents/bulk` takes 1–100 incident ids and one action. Each incident goes through the **same handler as the single change** (same permissions, field rules, life-cycle rules, SLA update, search document, audit entries and notifications) inside one transaction with a savepoint per incident: an incident that fails is rolled back to its savepoint and the others are saved. The answer lists every incident with its status and problem code (`ticket-closed`, `transition-not-allowed`, `validation-failed`, …), which the bulk bar shows by number. Needs `incidents.edit`, and `incidents.assign` for assign, assignment and route.

## Live updates

```
change saved ──► LiveUpdateInterceptor ──► SELECT pg_notify('zelvo_live', '{"tenant":…,"kind":"ticket","id":…}')
 (API or worker)      (after the save)          delivered by PostgreSQL only when the transaction commits
                                                                │
every API instance: LiveListener (LISTEN zelvo_live) ───────────┘
        │  SignalR hub /api/v1/live: groups staff:{tenant} (incidents.read) and user:{tenant}:{user}
        ▼
browser (one connection per page, @microsoft/signalr) ◄── /bff/api/v1/live (session token added, CSRF on POST)
        ticketChanged {ticketId} · refresh · notificationsChanged
```

- **What is announced:** changes of tickets, incidents, comments, SLA clocks and SLA alerts (to the tenant's staff), and changes of a person's notifications (to that person). More than 200 tickets in one save are announced as one `refresh`.
- **Ids only:** messages never carry ticket content or personal data. The browser loads what changed through the API, which applies permissions and field rules, so a person whose rights changed while connected still sees only what they may.
- **Why no backplane:** PostgreSQL delivers `NOTIFY` to every listening API instance and only after commit, so each instance forwards to its own connections and the worker (SLA timers, auto-close, delivered notifications) reaches browsers without a Valkey connection. The `Microsoft.AspNetCore.SignalR.StackExchangeRedis` package approved for this was therefore not added.
- **Transports:** Server-Sent Events, then long polling. WebSockets are not offered because the backend-for-frontend (a Next.js route handler) cannot forward them; it forwards the SignalR requests with the session's token, without the usual 10-second timeout, and ends them when the browser goes away. A connection closes when its token expires (`CloseOnAuthenticationExpiration`) and the browser reconnects with a fresh one. The hub accepts people only (integrations are refused) and takes no messages from clients beyond the handshake (4 KB limit).
- **In the browser:** the workspace gathers changes for 1.5 seconds and then loads the page's data again without a reload, only while the tab is visible; the composer keeps what is being typed. The header's notification bell follows `notificationsChanged` and polls every 5 minutes while live (every 30 seconds while not). The workspace shows whether updates are live.
- **SLA timers** count down in the browser every 30 seconds to the clock's due time (computed on the business calendar by B3), in the colours of the clock's status (at risk from the policy's escalation thresholds; breached when the due time passes, before the server confirms). Text and a symbol, not colour alone, carry the status.

## Notifications (B5 engine)

Two events, on by default, wording in `Texts/en.json` and `Texts/ar.json` (formal Arabic per the guidelines):

| Event | Recipients | Raised when |
| --- | --- | --- |
| `incident.replied` | The requester (email optional) | A staff member posts a public reply on their incident |
| `incident.mentioned` | The people mentioned (email optional) | Someone mentions them in a reply or note; links to the workspace |

Nobody is told about their own comment. Comment text in a notification is cut at 1,500 characters.

## API

All under `/api/v1`, OpenAPI tag "Agent workspace".

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET`, `POST /incidents/{id}/comments` | Staff; the requester for their own | The conversation, oldest first; write a reply or note |
| `GET /incidents/{id}/customer` | `incidents.read` | The requester beside the ticket; showing the email is audited as a view of personal data |
| `GET /incidents?requester=` | `incidents.read` | The list filtered by requester (new filter) |
| `GET`, `POST /canned-responses`, `PUT`, `DELETE /canned-responses/{id}`, `GET /canned-responses/placeholders` | `incidents.edit` | Responses to use (`?manage=true`: to change) |
| `POST /incidents/{id}/canned-responses/{responseId}/render` | `incidents.edit` | A response filled in for the incident |
| `GET`, `POST /workspace/views`, `PUT`, `DELETE /workspace/views/{id}` | `incidents.read` | Saved views |
| `POST /incidents/bulk` | `incidents.edit` (+ `incidents.assign`) | One action for up to 100 incidents |
| `/live` (SignalR) | Signed-in people | Live updates |

Problem codes: `canned-response-not-found`, `saved-view-not-found`, plus those of the single changes.

## Permissions

| Permission | Built-in roles | Gives |
| --- | --- | --- |
| `responses.manage` | Tenant administrator | Canned responses for the whole organisation (team leads keep their groups' without it) |
| `workspace.views.share` | Tenant administrator | Saved views shared with everyone (team leads share with their groups without it) |

All four new tables have forced row-level security (`EnableTenantIsolation`); the guard test covers them.

## Demo data

`DemoWorkspaceSeeder` (`b4-workspace`): two tenant-wide canned responses in English and Arabic, one for the service desk group and one of the agent's own; the team lead's "Service desk queue" view shared with the group and the agent's "High priority" view; on the first three open incidents the requester's message, a public reply where work has started and an internal note mentioning the team lead, in the organisation's main language.

## Tests

- Integration against PostgreSQL (`WorkspaceApiTests`): who reads and writes which comments, the audit log keeps lengths only, comments cannot be changed, closed incidents take none, the response clock, notifications of replies and mentions, canned responses at each scope and their rendering and fallback, removing a used response, saved views and their sharing rules and filter checks, the customer panel and its audit entry, bulk actions with mixed results and per-incident audit, live events sent on commit with ids only, the hub's transports and sign-in. `DemoModeTests` covers the seeder.
- Web component tests (`workspace.test.tsx`): conversation, composer (canned response, mentions, keyboard event, refused replies in Arabic), SLA countdown (left, overdue, paused, Arabic-Indic digits), bulk bar, views, canned responses, the shell's shortcuts and live refresh; and the end-to-end journey (`workspace.spec.ts`): reply, note by shortcut, the requester's answer arriving live, a bulk action, the shortcuts dialog, Settings → Canned responses, axe in English and Arabic.

## Not in B4

- One-click translation, summaries and suggested replies (E5; the AI assistant column and the translate button are in place).
- Attachments on a reply or note: files stay on the ticket's attachments panel (B1). A file meant for an internal note would be visible to the requester until the note is posted, so this needs its own design.
- A requester reply notifying the assignee (not among the approved events; the workspace shows it live). Suggested for the rule builder (C3) or a later decision.
- Comments written by integrations and AI agents through the MCP server.
- Live updates of the requester's own pages (they get the notification); live updates per ticket rather than per tenant for very large tenants (measured in F7).
