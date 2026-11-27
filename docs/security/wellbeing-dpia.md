# Data protection impact note: agent wellbeing (task K4, module 21)

A short impact note for the wellbeing dashboard under the UAE Personal Data Protection Law (Federal Decree-Law No. 45 of 2021, GOV-5). It is evidence for the customer's own assessment: each customer, as the controller of its staff's data, decides whether to turn wellbeing on and records its own assessment. Article numbers are indicative, as in `uae-pdpl-mapping.md`.

## What and why

| | |
| --- | --- |
| Purpose | Balance work between support agents before anyone is overloaded (module 21). Not for ranking, scoring, discipline or pay. |
| People | Members of support groups (agents and team leads) of the customer's own organisation. |
| Data | Per person and day: open tickets assigned (and weighted by priority), new assignments, number of their changes in Zelvo and of those outside the group's working hours, clock hours with such changes, tickets handled and how many were angry or escalated. Open alerts against the group's thresholds. |
| Sources | What Zelvo already keeps to run the service desk: tickets, comments, tasks and the audit log. No new collection; no presence, sign-in, keystroke or screen tracking. |
| Lawful basis (customer to confirm) | The employer's legitimate interest in workload and health and safety, with staff informed in advance (the privacy notice's staff section and the `wellbeing.enabled` notification). |

## Measures in Zelvo

| Risk | Measure |
| --- | --- |
| Monitoring staff without their knowledge | Off by default. Turning it on needs the administrator's confirmation that staff were told; every agent is then notified in the app and by email in their language, and can see their own figures and alerts at any time (Account → My workload). |
| Using the figures to rank or score people | Agents listed by name only, never ordered by a figure; no league tables, comparisons, export, PDF or scheduled reports; not part of agent performance reports (F2). |
| Too many people seeing individual figures | Individual figures only for the team leads of the person's own groups; executives and administrators see group totals only; never for integrations. |
| Unnoticed access | Every view of a person's figures is in the audit log (`wellbeing.viewed`). |
| Keeping more than needed | Daily figures only; 90 days; removed when the person is erased; everything removed when wellbeing is turned off. |
| Excess detail about working hours | After-hours work is counted per hour with activity and per change, never as a timeline of the person's day; only Zelvo activity is used. |
| Decisions made by the system | Alerts and suggested reassignments are advice; only a team lead moves work, one ticket at a time, and each move is audited. |
| Rights of the person | The figures and alerts are in the person's copy of their data (F5); erasure removes them; correction and objection go through F5's data subject requests. |
| Tenant isolation | All three tables use PostgreSQL row-level security like every tenant-owned table. |

## Residual points for the customer

- Add a staff section to the privacy notice (Settings → Privacy) before turning wellbeing on; the demo notice shows an example.
- Decide who holds `wellbeing.read` beyond team leads, executives and administrators, and the thresholds that suit each team.
- Consider consulting staff representatives where the organisation's policies require it.
