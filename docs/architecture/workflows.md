# Workflow rules and approvals (task C3, modules 2 and 10)

Covers, from `docs/requirements.md`:

- module 10's **no-code workflow builder for assignment, approval and notification rules**;
- module 2's **approvals at any stage**: before assignment and after fulfilment; single, multi-level, parallel, or chosen by role, amount or form answer; approve from email and the portal (Teams and WhatsApp come with module 6, task E3); rejection returns the request with a reason.

Product owner decisions (2026-11-24):

- **Elsa Workflows 3 confirmed** by the spike at the start of C3 (Temporal stays the fallback). Elsa runs embedded in the API with PostgreSQL persistence. Its tables get a tenant column filled by the database and forced row-level security. The rules stay Zelvo data, edited in Zelvo's own bilingual builder; Elsa Studio (English only) and Elsa's HTTP API are not used.
- **The builder is "when / if / then" rules**, not a flowchart canvas: a trigger, conditions and actions, in an ordered list. Module 20 (Phase 2) adds plain-language rules and a visual view.
- **Approve from email**: the email's Approve and Reject buttons open the approval in Zelvo with the choice made. The approver signs in as usual (email code, UAE PASS, a second factor for staff) and confirms. Nothing is decided by holding the email alone.
- **Rejected before assignment: the request is closed** with the reason; the requester can request the service again. A rejected sign-off after fulfilment goes back to the assignee (In progress) with the reason.

## The spike

Done first, as the build plan asks (`/tmp` project, not kept):

1. Elsa 3.10.2 (MIT) targets .NET 10 and EF Core 10, so it sits next to Zelvo's own packages.
2. A code-defined workflow with a custom activity that waits at a bookmark was started, persisted in PostgreSQL, and resumed from a new service scope by bookmark id.
3. Row-level security on Elsa's tables: each table got `zelvo_tenant_id uuid DEFAULT zelvo_current_tenant_id()` and a forced policy. An EF Core connection interceptor sets `zelvo.tenant_id` on Elsa's connections. A role without `BYPASSRLS` then saw its own tenant's workflow instance and not another tenant's.
4. Only `Elsa.Workflows.Runtime`, `Elsa.Workflows.Management` and `Elsa.Persistence.EFCore.PostgreSql` are needed. They still bring Elsa's API packages (FastEndpoints, NSwag and others, MIT) as transitive dependencies; none is started.

## Code

| Piece | Where |
| --- | --- |
| Tables `workflow_rules`, `approval_processes`, `approval_decisions`; `tickets.awaiting_approval`; function `zelvo_secure_elsa_tables()` (migration `Workflows`) | `src/api/Zelvo.Data/Workflows` |
| Rule documents (conditions, actions, approval levels), checks and evaluation | `src/api/Zelvo.Workflows/Rules/RuleDocuments.cs` |
| The interceptor that runs rules with every save; the facts rules look at | `src/api/Zelvo.Workflows/Rules/WorkflowRuleInterceptor.cs` |
| Approval planning (who approves each level) | `src/api/Zelvo.Workflows/Approvals/ApprovalPlanner.cs` |
| The Elsa engine, its workflow and the tenant on its connections; migrations | `src/api/Zelvo.Workflows/Engine` |
| API: rules, approvals, the approval service, reference checks, missing translations | `src/api/Zelvo.Api/Workflows` |
| Notifications: approval events, rule wording, two-button emails | `src/api/Zelvo.Notifications/WorkflowNotificationSources.cs`, `NotificationComposer.cs` |
| Demo seeder `c3-workflows` | `src/api/Zelvo.Api/Demo/DemoWorkflowsSeeder.cs` |
| Settings → Workflow rules, My approvals, the approval page, approvals on request pages, approvals in the service editor | `src/web/src/app/[locale]/{settings/workflows,approvals}`, `src/web/src/components/workflows` |

## The engine

`IApprovalEngine` is the only way Zelvo talks to Elsa, so Temporal could replace it.

- It runs one code-defined workflow, `zelvo-approval`. Its single activity, `ApprovalLevels`, walks the levels of a process: it waits at a bookmark for each level's outcome. Approved moves to the next level; rejected, or the last level approved, ends the workflow.
- Elsa persists the position, so a process survives restarts and continues on any API instance.
- Zelvo keeps everything people see in its own tables: the planned levels, approvers, decisions, comments and the outcome. Elsa holds no personal data, only ids.
- `ElsaTenant` (an async-local value) holds the tenant for Elsa's connections. `ElsaTenantConnectionInterceptor` sets `zelvo.tenant_id` from it, so Elsa's reads and writes go through row-level security like Zelvo's own.

**Elsa's tables** are in the schema `Elsa` of Zelvo's database.

- The `migrate` step runs Elsa's own migrations with the owner's connection (`WorkflowEngineSetup.MigrateAsync`). The API never migrates Elsa.
- `migrate` then calls `zelvo_secure_elsa_tables()`. For every Elsa table it adds `zelvo_tenant_id` (default: the connection's tenant) with an index, enables and forces row-level security and creates the policy. It also grants the application role (whoever may read `public.tenants`, apart from the owner) the rights it needs. The function is safe to run again; new Elsa tables after an upgrade are covered the next time.
- Workflow definitions, triggers, registry generations and key-value pairs are written by code when the API starts, without a tenant. Their policy also allows rows with no tenant. Every other table (instances, bookmarks, logs, execution records, inbox, bookmark queue) only shows rows of the connection's tenant.
- Elsa's background services run without a tenant, so they see no instance rows. Zelvo resumes processes itself, by bookmark id.
- "Reset demo" deletes the sample tenant's Elsa instance rows too.
- **Locks.** Elsa's default distributed lock is a file under the working directory, which the read-only, non-root containers cannot write. Zelvo gives Elsa PostgreSQL advisory locks instead (`DistributedLock.Postgres`, MIT, the library family Elsa already uses), which also hold across several API instances.

**Consistency.** A decision is recorded in Zelvo's transaction, with the process row locked (`FOR UPDATE`), so approvers deciding at the same time are taken one after the other. The engine is called inside that lock. Elsa commits its own state on its own connection, so if Zelvo's save then failed, the engine would be one step ahead. Zelvo's rows stay the record: the request still shows the level as waiting, and the instance can be cancelled. This is rare (the save follows straight after) and is noted for review in F7.

## Workflow rules

A rule (`/api/v1/workflow-rules`, `workflows.manage`) has:

- a bilingual name and description, an order and whether it runs;
- **when**: a trigger and a record type (incidents, service requests or both);
  - `TicketCreated`: a ticket is logged or a request submitted;
  - `StateChanged`;
  - `AssignmentChanged`: group or assignee;
  - `ApprovalDecided`: an approval process ended;
- **if**: conditions, all or any of which must hold (none: always):

  | Field | Operators | Value |
  | --- | --- | --- |
  | `service`, `group`, `requesterDepartment` | `in`, `notIn` | ids |
  | `category`, `location` | `in` (the node or below), `notIn` | ids |
  | `channel`, `state`, `requesterLanguage` | `in`, `notIn` | names |
  | `priority` (incidents), `fee` (service requests, AED; free is 0) | `eq`, `neq`, `gt`, `gte`, `lt`, `lte` | a number |
  | `answer.<fieldKey>` (form answers) | the comparisons, `contains`, `empty`, `notEmpty` | text or a number |

- **then**: actions, in order:
  - `assign`: to a group's queue, by round-robin among its available members (B2's rotation), or to a named member;
  - `notify`: recipients and bilingual wording (below);
  - `approval`: service requests on submission only. The stage (before assignment or after fulfilment) and its levels.
  - `connector` (task E7): call an operation of a REST connector; queued with the change and sent by the worker with retries, its answer written as an internal note (`connectors.md`).
- optionally **stop**: when it applies, later rules of the same trigger are not tried.

Problems are listed together (400 with `problems`, as forms and cards do): known fields with fitting operators and values, references that exist (services, categories, locations, groups, departments, users, roles), placeholders the wording may use, and approvals only where they are allowed. "Try the rules" (`POST /workflow-rules/test`) shows which rules of a trigger hold for an existing ticket now, and what they would do, without changing anything.

**How rules run.** `WorkflowRuleInterceptor` is a save interceptor registered before notifications and webhooks, in both the API and the worker, so rules see every change, whoever makes it (the portal, email intake, the MCP server, the SLA and auto-close jobs).

- With each save it collects the tickets that were created, changed state or changed assignment, and the approval processes that ended.
- It tries the active rules of those triggers in order and applies the actions of each rule that holds, in the same transaction.
- `assign` changes the ticket before it is saved, so the usual assignment notifications and webhooks follow. It does nothing while the ticket waits for an approval, or once it is resolved or closed.
- `notify` queues notifications through `PendingNotifications`, which the notification interceptor writes.
- Rules run once per save: what rules change does not trigger rules again.
- Every rule that applied is audited (`workflows.rule.applied`, with the rule, trigger and actions).

B2's assignment rules still decide where tickets go first; workflow rules run after them and can override them.

**Notify actions** send the event `workflow.rule`.

- The wording is the rule's own (subject and message in English and Arabic), carried in the notification's values. The B5 renderer uses it in the recipient's language, with the same placeholders, encoding, letterhead and footer as every Zelvo email.
- Placeholders: `recipient.name`, `tenant.name`, `ticket.number`, `ticket.title`, `ticket.state` (named in the recipient's language), `ticket.group`, `requester.name`, `service.name`.
- Recipients: the requester, the requester's manager, the assignee, the ticket's group or its team leads, named groups and holders of a role. Requesters' links open their own page; others' open the staff page.
- The rule's "also by email" setting decides the email. The event is not listed in Settings → Notifications or Account → Notifications, because each rule has its own wording and setting.

## Approvals

**Where levels come from.** A service's card has `approvals.beforeAssignment` and `approvals.afterFulfilment`, each a list of levels, edited in the service editor and versioned with the card. Rules with approval actions add levels after the service's own, in rule order. A level has:

- a bilingual name, shown to requesters and approvers;
- a mode: `Any` (the first approval decides the level) or `All` (parallel: everyone must approve, and one rejection rejects);
- approvers: the requester's manager (C1 profile), a named person, every member of a group, a group's team leads, holders of a role, or the person picked in a user-picker answer of the form;
- conditions under which it applies, with the fields above. This is how approvals by **amount** (for example `fee gt 5000`) or by **answer** are made.

**Planning** (`ApprovalPlanner`) happens when the process starts.

- Levels whose conditions do not hold are left out.
- Approvers are worked out then; the requester is never their own approver.
- If a level names nobody who can approve (for example the requester has no manager), the tenant's workflow administrators (`workflows.manage`) are asked instead. The level is marked "fallback" so requesters and approvers can see why.

**Before assignment.** When a request is submitted and levels are planned:

- the request is not routed; it waits (`awaitingApproval`) in state New without a group;
- the first level's approvers are asked; each level follows when the one before it is approved;
- staff cannot change its state or assignment meanwhile (409 `awaiting-approval`);
- approved: the request is routed to the service desk as usual;
- rejected: the request is closed with the reason as its notes;
- the requester may withdraw it, and staff with `requests.edit` may stop the approval (`POST /service-requests/{id}/approvals/cancel`). Either way the request is closed as withdrawn.

**After fulfilment (sign-off).** When an agent resolves a request whose service or rules ask for a sign-off:

- the request stays in progress and waits; the agent's resolution notes are kept with the process;
- approved: the request is resolved with those notes;
- rejected: it goes back to the assignee, the reason is added to the conversation as an internal note (by the approver), and the assignee is told;
- staff may stop the sign-off; the request then stays in progress.

**SLA clocks pause** while a request waits for an approval (B3's clocks; the C4 service clocks pause too).

**Deciding** (`/api/v1/approvals`):

| Endpoint | What |
| --- | --- |
| `GET /approvals?state=Pending` | Decisions asked of the caller (all with `all=true` for `approvals.read`) |
| `GET /approvals/{id}` | The decision with the request, its form answers (personal answers seen by others than the requester are audited) and the process; `mayDecide` |
| `POST /approvals/{id}/decision` | `{ decision: Approve or Reject, comment, via }`; rejecting needs the reason; only the approver, while the decision waits and its level is current (409 `already-decided` otherwise); others get 404 |
| `GET /service-requests/{id}/approvals` | The request's processes with levels (Waiting, Approved, Rejected, NotStarted, Cancelled) and decisions; for whoever may see the request |

Approvers may see the request they are asked about, its answers and its documents.

Everything is audited:

- `approvals.started` (stage, number of levels, whether a fallback was used);
- `approvals.approved` and `approvals.rejected` (level, via portal or email);
- `approvals.ended`;
- `approvals.cancelled`.

## Notifications

New B5 events, with bilingual default wording in formal Arabic (`Texts/en.json`, `Texts/ar.json`). Administrators switch them and reword them in Settings → Notifications, and each person can turn off their email.

| Event | To | When |
| --- | --- | --- |
| `approval.requested` | Each approver of a level | The level starts. The email has **Approve** and **Reject** buttons that open `/approvals/{id}?decision=approve` or `reject`. |
| `request.approved` | The requester | Approved before assignment |
| `request.rejected` | The requester | Rejected before assignment (with the reason) |
| `request.signoff-rejected` | The assignee | The sign-off was rejected (with the reason) |

The renderer gained a second, outlined button for these emails, with the same encoding and a plain-text line.

## Permissions

| Permission | Lets you | Default roles |
| --- | --- | --- |
| `workflows.manage` | Settings → Workflow rules; approving when a level names nobody else | Tenant administrator |
| `approvals.read` | See every approval | Tenant administrator, executive / auditor |

Approvers need no permission: being asked is enough. `workflows.manage` cannot be given to integrations (for the product owner's approval). Like notification templates, rules send wording of their own to anyone, and they could route requests past their approvers.

## Demo data

Seeder `c3-workflows` (order 160) gives each sample organisation:

- the sample requester's manager: the team lead;
- a rule that asks for two approvals of a new laptop: the manager, then the approver persona;
- a sign-off by the approver for a salary advance;
- a rule announcing P1 and P2 incidents to the group's team leads.

Requesting a laptop as the sample requester starts a real approval.

## Tests

- **Integration against PostgreSQL** (`WorkflowApiTests`):
  - manager then parallel group approval before assignment, with clocks paused and routing after approval;
  - email notification with the approval link, nobody else may decide, rejection needs a reason, double decisions refused;
  - rejection closes the request, and the requester withdraws;
  - sign-off rejected goes back to the agent with an internal note, then approved resolves;
  - rules: problems listed, round-robin assign, rule notifications, approval by amount (`fee gt 5000`), "Try the rules", delete;
  - Elsa's tables have forced row-level security, and one tenant never sees another's workflow instance.
- **Web**: `src/web/src/components/workflows/workflows.test.tsx`: the rule list order and move buttons, building a rule with the API's problems listed, no approval action for incidents in Arabic, the level timeline, the decision form opened from the email with a reason needed to reject.

## Not in C3

- (Done in E3: approvals in Teams, which decide there, and WhatsApp and SMS messages with the approval links; see `messaging.md`.) Reminders and expiry of waiting approvals, delegation while an approver is away.
- (Done in K1: plain-language rules built by AI and the visual view; see below.)
- (Done in E7: the action *Call a connector*, which queues a REST connector's operation with the change for the worker; see `connectors.md`.)
- Fulfilment tasks, the service's own group and SLAs, and the journey (task C4).

## Plain-language rules and the visual view (task K1, module 20)

Covers module 20's **plain-language rules**: "an admin describes a rule in English or Arabic; Zelvo's AI builds the workflow and shows it visually for review; it only goes live after the admin approves it", and the story "when a VIP raises a critical incident, notify the IT manager on WhatsApp".

Product owner decisions (2026-11-26):

- **The administrator who wrote the rule approves it** (no second person); the AI feature is **off until set up** in Settings → AI.
- **VIP** is a B3 customer group, not a new flag.

**The AI feature `ruleBuilder`.** A tenth AI feature in Settings → AI → Features, with its own provider and model, through the E4 gateway like every call (personal data masked, prompt-injection guards, monthly limit, AI log, audit). The key follows the other features' camelCase (`ruleBuilder`; the plan wrote `rule-builder`).

**Turning words into a draft** (`POST /workflow-rules/plain-language`, `workflows.manage`):

1. The administrator's text (up to 2,000 characters, English or Arabic) goes to the AI as data, with the tenant's own active lists, each item under a reference: services `s1`, categories `c1`, locations `l1`, support groups `g1`, departments `d1`, customer groups `v1`, roles `r1` (up to 300 of each). No ids or people's names are sent; people cannot be named as recipients.
2. The instructions describe the rule schema: triggers, record types, condition fields and operators, the `assign` and `notify` actions, recipients and channels, and the placeholders the wording may use. Anything the AI cannot express goes to `unmapped`.
3. The answer is untrusted. `RuleDraftMapper` (`src/api/Zelvo.Workflows/Rules/RuleDraftMapper.cs`) reads it field by field: references are resolved only from the lists sent; every condition and action is checked with the builder's own checks (`RuleDocuments`); anything unknown (a reference that was not offered, a field or operator the builder lacks, form-answer conditions, approvals, connectors, named people, a channel other than WhatsApp, SMS or Teams, wording with unknown placeholders) is **left out and listed** in `unmapped`, never accepted. A missing or unknown trigger becomes "a ticket is created", with a note.
4. With at least one action left, the rule is saved as a **draft** (`workflow_rules.is_draft`) with the original text (`source_text`, `source_language`), what was left out (`unmapped`) and the AI call (`ai_call_id`), audited `workflows.rule.drafted` with the text. Without any action, 422 `rule-not-understood` lists the reasons (audited `workflows.rule.not-understood`). AI off or the limit reached answer 409 `ai-off` or `limit-reached`; provider failures 502.

`GET /workflow-rules/plain-language` says whether the feature can be used now (`available`, `reason`).

**Drafts never run.** `WorkflowRuleInterceptor` tries only active rules that are not drafts. "Try the rules" includes drafts when asked (`includeDrafts`), marked as drafts, so a draft can be tried on a real ticket before it is approved. Drafts can be changed in the builder (`PUT`, still a draft) or deleted.

**Approval** (`POST /workflow-rules/{id}/approve`, If-Match checked when sent): the draft is checked again like any rule (what it refers to may have changed), then goes live; `approved_by_id` and `approved_at` are set and `workflows.rule.approved` is audited with the original text, the AI call and what was left out. Approving a live rule answers 409 `not-a-draft`.

**What the builder gained for the story:**

- the condition **`requesterCustomerGroup`** (`in` / `notIn` customer groups): the requester is in the group by hand or by email domain (B3's rule, `SlaMatcher.InDomain`), active groups only;
- the recipient **`groupLeads:<group id>`**: the team leads of a named group (besides `groupLeads`, the ticket's own group's);
- **notify by WhatsApp, SMS or Teams** (`channels` on the notify action, E3): the rule decides the channels instead of the event's setting. People are reached where Zelvo can: WhatsApp and SMS to the phone of those who turned them on under Account → Notifications (their phone and their consent), Teams to those linked to the tenant's Teams app; WhatsApp outside the 24-hour window needs the event's template as before. Rules made before K1 have no `channels` and keep following each person's choices.

**The visual view.** Settings → Workflow rules shows each draft as a read-only **when → if → then** diagram (`RuleDiagram`) with the administrator's words and the list of what was left out, and buttons to approve, change in the builder or discard; every existing rule can be shown the same way ("Show … as a diagram"). The diagram is built from headings and lists, so it reads in order with a screen reader; boxes stack on phones and the arrows follow the reading direction in Arabic.

**Demo data.** Seeder `k1-plain-language-rules` (order 270) adds the story as a draft in each sample organisation, in its language (Arabic for the government entity, English for the company): sample VIP customer group, P1, the service desk's team leads, in the app, by email and by WhatsApp. The development `ai-mock` answers `ruleBuilder` with a rule built from the words it recognises.

**Tests.**

- Integration (`PlainLanguageRuleTests`): off until set up; the lists sent by reference; only what fits taken, the rest listed (unknown references, fields, channels, recipients, approvals); a draft never runs; "Try the rules" with drafts; approval audited with the text, twice refused; live, a VIP's P1 tells the IT desk's team lead in the app and on WhatsApp while other P1s and VIP P3s do not; a description without any action refused with the reasons; agents refused.
- Web (`workflows.test.tsx`): a draft from a description shown as a diagram with what was left out, approved with If-Match; the reasons when nothing could be built; the feature off in Arabic; the diagram in Arabic.
