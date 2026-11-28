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

- SLA clocks for changes (they run to their window).

# The change advisory board (task H4)

Decision 8: **votes are advice; the chair records the decision**, and the board's outcome goes **through C3's approval service**.

| Piece | Where |
| --- | --- |
| Tables `change_boards`, `change_board_members`, `cab_meetings`, `cab_meeting_members`, `cab_agenda_items`, `cab_votes` (row-level security; migration `ChangeAdvisoryBoard`) | `src/api/Zelvo.Data/Changes/BoardEntities.cs`, `ZelvoDbContext.Changes.cs` |
| Board approver kinds `ChangeBoard` and `EmergencyBoard`; the board kept on planned levels | `Zelvo.Workflows/Rules/RuleDocuments.cs`, `Approvals/ApprovalPlanner.cs` |
| API: boards, meetings, agenda, votes, decisions, minutes, a change's board history | `src/api/Zelvo.Api/Changes/ChangeBoardEndpoints.cs` |
| Invitations with a calendar file (`.ics`) | `src/api/Zelvo.Notifications/MeetingInvites.cs`, `NotificationJobs.SendAsync` |
| Pages `/changes/board`, `/changes/board/[id]`, the board history on change pages, Settings → Changes → Boards | `src/web/src/components/changes/ChangeBoard.tsx` |

## Boards

Each tenant has two boards (`/change-boards`, made with their default names when first read; changed with `changes.manage` in Settings → Changes → Boards, audited):

- **The change advisory board**: bilingual name, a **chair** and a **deputy chair**, and its members.
- **The emergency board**: a smaller membership and the **hours within which it decides** (1–168, 4 by default).

Members and chairs are active people whose roles hold `changes.approve` (decision 11: the board member is a permission).

**On the approval paths**, two new C3 approver kinds name the boards: `ChangeBoard` asks the advisory board's **chair and deputy** (the change managers while it has no chair), and `EmergencyBoard` asks the **emergency board's members** (holders of `changes.approve` while it has none). The default paths now use them: medium risk → the board; high → the board, then an executive; emergency → the emergency board. The planned level records which board it is (`PlannedLevel.Board`), so Zelvo knows a change **waits for the board**.

## Meetings

`/cab-meetings` (`changes.read` to see; planning and changing them needs the change manager, `changes.manage`, or the board's chair or deputy):

- **Planning** (`POST`): bilingual title, start and end, room or online address; the chair and members default to the board's (the chair is always a member). **The agenda is built automatically** from the changes waiting at a board level that no other planned meeting has on its agenda yet, soonest window first; `…/agenda/refresh` adds those waiting since and removes undecided ones that no longer wait; an undecided change can be taken off. `GET /cab-meetings/waiting` lists the changes waiting for the board.
- **Invitations**: members and the chair get `cab.invited` (in the app and by email, which cannot be turned off) with a **calendar file** (`meeting.ics`, iCalendar `METHOD:REQUEST`, the meeting's id as UID, a sequence that grows with each change so calendars update the event, the title in the recipient's language and the agenda's numbers in the description). Changing the time, place or members sends it again; members taken off and everyone when the meeting is called off get `cab.cancelled` with `METHOD:CANCEL`. The person who planned it is invited too, so that it reaches their calendar.
- **Votes** (`PUT …/agenda/{item}/vote`): each member's Approve, Reject or Abstain with a comment, changed until the chair decides. Only the meeting's members vote. Votes are **advice**: shown with a tally to the chair and everyone reading the meeting.
- **The chair's decision** (`POST …/agenda/{item}/decision`, the meeting's chair only): **Approved** or **Rejected** (with the reason) is recorded as the chair's own decision on the change's pending board level through `ApprovalService.DecideAsync` (via `cab`): the process moves to its next level or ends, and the change is scheduled or rejected exactly as from My approvals. 409 `chair-not-approver` when the change does not wait for this chair (decided already, or the board's chair is someone else). **Deferred** keeps the change waiting for a later meeting. The decision takes the **minutes about this change**.
- **Minutes** of the meeting (`PUT …/minutes`, the chair or the change manager), **held** (`…/complete`: undecided changes are deferred) and **called off** (`…/cancel`).
- **On each change**: `GET /changes/{id}/board` lists every meeting it was on with the votes, the decision and the minutes; the change page shows them, and its facts say when it waits for a board.

Every planning, change, vote, decision, minutes and cancellation is audited.

## The emergency board

Emergency changes go to the emergency board's level: its members decide **without a meeting**, in My approvals or from the email, like any C3 approval. The change's approval summary carries the board and **`decideBy`** (the time the approval was asked plus the board's hours), shown on the change page. Emergency changes are never put on the advisory board's agenda.

## Not in H4

- Reminders or escalation when the emergency board has not decided by its time (the time is shown; for the product owner).
- Quorum rules for votes: votes are advice by decision 8.

# What-if simulation (task H5)

Before approval, the requester, the approvers and the board see what the change could reach, how similar changes went and how risky the past says it is (decisions 6 and 7).

| Piece | Where |
| --- | --- |
| The simulation: blast radius, similar changes, predicted risk | `src/api/Zelvo.Api/Changes/ChangeSimulation.cs` |
| API: the simulation and the AI explanation | `src/api/Zelvo.Api/Changes/ChangeWhatIfEndpoints.cs` |
| Kept with the change on approval (`changes.what_if`, `what_if_explanation`, `…_language`, `…_explained_at`; migration `ChangeWhatIf`) | `ChangeApprovals.cs`, `Zelvo.Data/Changes/ChangeEntities.cs` |
| AI feature `changeWhatIf` | `src/api/Zelvo.Ai/AiFeatures.cs`, the development `ai-mock` |
| The what-if panel on the change page, the approval page and figures on the board's agenda | `src/web/src/components/changes/ChangeWhatIf.tsx`, `ChangeBoard.tsx` |

## The simulation

`GET /changes/{id}/what-if` (holders of `changes.read` and the people asked to approve the change):

- **Blast radius**: G1's impact traversal (three steps) from the change's configuration items: the affected items, the **business services** among them, and the users, departments and customer groups that use them through "used by" (decision 6); **people with an affected asset assigned to them** (G3, decision 6); and the number of **people affected**, each counted once (the services' users, the members of their departments and the asset holders).
- **Similar past changes**: reviewed changes scored by **shared configuration items** (3 each, up to three), items of the **same classes** (2), the **same team** (1) and **words** of the title and description in common (up to 3; English and Arabic normalised: diacritics, tatweel, alef forms, teh marbuta and alef maqsura, the definite article, plurals, common words). Up to eight with a score of 2 or more, each with its **review outcome** and notes and why it is similar.
- **Predicted risk** (decision 7: statistics of past outcomes on the same items, classes and team): failed and backed-out changes count 1, successful with problems a half; each cohort's failure rate is smoothed toward the tenant's usual rate (all reviewed changes; 15 % until there are five), weighted by closeness (items 3, classes 2, team 1) and evidence. Low below 20 %, medium below 40 %, high from 40 %. Shown **beside the questionnaire's level** with the reasons per cohort; it never decides and never changes the approval path.

While the change is not approved the simulation is worked out live. **When it is approved** (or a standard change is scheduled) the simulation is **kept with the change** (`kept: true`, with when it was worked out); replanning forgets it.

## The AI explanation

`POST /changes/{id}/what-if/explain` with `language` `en` or `ar` (same access; audited `changes.what-if.explained`) asks the **AI feature `changeWhatIf`** (Settings → AI → Features, off until set up; `GET /changes/what-if/availability`) to explain the figures in plain language, at most 150 words. It goes through the E4 gateway (masking, guards, AI log): the data blocks hold figures and the names of the change, services, departments and similar changes, **never people's names**, and the instructions forbid recommending approval or rejection: **the AI only explains**. The explanation is kept with the change.

## Where it is shown

- The **change page** (the what-if panel, with "Explain in English" and "Explain in Arabic" when the AI is on).
- The **approval page** of a change, for approvers (a compact panel without the similar changes, which the change page shows).
- The **board's agenda**: each change with its business services, people affected, chance of failing and predicted risk.

## Not in H5

- Learning beyond the counts (for example a model per class): the statistics are explainable and enough for the first customers; for the product owner.
