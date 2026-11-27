# Change management (tasks H3, H4 and H5, module 17)

Covers module 17 of `docs/requirements.md`. **H3** is the change core: the record type `Change` with standard, normal and emergency changes, each with its own approval path; the risk and impact questionnaire; affected configuration items; implementation, test and back-out plans; tasks for each team; the planned window, the change calendar, conflict detection and blackout periods; the post-implementation review; links to problems and incidents. **H4** adds the change advisory board and **H5** the what-if simulation (sections below).

Product owner decisions of 2026-11-26 that apply:

- **Decision 6.** Who a change affects: "used by" relationships (users, departments, customer groups) and assets assigned to users (H5).
- **Decision 7.** The approval path is chosen by the risk level, by default low: the change manager; medium: the change advisory board; high: the board and an executive; emergency: the emergency board. It is editable. The predicted risk (H5) comes from statistics of past changes' outcomes; the AI only explains.
- **Decision 8.** Board votes are advice; the chair records the decision (H4).
- **Decision 11.** The change manager (`changes.manage`) and the board member (`changes.approve`) are permissions held by team leads and tenant administrators by default; the six personas stay.

## Code

| Piece | Where |
| --- | --- |
| Record type `Change` (`CHG-000123`) on the ticket core; tables `changes`, `change_templates`, `change_blackouts`, `change_links` (row-level security; migration `ChangeManagement`) | `src/api/Zelvo.Data/Changes`, `ZelvoDbContext.Changes.cs`, `Tickets/Ticket.cs` |
| Permissions, stages, search documents `change` | `src/api/Zelvo.Tickets/ChangeSearch.cs` |
| API: changes, risk, submission, moves, review, assignment, notes, tasks, links; calendar, conflicts and blackout periods; standard change templates; Settings → Changes | `src/api/Zelvo.Api/Changes` |
| Approval path (C3 processes of stage `ChangeAuthorisation`) | `ChangeApprovals.cs`, `Workflows/ApprovalService.cs` |
| Tasks (C4's `request_tasks` generalised) | `Catalog/Fulfilment.cs`, `Catalog/RequestTaskEndpoints.cs` |
| Notifications | `src/api/Zelvo.Notifications/ChangeNotificationSource.cs`, templates in `Texts/en.json` and `ar.json` |
| Demo seeder `h3-changes` | `src/api/Zelvo.Api/Demo/DemoChangesSeeder.cs` |
| Pages `/changes`, `/changes/new`, `/changes/[id]`, `/changes/calendar`, Settings → Changes; the header link; "Raise a change to fix it" on problem pages; the change link on approval pages | `src/web/src/app/[locale]/{changes,settings/changes}`, `src/web/src/components/changes` |

## Changes on the ticket core

A change is a ticket of record type `Change` with its details in `changes`. It shares numbers (prefix in Settings → Incidents → number prefixes, `CHG` by default; existing tenants get it), B2 routing (a standard change goes to its template's group, other changes are routed by the assignment rules), the conversation table (internal notes only) and the audit log's field history. The person who raises it stands in as requester.

- **No SLA clocks.** Changes run to their planned window, not to response and resolution targets; Settings → SLA policies does not offer them. They are left out of auto-close (an implemented change closes with its review) and of the portal (My tickets, portal search).
- **Types.** `Standard`: made from an active **standard change template** of the catalog (Settings → Changes → Standard changes: bilingual name and description, category, group, the three plans and up to 30 tasks), pre-approved and low risk by definition. `Normal`: assessed with the questionnaire; its risk level picks the approval path. `Emergency`: the emergency path whatever its risk.

### Stages

| Stage | Ticket state | Phase |
| --- | --- | --- |
| New | New, Assigned | Planning |
| Authorisation | New, Assigned | Authorisation (`AwaitingApproval`) |
| Scheduled | New, Assigned | Scheduled |
| Implementation | In progress | Implementation |
| Review | Resolved | — |
| Closed | Closed, with the closure Completed, Rejected or Cancelled | — |

| Endpoint | From | Needs | What happens |
| --- | --- | --- | --- |
| `POST …/submit` | New | `changes.edit`; a window, implementation and back-out plans; normal changes every active question answered | Standard: scheduled at once, **except in a blackout period** (409 `blackout`: nobody reviews a standard change). Normal and emergency: the approval path starts (below); with no level to ask, scheduled at once. Audited `changes.submitted` with the number of conflicts. |
| `transitions` `Replan` | Authorisation, Scheduled | `changes.edit` | The pending approval stops; back to planning (a new submission asks again) |
| `transitions` `StartImplementation` | Scheduled | `changes.edit`; an assignee (409 `assignee-required`) | In progress; the actual start is recorded and the first stage of tasks opens |
| `transitions` `Implemented` | Implementation | `changes.edit`; notes; every task done or cancelled (409 `tasks-open`) | Resolved with the notes; the review is due |
| `transitions` `Cancel` | New, Authorisation, Scheduled | `changes.edit`; a reason | The approval stops, tasks are cancelled; closed as Cancelled |
| `POST …/review` | Review | `changes.manage` | The **post-implementation review**: outcome `Successful`, `SuccessfulWithProblems`, `Failed` or `BackedOut` (notes needed unless successful); closed as Completed. H5 learns from the outcomes. |

The plans, window, risk and configuration items change only in stage New (409 `change-not-planning`); title, description and category until it is closed. Changes send If-Match with the ETag.

## Risk and approval paths

Settings → Changes (`changes.settings`, `changes.manage`; read with `changes.read`):

- **The questionnaire**: up to 20 questions, each with a bilingual text and 2–6 answers with a weight of 0–100; inactive questions are no longer asked, answers already given stay. Five questions by default (how many people it affects, whether it was done before, interruption, how easy it is to undo, testing).
- **Thresholds**: medium risk from a score of 5, high from 10 by default. `PUT /changes/{id}/risk` stores the answers, score and level; submission works them out again with the current settings.
- **Approval paths** for low, medium and high risk and for emergency changes, each up to five C3 approval levels (any or all approvers; no conditions). Approvers can be holders of a permission (new approver kind **`Permission`**, added to C3 for changes: `changes.manage` for the change manager, `changes.approve` for board members), named people, a group's members or team leads, or a role. The defaults follow decision 7: low → change managers; medium → board members; high → board members, then executives; emergency → board members (H4 replaces the board levels by the board and the emergency board). As in C3, the person who raised the change is never asked, and a level with nobody to ask goes to the workflow administrators.

The process is a C3 approval process of the new stage `ChangeAuthorisation` on the Elsa engine: approvers decide in My approvals or from the email (event `change.approval-requested`, with the risk level and window); the approval page links to the change, its plans and conflicts. Approved: the change is **Scheduled**. Rejected: it is **closed as Rejected** with the reason. Workflow rules' approval actions of this stage are added like other stages'.

## Configuration items, conflicts, calendar and blackout periods

- **Configuration items**: G1's `ticket_configuration_items` under `/changes/{id}/configuration-items` (`changes.read` to see, `changes.edit` to change), or given when the change is raised (up to 50).
- **Conflicts** (`ChangeConflicts`): another open change (not reviewed or closed) on one of the same items in an overlapping window, and **blackout periods** in the window for the whole tenant or for an item the change touches **directly or through what depends on it** (G1's impact traversal, three steps: a blackout on a business service covers the servers it runs on). Shown on the change (`conflicts` of `GET /changes/{id}`) before submission and to approvers; `POST /changes/conflicts` checks a window before saving.
- **Blackout periods** (`/change-blackouts`, `changes.manage`; Settings → Changes): bilingual name, start and end, optionally one configuration item. Audited.
- **The change calendar** (`GET /changes/calendar?from=&to=`, at most 400 days; page `/changes/calendar` by month): changes by planned window (rejected and cancelled ones left out), each marked when it conflicts, and the period's blackouts. A table, so keyboard and screen-reader users read it like everyone else.

## Tasks

C4's task model (`request_tasks`) is generalised to changes: tasks come from the standard template or are added on the change (`POST /changes/{id}/tasks`: bilingual name, group, target minutes, together with the last task or after it), **wait until implementation starts**, then open stage by stage on their groups' calendars. They are worked on from My tasks (`/request-tasks`, which now lists the tasks of changes for holders of `changes.read`; starting and completing need `changes.edit`, giving them to people `changes.manage` or the group's team lead). Unlike a service request, a change whose last task is done is **not** resolved by itself: a person marks it implemented. Tasks not opened yet can be removed.

## Links

`change_links`: a problem the change **fixes** or an incident it **caused**, linked by number with `changes.edit` and audited on both tickets. `GET /problems/{id}/changes`, `GET /incidents/{id}/changes`. The **H1 timeline** of a problem now shows the changes that fix it (raised, linked, approved and scheduled, implemented, closed). Problem pages offer "Raise a change to fix it" (`/changes/new?problem=PRB-…`).

## Notifications

New events with English and formal Arabic templates (staff audience; switchable and reworded in Settings → Notifications):

| Event | To |
| --- | --- |
| `change.assigned` | The new assignee, unless they assigned themselves |
| `change.queued` | Members of the group, when a change arrives there without an assignee |
| `change.approval-requested` | Each approver asked (instead of C3's `approval.requested`, which names a service) |
| `change.approved` | The person who raised it and the assignee, when it is scheduled |
| `change.rejected` | The same, with the reason |
| `change.implemented` | The person who raised it and the group's team leads: the review is due |

Placeholders `change.start`, `change.end` (in the recipient's calendar and time zone) and `change.risk`. Arabic uses تغيير for change, اعتماد for approval and مراجعة ما بعد التنفيذ for the post-implementation review.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `changes.read` | See changes, plans, risk, tasks, links, the calendar and Settings → Changes | Agent, team lead, executive / auditor, tenant administrator |
| `changes.edit` | Raise and plan changes, answer the questionnaire, submit, replan, implement, cancel; tasks, links, notes | Agent, team lead, tenant administrator |
| `changes.manage` | The change manager: assign and route, the post-implementation review, Settings → Changes (questionnaire, paths, blackouts, standard changes). **Never for integrations** (for approval), like `workflows.manage` | Team lead, tenant administrator |
| `changes.approve` | A board member: asked on the board's approval levels | Team lead, tenant administrator |

Like other Phase 2 modules, change management is not a separately licensed module yet (for the product owner).

## Demo

Seeder `h3-changes` (after `h1-problems`): two standard changes (a scheduled restart of an application server, monthly desktop patches); a year-end blackout for everyone and one on the main business service; the **fix of the H1 problem** (normal, medium risk, scheduled on the outage's items, tasks for two teams, linked to the problem); a **high-risk normal change** planned and assessed, ready to submit to the board; an **emergency change** implemented and waiting for its review; and three past changes with their review outcomes (backed out, successful, a successful standard change) for H5.

## Not in H3

- The change advisory board, its meetings and the emergency board: H4.
- The what-if simulation: H5.
- SLA clocks for changes (they run to their window).
