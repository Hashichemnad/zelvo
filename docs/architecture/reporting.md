# Reporting, dashboards and setup wizard (task F2)

Covers module 10's "Dashboards: volumes, backlog, SLA compliance, agent performance, happiness scores; scheduled reports to email" and "Admin: setup wizard", and the module 10 story "set up a new department's service desk from a template, so that it's live the same day" (`docs/requirements.md`).

## Product owner decisions (2026-11-25)

1. **Fixed dashboards with filters.** Five built-in dashboards (volumes, backlog, SLA compliance, agent performance, happiness) and an overview, filtered by period, record types, groups, categories and interval. A dashboard builder is not part of Phase 1.
2. **Agent performance:** team leads see the agents of the groups they lead; executives and tenant administrators see all agents; agents see only their own figures.
3. **Scheduled reports** are emailed as a **branded PDF** (F1's renderer) with a link to the live dashboard, **only to tenant users who may see reports**. Each recipient's report is built with **their own access**, in their language, calendar and digits.
4. **Department templates:** IT service desk, human resources, facilities, customer happiness centre. Approval rules come with task C3 and are not in the templates yet.

## Code

| What | Where |
| --- | --- |
| Report model, filters, scope, access, the aggregate SQL, PDF document and labels (English, Arabic), schedule times | `src/api/Zelvo.Tickets/Reports` |
| Schedules, recipients, delivery log (entities) | `src/api/Zelvo.Data/Reports/ReportEntities.cs`, migration `Reporting` |
| Dashboard API, CSV and Excel tables, PDF exports, targets | `src/api/Zelvo.Api/Reports/ReportEndpoints.cs` |
| Scheduled reports API | `src/api/Zelvo.Api/Reports/ReportScheduleEndpoints.cs` |
| Worker job that sends scheduled reports | `src/api/Zelvo.Notifications/ReportJobs.cs` |
| Setup wizard and department templates | `src/api/Zelvo.Api/Setup` |
| Demo data | `src/api/Zelvo.Api/Demo/DemoReportingSeeder.cs` (`f2-reporting`) |
| Web pages | `src/web/src/app/[locale]/reports`, `src/web/src/app/[locale]/setup`, Settings → Reports |

## Who sees what

`ReportAccess.ScopeAsync` works out a `ReportScope` for a person from the permissions that apply now:

| Permission | Default roles | Sees |
| --- | --- | --- |
| `reports.all` | executive / auditor, tenant administrator | every group and agent, and general portal ratings |
| `reports.read` | team lead, executive / auditor, tenant administrator | the groups they lead and those groups' members; with no group to lead, like other staff |
| none of the two | any staff with a read permission | only tickets assigned to them ("my performance") |
| `reports.manage` | tenant administrator | schedules for other people, every schedule, the report targets |

Record types follow the person's read permissions: incidents with `incidents.read`, service requests with `requests.read`, and complaints, inquiries and suggestions with `voice.read`. Without any of them the API answers 403. A filter can only narrow the scope: a team lead who asks for a group they do not lead gets no figures for it. Integrations (API keys, OAuth clients) can read dashboards only with `reports.all`, because they have no groups or figures of their own.

## Definitions

Periods are local dates in the tenant's time zone (the default business calendar's, B3), both days included; the last 30 days by default, at most 731 days. Trends are split by day up to two months, by ISO week up to a year, then by month, unless an interval is chosen (at most 400 points).

- **Volumes:** tickets created, resolved and closed in each bucket, by when that happened; created tickets per record type, channel, priority, group and category (a category filter includes the categories below it).
- **Backlog:** tickets open now (not Resolved or Closed) per state, age band (under a day, 1–3, 3–7, 7–30, over 30 days), record type, priority and group, and how many are not assigned. The trend is the number open at the end of each bucket: created before it and not yet resolved or closed then (reopened tickets count from their first resolution, a simplification of the trend only).
- **SLA compliance:** the B3 clocks of tickets created in the period. A clock is **met** when it stopped without a breach, **breached** when it passed its target, and **still running** otherwise; the percentage is met ÷ (met + breached). Per priority, record type, group, SLA policy and bucket, with the tenant's target (95 % by default, the requirements' first-response metric).
- **Agent performance:** per agent (the ticket's current assignee): tickets created in the period and assigned to them, resolved in the period, open now, median minutes to the first response and hours to resolve, SLA clocks met, share of resolved tickets that were reopened, and happiness of their tickets' ratings.
- **Happiness:** ratings given in the period (C6's happiness meter), as the average score as a percentage of each rating's scale (worst 0 %, best 100 %), with scores, record type, channel, group, service and buckets. The target is 75 % by default (4 out of 5). General portal ratings have no ticket and are shown only with `reports.all` and no group, category or type filter.

Priorities come from incidents (impact × urgency) and customer voice records (severity or the type's priority); service requests have none and show as "none".

## API

All under `/api/v1`, documented in OpenAPI:

- `GET /reports/scope`: what the caller sees (`All`, `Groups` with the groups, or `Own`).
- `GET /reports/overview|volumes|backlog|sla|agents|happiness?from&to&types&groups&categories&interval`.
- `GET /reports/{kind}/table?format=csv|xlsx`: the dashboard's main table (CSV is UTF-8 with a byte order mark; cells that look like formulas are neutralised by E1's `TabularFile`).
- `POST /reports/{kind}/exports?language=en|ar&…filter`: a branded PDF made by the worker (F1's `document_exports` gets the kind `Report` with the dashboard and filter as JSON, no ticket). It is built with the asker's access at the time it is made, and only the asker downloads it from `GET /exports/{id}/file`.
- `GET|PUT /settings/reports`: the targets (`reports.targets` tenant setting; change needs `reports.manage`).
- `GET|POST /reports/schedules`, `GET|PUT|DELETE /reports/schedules/{id}`, `POST /reports/schedules/{id}/send-now`, `GET /reports/schedules/{id}/deliveries`, `GET /reports/schedules/recipients`.

Every dashboard view and table export is audited (`report.viewed`, `report.exported`, with the filter and scope but no figures); PDF exports (`export.requested`, `export.downloaded`), schedule changes (field-level, as audited entities), "Send now" and each report emailed (`report.emailed`) too.

The aggregate queries are hand-written SQL (`ReportBuilder`) run on the tenant's connection, so row-level security applies to them like to every other query. Every value is a parameter; the SQL text is put together only from fixed fragments in code.

## Scheduled reports

A schedule names a dashboard, its filters (record types, groups, categories), a frequency and a time in the tenant's time zone:

| Frequency | When | Covers |
| --- | --- | --- |
| Daily | every day at the time | the day before |
| Weekly | on a day of the week (1 Monday – 7 Sunday) | the seven days before |
| Monthly | on a day of the month (1–28) | the calendar month before |

People with `reports.read` or `reports.all` schedule reports for themselves. With `reports.manage` they add up to 50 recipients, who must be active users holding `reports.read` or `reports.all`. Owners change and remove their own schedules; `reports.manage` changes all of them.

The worker's `ReportScheduleJob` (every 30 seconds; tenants found with the SECURITY DEFINER function `zelvo_report_tenants()`; leases with `FOR UPDATE SKIP LOCKED`) does three things:

1. When a schedule is due, it queues one `report_deliveries` row per recipient for that run (unique per schedule, recipient and run, so a run is never sent twice) and moves the schedule to its next run.
2. For each email due, it checks the recipient again: people who are no longer active, have no email address or may no longer see reports are **skipped**. Otherwise it builds the report with the recipient's scope, prints it with headless Chromium (F1) in the recipient's language, calendar and digits, and emails it with the PDF attached. The email comes from the tenant (organisation name, its verified sender domain and DKIM from F1), uses the B5 letterhead and "Powered by Zelvo", and links to the dashboard with the same period. Failed attempts are retried like notification emails (`Notifications:RetryDelays`).
3. It removes delivery log entries older than 30 days (`zelvo_purge_report_deliveries`).

"Send now" queues the report for every recipient straight away, for the last complete day, week or month.

## Setup wizard

`/setup` (permission `setup.manage`, tenant administrators; not for integrations, for approval) lists nine steps in order: organisation name, language and region, business calendar, branding, sign-in, departments, users, channels (mailboxes and live chat) and AI. Each step shows whether its settings already look set up (for example a default business calendar exists, branding is published, there is more than one active staff user, an AI provider is connected; sign-in has no automatic check). It explains what to do and links to the step's Settings page. The administrator marks it done or skips it. The wizard only guides: settings are changed on their own pages through their own APIs. Progress is the tenant setting `setup.progress`. The administrator finishes the wizard (and can reopen it); until then Settings shows "Finish setting up". License activation joins the wizard with task F3.

### Department templates

`GET /setup/templates` lists the four templates with everything they create. `POST /setup/templates/{key}/apply` creates, in one audited transaction (`setup.template.applied`, plus field-level entries for every new row):

- a **department** (C1's list), named as the administrator chooses (default: the template's name), in English and Arabic;
- its **support groups**, which can be renamed;
- a **category tree** under a new root category named after the department (second and third levels from the template), so no existing category changes;
- **assignment rules** sending each category to its group's queue. They are placed first, but they match only the new categories, so the order of existing rules is kept;
- **SLA policies** for the department's record types on its root category (response and resolution targets per priority in business minutes, escalations at 75 % and 100 % to the assignee and the group's team leads), also placed first, on the tenant's calendars;
- optionally, a **catalog** for the department with **draft services** from C2's built-in templates (codes such as `IT-LAPTOP`, prefix changeable). They are published only through C2's four-eyes review;
- optionally, the template's **canned responses** for the department's first group (B4).

Templates never change anything that exists. They can be applied again for another department, getting new groups, a second catalog key (`it-service-desk-2`) and new codes (`IT-LAPTOP-2`). People are added to the groups afterwards (Settings → Users and Groups, or bulk import).

| Template | Groups | Record types with SLA policies | Draft services |
| --- | --- | --- | --- |
| IT service desk | IT service desk, IT infrastructure, Business applications | incidents, service requests | new laptop, software access, password reset |
| Human resources | HR services, Payroll | service requests, incidents | salary certificate, HR general request |
| Facilities | Maintenance, Workplace services | incidents, service requests | meeting room booking, maintenance request |
| Customer happiness centre | Customer happiness team, Complaints handling | complaints, inquiries, suggestions | general service request |

## Demo data

The `f2-reporting` seeder adds about six months of weekday incidents and service requests to both sample organisations. They are spread over the sample groups, agents, categories and channels, with response and resolution SLA results and happiness ratings that improve month by month, and a few tickets still open. It also marks the setup wizard as finished and adds a weekly overview emailed to the executive on Monday mornings. It is deterministic, so every reset shows the same trends.

## Not in F2

- Knowledge article drafts in department templates: knowledge articles are created through D1's request flow (versions, search indexing, ETags). Adding them would need that flow refactored, so it was left for later.
- Approval rules in templates and the license activation step: with tasks C3 and F3.
- Read replicas for reporting (non-functional requirement "Scalability"): reports run on the primary with the existing indexes. Directing them to a replica is part of the medium and large installs (F6).
- A dashboard builder, and reporting formats that government customers submit to their regulator (open point under GOV-3).
