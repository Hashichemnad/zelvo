# Service request fulfilment (task C4, module 2)

Covers module 2's **fulfilment per service** in `docs/requirements.md`:

- the initial assigned group, with overrides by form answer;
- service-based SLAs (ownership and completion) with escalations;
- fulfilment tasks in sequence or in parallel, each with its own target time;
- the service steps shown as the requester's journey.

It also adds the notifications of service requests, and the MCP server's catalog request tools (moved here by E6's decision of 2026-11-24). Approvals before assignment and after fulfilment came with task C3 (`docs/architecture/workflows.md`).

Product owner decisions (2026-11-24):

- **Ownership stops when an agent starts work**: In progress or a public reply, like B3's response clock. Round-robin assignment alone does not count as taking ownership. The service's ownership target replaces the SLA policy's response target.
- **Number of days → the end of the Nth working day** after submission, on the business calendar. Holidays are skipped and a short UAE Friday counts as a day. Pauses push it later by the business time paused. An exact completion target in minutes can be entered instead.
- **When every task is done, the request resolves** with a note naming the tasks, or its sign-off (C3) starts first. Agents can still resolve by hand; tasks still to do are then cancelled.
- **Notifications**: for requesters, received, waiting for you, completed and closed (approved and rejected came with C3); for staff, assigned to you, new in your group's queue, and a task given to you or waiting in your group.

## Code

| Piece | Where |
| --- | --- |
| Table `request_tasks`; `service_requests.ownership_minutes`, `completion_minutes`, `escalations` (migration `Fulfilment`) | `src/api/Zelvo.Data/Catalog/CatalogEntities.cs` |
| The card's `fulfilment` section, `steps[].taskKey`, the `Approval` stage; checks | `src/api/Zelvo.Api/Catalog/ServiceCard.cs`, `CatalogCore.cs` |
| Routing, release, task stages and completion | `src/api/Zelvo.Api/Catalog/Fulfilment.cs` |
| Task API | `src/api/Zelvo.Api/Catalog/RequestTaskEndpoints.cs` |
| Service SLA in the clocks; the end of the Nth working day | `src/api/Zelvo.Tickets/Sla/SlaTracker.cs` (`ServiceSla`), `WorkingCalendar.EndOfWorkingDay` |
| Notifications | `src/api/Zelvo.Notifications/WorkflowNotificationSources.cs` (`ServiceRequestNotificationSource`, `RequestTaskNotificationSource`) |
| MCP catalog tools | `src/api/Zelvo.Api/Mcp/McpCatalogTools.cs` |
| Demo seeder `c4-fulfilment` | `src/api/Zelvo.Api/Demo/DemoFulfilmentSeeder.cs` |
| Fulfilment in the service editor, tasks on the request page, My tasks | `src/web/src/components/catalog/{FulfilmentEditor,RequestTasks}.tsx`, `src/web/src/app/[locale]/tasks` |

## The card's fulfilment

`card.fulfilment` is versioned with the rest of the card and edited in the service editor (Fulfilment section). Internal services only.

| Field | What |
| --- | --- |
| `groupId`, `method` | The group that receives new requests first, to its queue or by round-robin. None: the B2 assignment rules decide. |
| `overrides` | `{ conditions, groupId, method }`, tried in order; the first whose conditions all hold sends the request to its group. Conditions are C3's (usually `answer.<fieldKey>`, for example `answer.city eq "Abu Dhabi"`). |
| `ownershipMinutes` | Ownership target in business minutes. None: the policy's response target. |
| `completionMinutes` | Completion target in business minutes. None: to the end of the card's `days`th working day. Neither: the policy's resolution target. |
| `escalations` | `{ metric, percent, notifyAssignee, notifyGroupLeads, notifyGroupId }`, as in SLA policies. None: the policy's. |
| `tasks` | `{ key, name, description, groupId, parallelWithPrevious, targetMinutes }` in order. No group: the request's group. |

A step can name a task (`steps[].taskKey`): the step is reached when that task starts. A new stage, **Approval**, marks steps that are reached while a request waits for its approval.

The checks:

- groups are active, and override conditions are well formed and refer to things that exist;
- targets are 1 to 525,600 minutes and escalation shares 1 to 300 %, with someone to alert;
- task keys are camelCase and unique, task names are present;
- the first task does not say "parallel";
- a step names only tasks the card has;
- external services have no fulfilment.

## Release

A request is **released** to the service desk when it is submitted without approvals before assignment, or when those are approved (C3). Release (`ServiceRequestRouting.ReleaseAsync`):

1. **Routes** it: the first override whose conditions hold, else the service's group (round-robin picks an available member with B2's rotation), else the B2 assignment rules. "Route again" does the same.
2. **Copies the service's SLA** onto the request (`service_requests`), so later card changes do not move a request's targets. The completion target from days is worked out now: the business time from submission to the close of the Nth working day after the submission day, on the request's group calendar (else the tenant default). With no working calendar, days end at midnight.
3. **Creates the tasks** from the template (once). Each task without "parallel" starts a new stage; stage 0 opens.

## SLA clocks

`SlaTracker` reads the copied targets with each update (`ServiceSla`):

- the response clock (**ownership**) uses the service's ownership target and the resolution clock (**completion**) its completion target;
- their policy is then empty;
- the service's escalation thresholds replace the policy's (on an unsaved copy of the policy), and B3/B5 deliver the alerts as before;
- the calendar stays the group's, else the policy's, else the tenant default.

Clocks pause while the request waits for the requester (a pausing on-hold reason) or for an approval (C3). Ownership stops when an agent starts work. Starting a task starts work on the request: the request moves to In progress, assigned to the agent if nobody had it.

## Tasks

| State | Meaning |
| --- | --- |
| Waiting | Its stage has not started |
| Open | Ready; due time worked out on its group's calendar from its target |
| InProgress | Being worked on |
| Done | With notes, who and when |
| Cancelled | The request was resolved or closed by hand |

- **Who.** Its assignee and its group's members work on a task (`requests.edit`). Holders of `requests.assign` and the group's team leads give it to a member. Others get 403.
- **Stages.** Completing a task locks the request's row (`FOR UPDATE`), so parallel tasks finishing at the same time open the next stage once. When the stage is done, the next opens; when every task is done, the request resolves with "All tasks are done: …" in the requester's language (`Texts/*.json`, term `request.tasks-done`), or the sign-off starts.
- **Overdue** is shown when an open task passes its due time (no alerts for tasks in C4).

| Endpoint | What |
| --- | --- |
| `GET /request-tasks?assignee=me\|none\|any&group=mine\|{id}&state=&overdue=` | Tasks, soonest due first (`requests.read`) |
| `GET /service-requests/{id}/tasks` | A request's tasks in order (`requests.read`) |
| `POST /request-tasks/{id}/start` | Open → In progress |
| `POST /request-tasks/{id}/complete` | `{ notes }`; Open or In progress → Done; advances the stages |
| `PUT /request-tasks/{id}/assignment` | `{ assigneeId }`; a member of the task's group |

Every change is audited by the audit interceptor (`request_tasks` rows).

## The requester's journey

The request's `currentStep` is the last step reached:

- a step with a task is reached when its task opens, starts or is done;
- other steps are reached when the request reaches their stage, in the order Submitted, Approval, Assigned, In progress, On hold, Resolved, Closed.

The existing progress bar on "My requests" shows it, in both languages. Requesters see steps, not tasks.

## Notifications

New B5 events, bilingual, with formal Arabic default wording. Administrators switch them and reword them in Settings → Notifications; people turn off their email in Account → Notifications.

| Event | To |
| --- | --- |
| `request.received` | The requester (also while it waits for approval) |
| `request.awaiting-requester` | The requester, when put on hold waiting for them |
| `request.resolved` | The requester, with the resolution notes |
| `request.closed` | The requester (not when closed by a rejection: `request.rejected` says it) |
| `request.assigned` | The assignee (not of their own action) |
| `request.queued` | The group's members, when it arrives without an assignee |
| `task.assigned` | The task's assignee, when it opens or is given to them |
| `task.queued` | The task's group members, when it opens without an assignee |

## MCP catalog tools

Added to Zelvo's MCP server (E6). Each needs its permission in the agent's role and scopes, and is turned on per agent.

| Tool | Permission | What |
| --- | --- | --- |
| `search_services` | `requests.read` | Services a person (by email) may request, by words |
| `get_service` | `requests.read` | A service's card as that person sees it, with its form fields and form version |
| `request_service` | `requests.edit` | Requests a service for that person with form answers. Changes data, so it waits for a person's approval unless turned off. |
| `get_service_request` | `requests.read` | A request's state, journey, approval wait, assignee, group and clocks |

- Hidden services stay hidden: the agent sees what the named person may see.
- Requests raised by agents have channel `AiAgent` and record the agent (`tickets.created_by_api_client_id`).
- Agents cannot send documents: services with required documents are requested on the portal.
- The REST endpoint `POST /service-requests` accepts integrations with `requests.edit` that name the requester, in the same way.

## Demo data

Seeder `c4-fulfilment` (order 170) adds "New employee onboarding" (`HR-ONBOARD`) to each sample organisation's employee catalog:

- the service desk as its group;
- ownership in 4 working hours, completion in 3 working days, an 80 % escalation to the team leads;
- the laptop (applications team) and the network account (infrastructure or network team) in parallel, then the hand-over by the service desk;
- steps that follow the tasks.

## Tests

- **Integration against PostgreSQL** (`FulfilmentApiTests`):
  - the service's group and an override by answer;
  - ownership and completion targets (end of the second working day) and the copied escalations;
  - problems in the card;
  - tasks in parallel and in sequence, with due times;
  - only group members work on tasks;
  - starting a task stops the ownership clock;
  - the journey moves with the tasks;
  - the last task resolves the request with the note;
  - task and request notifications;
  - resolving by hand cancels the tasks;
  - the MCP tools (hidden services stay hidden; a request raised by an agent).
- **Web**: `src/web/src/components/catalog/fulfilment.test.tsx`: tasks in sequence and in parallel, overrides in Arabic, overdue in words, start and complete.
- **End to end**: `tests/e2e/specs/workflows.spec.ts` also opens the fulfilment section and My tasks in both languages.

## Not in C4

- Alerts for overdue tasks (tasks show "Overdue"; request SLAs alert as before).
- Bundles and proactive services (C5), payments (E8).
- Teams and WhatsApp approvals and notifications (E3).
