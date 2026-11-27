# Agent wellbeing (task K4, module 21)

Covers module 21 of `docs/requirements.md`: a dashboard for team leads with **workload per agent, overtime and after-hours work and the share of angry or escalated tickets handled**; **alerts and suggested reassignment** when an agent is overloaded; visible to team leads and above, and **each agent sees their own figures**; for **workload balancing, not for ranking**; **off by default**, every agent notified when an administrator turns it on; following the **UAE Personal Data Protection Law** (GOV-5).

Product owner decisions (2026-11-26):

- Team leads see the individuals of their own groups; executives and administrators see group totals only.
- Off by default; 90 days of daily figures; after-hours work from Zelvo activity only.
- Agents see their own figures and the alerts about themselves, and are notified (`wellbeing.enabled`, in the app and by email, formal Arabic) when an administrator turns wellbeing on.

The data protection impact note is `docs/security/wellbeing-dpia.md`.

## Code

| Piece | Where |
| --- | --- |
| Tables `wellbeing_days`, `wellbeing_thresholds`, `wellbeing_alerts` with row-level security; function `zelvo_wellbeing_job_tenants()` (migration `Wellbeing`) | `src/api/Zelvo.Data/Wellbeing` |
| Settings `wellbeing.state`, `wellbeing.defaults` | `src/api/Zelvo.Api/Wellbeing/WellbeingSettings.cs` |
| Figures, alerts and retention | `src/api/Zelvo.Api/Wellbeing/WellbeingCalculator.cs` |
| The hourly job (advisory lock per tenant) | `src/api/Zelvo.Api/Wellbeing/WellbeingJobs.cs` |
| API | `src/api/Zelvo.Api/Wellbeing/WellbeingEndpoints.cs` |
| Copy of the person's data and erasure (F5) | `src/api/Zelvo.Api/Privacy/PersonalDataExport.cs`, `PersonalDataEraser.cs` |
| Demo seeder `k4-wellbeing` | `src/api/Zelvo.Api/Demo/DemoWellbeingSeeder.cs` |
| Wellbeing dashboard, Account → My workload, Settings → Wellbeing | `src/web/src/components/wellbeing`, `src/web/src/app/[locale]/{wellbeing,account/workload,settings/wellbeing}` |

## What is recorded

Once an hour, for tenants with wellbeing on, `WellbeingJobs` works out **yesterday's and today's** figures (days in the tenant's time zone) for every active member of an active support group, on the tenant's connection, so row-level security applies. Only one row per agent and day is stored (`wellbeing_days`), worked out from what Zelvo keeps already; nothing new is collected:

| Figure | From |
| --- | --- |
| Open work: open tickets assigned to the agent and the same **weighted by priority** (P1 5, P2 4, P3 3, P4 2, P5 1; no priority 3), today only | `tickets`, `incidents`, `customer_voice_records` |
| New assignments during the day | The audit log's `assigneeId` changes on tickets |
| Changes during the day, and those **outside working time**, as changes and as clock hours with a change | The audit log's entries by the agent on tickets, incidents, comments (replies and notes) and request tasks, against the business calendar of the agent's group (the first by name with a calendar, else the tenant default; without any calendar nothing counts as after hours) |
| Tickets handled, and those **angry or escalated**: a frustrated requester (E5), an SLA alert (B3), a major incident or a complaint of severity 1 (C6) | The tickets of the changes above |

**Never recorded:** presence, sign-in times, keystrokes, screen time, or any event log beyond what Zelvo keeps already.

**Kept 90 days:** older days and alerts are removed by the same job. **Turning wellbeing off removes every figure and alert** (data minimisation), audited `wellbeing.disabled` with the counts.

## Thresholds and alerts

Thresholds per group (`wellbeing_thresholds`, set by the group's team leads; empty values take the tenant's defaults, set by administrators in Settings → Wellbeing; Zelvo's defaults 40 / 12 / 5 / 50):

| Alert | Raised when |
| --- | --- |
| `Workload` | Today's weighted open work is above the threshold |
| `NewAssignments` | New assignments per day, averaged over the last 7 days, above the threshold |
| `AfterHours` | After-hours hours in the last 7 days above the threshold |
| `Difficult` | The share of angry or escalated tickets over the last 7 days above the threshold, with at least 5 tickets handled |

The job raises one open alert per agent, group and kind (`wellbeing_alerts`, unique while open), keeps its value up to date, and ends it when the agent is back under the threshold or leaves the group. Alerts are shown on the dashboard to the group's team leads and to the agent under My workload; there is no separate notification event for them (only `wellbeing.enabled`, as the plan names; for the product owner).

**Suggested reassignment** (`GET /wellbeing/groups/{id}/agents/{userId}/suggestions`, team leads of the group): up to 10 of the agent's open tickets in the group that nobody started yet (New, Assigned, Reopened; not held, not waiting for approval), each with up to three members who could take it: active, **available for assignment** (B2), not the agent, under the group's workload threshold, and handling the requester's language when they list languages (B2), lightest weighted work first. **Nothing moves until the team lead chooses** (`POST /wellbeing/reassign`): the ticket must still be open in the group and the member still a candidate; the assignment goes through `TicketWorkflow.Assign` (so the usual notifications, SLA and webhooks follow) and is audited `wellbeing.reassigned`.

## Who sees what

| Who | Sees |
| --- | --- |
| Team leads (`wellbeing.read`, by default team leads, executives and tenant administrators) | Each agent of the groups they lead (`GET /wellbeing/groups/{id}`, `agents`), listed **by name only**; thresholds; suggested reassignment |
| Others with `wellbeing.read` (executives, administrators who do not lead the group) | Group totals only (`agents` is empty): agents, open work, new assignments per agent and day, after-hours hours, difficult share, agents with alerts |
| Every staff member | Their own figures and alerts (`GET /me/wellbeing`, Account → My workload) |
| `wellbeing.manage` (tenant administrators) | Settings → Wellbeing: on or off, default thresholds; any group's thresholds |

Both permissions are **never given to integrations** (for the product owner's approval). **Every view of a person's figures is audited**: one `wellbeing.viewed` entry (category View) per agent shown to a team lead, with the group and period. Totals are not a view of a person.

**Not for ranking:** agents are listed by name, never ordered by a figure; no league tables or comparisons; no export, no scheduled reports, no PDF; the figures are not part of F2's agent performance.

## Turning it on

`PUT /wellbeing/settings` (`wellbeing.manage`): turning it on needs `staffInformed: true` (the administrator confirms staff were told what is recorded, why, who sees it and for how long). Zelvo then notifies **every active member of a support group** with `wellbeing.enabled` (in the app and by email, which cannot be turned off; wording in English and formal Arabic in `Texts/en.json`, `Texts/ar.json`), and audits `wellbeing.enabled` with the number notified. The privacy notice (F5) should get a **staff section**; the demo's notice has one, and Settings → Wellbeing reminds administrators.

## Privacy (F5)

- The person's days and alerts are part of their **copy of their data** (`personal-data.json`, `wellbeing`).
- **Erasure** removes them.
- Retention is fixed at 90 days, independent of F5's retention periods.

## API

| Endpoint | What |
| --- | --- |
| `GET /wellbeing/settings` | On or off, defaults (`wellbeing.read` or `wellbeing.manage`) |
| `PUT /wellbeing/settings` | On (with `staffInformed`) or off, defaults (`wellbeing.manage`) |
| `GET /wellbeing/groups?days=7` | Active groups with totals and `led` (`wellbeing.read`) |
| `GET /wellbeing/groups/{id}?days=7` | Totals, thresholds, alerts count; agents for its team leads (audited) |
| `PUT /wellbeing/groups/{id}/thresholds` | The group's thresholds (its team leads, `wellbeing.manage`) |
| `GET /wellbeing/groups/{id}/agents/{userId}/suggestions` | Suggested reassignment (its team leads) |
| `POST /wellbeing/reassign` | Give one ticket to a candidate (its team leads; audited) |
| `GET /me/wellbeing?days=7` | The caller's own figures |

Periods are 7, 28 or 90 days. While wellbeing is off, the dashboard endpoints answer 409 `wellbeing-off` and `/me/wellbeing` says it is off. Module 21 is not a licensed module yet (no Phase 2 module is), so `ModuleOf` gives none.

## Demo data

Seeder `k4-wellbeing` (order 280) turns wellbeing on in each sample organisation and adds 13 days of figures for the sample agent's group: the sample agent is overloaded (heavy open work, many new assignments, after-hours replies, a high share of escalated tickets) with the four alerts open; colleagues are within the thresholds. The hourly job then works out today's figures from the real sample tickets.

## Tests

- Integration against PostgreSQL (`WellbeingApiTests`): off by default; turning on needs the confirmation and only `wellbeing.manage`, and notifies every agent; P1 work weighted and after-hours replies counted against the group's calendar; the team lead sees agents by name with the workload alert, each view audited; executives see totals only and cannot move work; agents cannot see the dashboard; suggestions offer only other available members, lightest first, and the team lead's reassignment moves the ticket (audited), a non-member refused; agents see their own figures; team leads set their group's thresholds and the alert ends; the figures are in the person's copy of their data; turning off removes everything.
- Web (`wellbeing.test.tsx`): the led group's agents by name with alerts and a reassignment chosen by the team lead; totals only, in Arabic with Arabic-Indic digits; My workload and its off state; Settings → Wellbeing turned on only after the confirmation.

## Not in K4

- Notifications to team leads when an alert is raised (alerts are on the dashboard and My workload).
- Licensing module 21 (no Phase 2 module is licensed yet).
