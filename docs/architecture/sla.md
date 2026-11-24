# SLA management (task B3)

Covers **module 8** (SLA management) and **GOV-7** (UAE business calendar) of `docs/requirements.md`. Product owner decisions of 2026-11-24:

1. **Customer groups** are made by administrators (bilingual names). A requester belongs to a group when their email address is in one of the group's domains, or when an administrator adds them by name. A requester can be in several groups; the first matching SLA policy wins.
2. **The response clock** stops when a staff member moves the ticket to In progress, or resolves it first. From B4 a staff public reply also counts, whichever comes first (built: `workspace.md`).
3. **Escalations:** each threshold says who to alert (the assignee, the group's team leads, the members of another group). B3 records the alert on the ticket, audits it, sends a webhook event and shows the ticket as at risk. Notifications (B5) deliver the alerts.
4. **Calendar of a ticket:** the assigned group's calendar when the group has one, else the SLA policy's calendar, else the tenant default. When the group changes, the remaining time is counted on the new calendar.

Code: `src/api/Zelvo.Data/Sla` (entities), `src/api/Zelvo.Tickets/Sla` (business time, policy matching, `SlaTracker`, `SlaTimerJob`, webhook events, calendar templates), `src/api/Zelvo.Api/Sla` (API, the clocks in incident responses, missing translations), `src/web/src/components/sla` and the `settings/sla-policies`, `settings/business-calendars` and `settings/customer-groups` pages.

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `business_calendars` | Bilingual name, IANA time zone (`Asia/Dubai` by default), default flag | At most one default per tenant (unique partial index); audited |
| `business_calendar_periods` | Day of the week, start and end minute (0–1440) | Several per day: a Friday half-day is a shorter period, a weekend is a day without one |
| `business_calendar_holidays` | First and last day, bilingual name | Whole days off; at most 60 days per entry |
| `customer_groups` | Bilingual name, active, email domains (`text[]`) | Audited |
| `customer_group_members` | Requesters added by name | Audited |
| `sla_policies` | Bilingual name, position, active, conditions (record type, category, customer group), calendar | Tried in order; audited |
| `sla_policy_targets` | Response and resolution minutes per priority (1–5) | Business minutes; a missing value means no target |
| `sla_escalations` | Threshold (1–300 % of a target), which target (or both), who to alert | 100 % is the breach |
| `ticket_slas` | One clock per ticket and target: policy, target, calendar, state (Running, Paused, Stopped), business seconds counted, counted until, due time, breach time, highest threshold reached, next check | Not audited row by row (moved on every few seconds); see *Audit* |
| `sla_alerts` | Thresholds a ticket reached: target, percent, when, recipients, delivered (B5) | One per clock and threshold (unique index); audited |

All ten tables have forced row-level security (`EnableTenantIsolation`); the guard test covers them. `tickets.responded_at` records the first response; `support_groups.calendar_id` gives a team its own hours. The timer job finds tenants with due clocks through the `SECURITY DEFINER` function `zelvo_tenants_with_due_slas(due)`, which returns only ids and slugs.

Tickets created before B3 get their clocks at their next change, counted from when they were logged (pauses before B3 are not known). The migration counts tickets that were already worked on as responded to.

## Business time

`WorkingCalendar` turns a calendar into working intervals: for each local date from the start, unless it is a holiday, each period of that day of the week, converted to UTC through the calendar's time zone (so daylight-saving changes work; the UAE has none). `SecondsBetween(from, to)` adds up the overlap, and `Add(from, seconds)` walks the intervals to the moment the time is used up (looking up to ten years ahead; a calendar without working time gives no due time). A tenant without calendars, or a calendar without any working period, counts around the clock.

Templates (`GET /api/v1/sla/calendar-templates`):

| Key | Week |
| --- | --- |
| `uae-government` | Monday–Thursday 07:30–15:30, Friday 07:30–12:00, Saturday and Sunday off (the federal working week since 2022) |
| `uae-private` | Monday–Friday 09:00–18:00 |
| `all-day` | Every day, around the clock |

Public holidays are entered by administrators. Islamic holidays follow the moon sighting, so Zelvo does not compute them. The screen says to add them when they are announced.

## Policies and clocks

- **Matching** (`SlaMatcher`): active policies in position order; the first whose conditions all hold applies. A category condition matches the category and everything below it. The customer group condition matches requesters in the group by email domain or by name. Each policy has targets per priority; the ticket's priority (incidents: from the impact × urgency matrix) picks the row.
- **Clocks** (`SlaTracker`): every ticket has a response clock and a resolution clock, with or without a target. Keeping a clock without a target means a policy that applies later counts the time that already passed. On every save of a ticket, inside its transaction:
  1. the business time since the last look is added (on the calendar the clock counted on);
  2. the policy, target and calendar are matched again: a priority, category, group or requester change takes the new target, and the elapsed time is kept;
  3. thresholds reached are recorded (see *Escalations*);
  4. the state follows the ticket. **Running**; **Paused** while on hold for a reason that pauses SLAs (the switch on each on-hold reason, on by default only for "Waiting for the requester"); **Stopped** when responded to (response) or resolved or closed (resolution). A reopened ticket's resolution clock runs again from where it stopped;
  5. the due time and the next check (the next threshold, or the breach) are worked out.
- **Locking:** the tracker locks the tickets' rows and then their clocks (in id order), and the timer job locks tickets with `FOR UPDATE SKIP LOCKED` before their clocks, so the API and the worker never deadlock and never count the same time twice.
- **Changes to the set-up** (policies, calendars, holidays, customer groups, a group's calendar, the on-hold reasons) ask for a look at every running or paused clock (`next_check_at = now()`), so they apply to open tickets within seconds. Stopped clocks keep what they had.

## Escalations

The timer job (`SlaTimerJob` in the worker, every 15 seconds) brings due clocks up to date through the same tracker. For each threshold a clock passes, once:

- an `sla_alerts` row with the moment it was reached (computed on the calendar, not when the job ran) and its **recipients**, resolved then: the assignee, the active team leads of the ticket's group and the active members of another group, as the escalation says. `delivered_at` stays empty until notifications (B5) deliver it;
- an audit entry (`sla_alert.created`, actor "SLA timer");
- webhook events `ticket.sla.threshold` (every threshold) and `ticket.sla.breached` (at 100 %), with ticket id, number, record type, target, percent and time; no personal data;
- at 100 % the clock's breach time. A breached clock stays breached after it stops.

## Who sees and does what

| Permission | Built-in roles | Gives |
| --- | --- | --- |
| `directory.read` | agent, team lead, administrator, executive/auditor | Read calendars, customer groups and policies; the policy tester |
| `sla.manage` | administrator | Change them and the business hours of groups |

Staff with `incidents.read` see the clocks of incidents (field `sla`, which field rules can hide). Requesters never see them.

## API

`/api/v1/sla` (OpenAPI tag "SLA"): `calendar-templates`; `calendars` (list, get, create, replace, delete; `…/holidays` add, change, remove); `customer-groups` (list, create, replace, delete; `…/members/{userId}` add, remove); `policies` (list, create, replace, delete, `PUT …/order`, `POST …/test`); and `PUT /api/v1/groups/{id}/calendar`. Incidents get `respondedAt` and `sla` (per clock: metric, status None / Running / AtRisk / Paused / Met / Breached, state, target, business seconds used up to now, percent, due, breach and stop times, policy and calendar) and the list filters `sla=atRisk|breached` and `sort=due`. Settings → Incidents gets `pausesSla` per on-hold reason and `autoCloseBusinessDays`.

Problem codes: `calendar-not-found`, `holiday-not-found`, `customer-group-not-found`, `policy-not-found`, `in-use` (the default calendar, or a calendar, customer group or category that policies or groups use), `validation-failed`.

## Auto-close in working days

With `tickets.autoCloseBusinessDays` on, the auto-close period counts working days of the tenant's default calendar: a ticket resolved on Thursday at 10:00 with a 5-day period on the government week closes the next Thursday at 10:00 (holidays skipped). Without a default calendar every day counts.

## Audit

Calendars, periods, holidays, customer groups and members, policies, targets, escalations and alerts are audited field by field. Clocks are not (they change every few seconds); what matters about them is audited instead: `tickets.sla.policy` when a ticket gets a policy or target (or loses it), and every alert.

## Screens

- **Settings → SLA policies:** policies in order with move up and down, a summary of conditions, targets and thresholds, a warning when no policy covers every incident; the form (conditions, calendar, targets per priority in hours and minutes, escalations with who to alert); "Try the policies".
- **Settings → Business calendars:** each calendar with its week and holidays; templates; the week editor (any number of periods per day, 24-hour times); holidays as one day or a range.
- **Settings → Customer groups:** domains and people added by name.
- **Settings → Groups → a group:** its business hours.
- **Settings → Incidents:** "Pauses SLA clocks" per on-hold reason; "Count working days" for auto-close.
- **Incidents:** an SLA column (the most urgent clock and when it is due), filters at risk and breached, soonest due first, and a link to open incidents at risk. The incident page shows both clocks: status, target, time used with a progress bar, due, breach and stop times, policy and business hours. Live timers are in the agent workspace (B4, `workspace.md`).
- English and Arabic, right to left, Hijri dates and Arabic-Indic digits as the user chooses, WCAG 2.2 AA (axe checks in `tests/e2e/specs/sla.spec.ts`).

## Demo data

`DemoSlaSeeder` (`b3-sla`): the government entity gets the government week as its default calendar, the company office hours. Both get an around-the-clock calendar for the infrastructure or network team and UAE public holidays to August 2027 (Islamic holidays as expected dates, marked as such). There is a customer group with the demo requester, a VIP and a standard policy with 75 % and 100 % escalations, and clocks for the sample incidents.

## Tests

- Unit (`SlaEngineTests`): business time on the government week (nights, weekend, Friday half-day, holidays), adding and counting agree, around the clock, daylight-saving days, working days for auto-close, policy matching and order, targets and thresholds, email domains, clock states from ticket states, the response at resolve, reasons saved before B3.
- Integration against PostgreSQL (`SlaApiTests`): calendars, holidays, the default and validation; clocks following the life cycle (response met at In progress, paused only for pausing reasons, resolution stopped and resumed on reopen); the timer recording 75 % and 100 % once, with the right recipients, breach time, audit entries and webhook deliveries, and the at-risk and breached filters; customer groups by name and by domain with the VIP policy first and the tester; a priority change keeping the elapsed time; group, policy and default calendars; policy changes reaching open tickets; policy validation and permissions; pausing reasons and auto-close in working days; tenant isolation. `DemoModeTests` checks the sample data.
- Web component tests (`sla.test.tsx`) and the end-to-end journey (`sla.spec.ts`) in English and Arabic.

## Not in B3

- Live timers over SignalR in the agent workspace and public replies as a response: built in B4 (`workspace.md`). Delivering the alerts by email and in-app is now built (B5, `notifications.md`): each new `sla_alerts` row notifies its recipients and `delivered_at` is set when the first notification is out.
- SLAs of service requests, complaints, inquiries and suggestions: the model takes any record type (`sla_policies.record_type`); C4 and C6 add their record types and service-specific targets.
- Special working hours for a period (for example shorter hours in Ramadan) and half-day holidays: holidays are whole days. To confirm with the product owner.
- SLA compliance dashboards (F2).
