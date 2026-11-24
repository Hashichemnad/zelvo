# Directory and assignment (task B2)

Covers **module 10** (admin: users and groups, roles and permissions, categories) and the assignment part of **module 1** ("assignment to groups and agents by rules (category, location, language, skill) or round-robin") of `docs/requirements.md`.

Product owner decisions of 2026-11-23:

1. **Categories and locations are trees of up to 3 levels** (for example Hardware → Laptop → Battery; Site → Building → Floor), with names in English and Arabic, shared by all record types. They can be made inactive; they can be deleted only while nothing uses them. A rule on a parent also matches its children.
2. **Assignment rules are ordered; the first match wins.** Each rule has optional conditions (category, location, requester language) and sends the ticket to a group, then either leaves it in the group's queue or picks an agent by round-robin (optionally only agents with a required skill). A tenant default catches tickets no rule matches. Rules run when a ticket is created and when an agent asks to "Route again"; they never reassign a ticket on their own later.
3. **Round-robin picks available, qualified members**: active members of the group who are marked "available for assignment", may work on incidents, have the rule's required skill and handle the requester's language when the rule names one. It takes the member whose last ticket from this group came longest ago. When nobody qualifies, the ticket stays in the group's queue.
4. **The assignee belongs to the group.** Incidents get an assignment group; when a group is set, the assignee must be one of its members. Groups have members and team leads. Each user's profile lists their skills (defined by administrators, bilingual) and the languages they handle (English, Arabic). Customer groups for SLAs come with B3.

Code: `src/api/Zelvo.Data/Directory` (entities), `src/api/Zelvo.Tickets/Routing.cs` (the router), `src/api/Zelvo.Api/Directory` (API), `src/web/src/app/[locale]/settings/{users,roles,groups,categories,locations,skills,assignment}` and `src/web/src/components/directory` (screens).

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `categories` | Name (en/ar), parent, depth (1–3), position among siblings, active | Audited |
| `locations` | Same shape as categories | Audited |
| `skills` | Name (en/ar), active | Audited |
| `user_skills` | User ↔ skill | Audited |
| `support_groups` | Name (en/ar), description (en/ar), active | Audited |
| `support_group_members` | Group ↔ user, team lead flag | Audited |
| `support_group_rotation` | When each member last got a ticket from the group by round-robin | Not audited (the assignment itself is); one row per group and member |
| `assignment_rules` | Name (en/ar), position, active, conditions (category, location, language), group, method (`GroupQueue` or `RoundRobin`), required skill | Audited |

`users` gains `available_for_assignment` (default true) and `languages` (`en`, `ar`; empty by default). `tickets` gains `category_id`, `location_id`, `group_id` and `routing_rule_id` (the rule that last routed it; null when the default or a person placed it). The default for unmatched tickets is the tenant setting `assignment.default` (`{ "groupId": null, "method": "GroupQueue" }` by default).

All new tables have forced row-level security; the guard test covers them.

## Routing

`TicketRouter.RouteAsync` (in `Zelvo.Tickets`, so channels in the worker can use it later):

1. The requester's language: their profile language, else the tenant default.
2. Active rules in order. A rule matches when each condition it has is met: the ticket's category is the rule's category **or below it**; the same for location; the requester's language equals the rule's. Rules whose group is inactive are skipped.
3. No rule matched: the tenant default (`assignment.default`); with no default group the ticket stays without a group.
4. `GroupQueue`: the ticket gets the group and no assignee. `RoundRobin`: inside the transaction the group's row is locked (`SELECT … FOR UPDATE`), so two tickets routed at the same time go to different people. Candidates: active members who are available for assignment, whose roles hold `incidents.edit`, who have the rule's skill (if any) and handle the rule's language (if any). The one with the oldest last round-robin assignment from this group (never first, then by id) gets the ticket and their rotation row is updated. Nobody qualifies: the ticket stays in the queue.

Routing happens:

- when an incident is created without a group or assignee (always for requesters' reports);
- on `POST /api/v1/incidents/{id}/route` ("Route again", `incidents.assign`, open incidents only).

Manual changes (`PUT …/assignment`) clear `routingRuleId`.

## Permissions

| Permission | Built-in roles | Gives |
| --- | --- | --- |
| `directory.read` | agent, team lead, administrator, executive/auditor | See groups with their members, skills and assignment rules |
| `directory.manage` | administrator | Change categories, locations, skills, groups and members, assignment rules and the default |
| `users.manage` (A2) | administrator | Also: add users, change their skills, languages and availability |

- Every signed-in user can read **active** categories and locations (requesters choose them when reporting). Inactive ones need `directory.read`.
- **Team leads of a group** (members with the lead flag) may add and remove that group's members (not leads) and change the availability of its members without `directory.manage`.
- Everyone with `incidents.edit` may change their own availability (`PUT /api/v1/me/availability`).

Field resource `users` gains `skills`, `languages`, `availableForAssignment` (writable) and `groups` (read only). Field resource `incidents` gains `category`, `location` (writable) and `group`. Requesters see the category and location of their own incidents, not the group.

## API

All under `/api/v1`, OpenAPI tags "Directory", "Users", "Roles" and "Incidents". Bilingual values use the usual shape: responses `{"en": "…", "ar": "…", "missing": ["ar"]}`, requests `{"en": "…", "ar": "…"}` (at least one). Lists that are not paged return arrays.

### Categories and locations (same shape; replace `categories` with `locations`)

| Method and path | Permission | Body / query | Answer |
| --- | --- | --- | --- |
| `GET /categories` | signed in | `?includeInactive=true` (needs `directory.read`) | `TreeNode[]`, flat, ordered by path |
| `POST /categories` | `directory.manage` | `{ name, parentId?, active? }` | 201 `TreeNode` |
| `PATCH /categories/{id}` | `directory.manage` | `{ name?, active?, position? }` | `TreeNode` |
| `DELETE /categories/{id}` | `directory.manage` |  | 204, or 409 `in-use` (children, tickets or rules use it) |

`TreeNode`: `{ id, parentId, depth, position, name, path: LocalizedText /* "Hardware › Laptop" */, active, childCount }`. Problems: `too-deep` (409, a fourth level), `parent-not-found` (422), `name-taken` (409, same name in either language under the same parent). Deactivating a node also hides it from new tickets but keeps it on existing ones; its children stay as they are, and inactive nodes and their children cannot be chosen.

### Skills

`GET /skills` (`directory.read`; `?includeInactive=true`) → `Skill[]` `{ id, name, active, userCount }`; `POST /skills` `{ name, active? }`, `PATCH /skills/{id}` `{ name?, active? }`, `DELETE /skills/{id}` (409 `in-use` while a rule needs it; users lose it). All `directory.manage`.

### Groups

| Method and path | Permission | Body | Answer |
| --- | --- | --- | --- |
| `GET /groups` | `directory.read` | `?includeInactive=true` | `GroupSummary[]` `{ id, name, description, active, memberCount, leadCount }` |
| `GET /groups/{id}` | `directory.read` |  | `Group` = summary + `members: [{ userId, displayName, isLead, availableForAssignment, languages, skills: [{id, name}], status, lastAssignedAt }]` |
| `POST /groups` | `directory.manage` | `{ name, description?, active? }` | 201 `Group` |
| `PATCH /groups/{id}` | `directory.manage` | `{ name?, description?, active? }` | `Group` (409 `in-use` when deactivating a group the default or an active rule uses) |
| `DELETE /groups/{id}` | `directory.manage` |  | 204, or 409 `in-use` (tickets or rules use it) |
| `PUT /groups/{id}/members/{userId}` | `directory.manage`, or lead of the group (not for `isLead: true`) | `{ isLead: false }` | `Group`; 422 `userId` when the user is not active staff (roles without `incidents.edit` cannot be members) |
| `DELETE /groups/{id}/members/{userId}` | `directory.manage`, or lead of the group (not for leads) | | `Group` (open incidents assigned to that person in this group keep their assignee) |

### Users (A2 endpoints, extended)

- `GET /users?q=&role=&status=&page=&pageSize=`: `q` matches the display name, and the email when the caller may see emails.
- `POST /users` (`users.manage`): `{ email, displayName?, roles?: string[] }` → 201 `User`. Creates the person in the tenant's Keycloak realm (as bulk import does) and the Zelvo record; 409 `user-exists`.
- `PATCH /users/{id}` gains `availableForAssignment` and `languages` (`["en","ar"]`).
- `PUT /users/{id}/skills` `{ skillIds: [] }` (`users.manage`).
- `PUT /users/{id}/availability` `{ available }`: `users.manage`, or lead of a group the user belongs to.
- `User` gains `skills: [{id, name}]`, `languages`, `availableForAssignment`, `groups: [{id, name, isLead}]`.
- `PUT /me/availability` `{ available }` (holders of `incidents.edit`); `GET /me` gains `availableForAssignment` and `groups`.

### Roles (A2 endpoints, extended)

- `POST /roles` (`roles.manage`): `{ key, name, requiresMfa? }` → 201 `Role`. Custom roles start with no permissions; keys are `^[a-z][a-z0-9-]{1,49}$` and not a built-in key.
- `DELETE /roles/{key}`: custom roles only (409 `role-system`), and only while no user or API client has it (409 `in-use`).

### Assignment rules

| Method and path | Permission | Body | Answer |
| --- | --- | --- | --- |
| `GET /assignment-rules` | `directory.read` |  | `Rule[]` ordered by position |
| `POST /assignment-rules` | `directory.manage` | `RuleInput` | 201 `Rule` (added last) |
| `PUT /assignment-rules/{id}` | `directory.manage` | `RuleInput` | `Rule` |
| `DELETE /assignment-rules/{id}` | `directory.manage` |  | 204 |
| `PUT /assignment-rules/order` | `directory.manage` | `{ ids: [] }` (every rule exactly once) | `Rule[]` |
| `GET /assignment-rules/default` | `directory.read` |  | `{ groupId, method }` |
| `PUT /assignment-rules/default` | `directory.manage` | `{ groupId, method }` | same |
| `POST /assignment-rules/test` | `directory.read` | `{ categoryId?, locationId?, language? }` | `{ ruleId, ruleName, groupId, groupName, method, assignee: {id, displayName} | null }`: what would happen now, without assigning or moving the rotation |

`RuleInput`: `{ name, active?, categoryId?, locationId?, language?: "en"|"ar", groupId, method: "GroupQueue"|"RoundRobin", skillId? }` (`skillId` only with `RoundRobin`). `Rule` = the same with `id`, `position`, and `category`, `location`, `group`, `skill` as `{ id, name }` (category and location with their `path`).

### Incidents (B1 endpoints, extended)

- Responses gain `category: { id, name, path } | null`, `location` (same), `group: { id, name } | null` and `routingRule: { id, name } | null` (staff only).
- `POST /incidents` accepts `categoryId` and `locationId` from everyone (active nodes only), and `groupId` with `incidents.assign`. Without `groupId` and `assigneeId` the incident is routed.
- `PATCH /incidents/{id}` accepts `categoryId` and `locationId` (null clears them). It does not route again.
- `PUT /incidents/{id}/assignment` `{ groupId, assigneeId }` (`incidents.assign`): both values replace the current ones; the assignee must be a member of the group when a group is given (422 `assigneeId`, "not a member of the group"). `PUT …/assignee` (B1) still works and checks membership of the current group.
- `POST /incidents/{id}/route` (`incidents.assign`): routes again; 409 `ticket-not-open` for resolved and closed incidents. Answers the incident.
- `GET /incidents?group=mine|none|<id>&category=<id>&location=<id>`: `mine` = groups the caller belongs to; category and location include what is below them.
- `GET /incidents/assignees?groupId=<id>`: only that group's members.

## Screens

Settings (each item shown only with its permission): **Users** (search, filters by role and status, add user; user page with name, status, Emirates ID, roles, languages, skills, availability, groups), **Roles** (list; role page with name, second factor, permissions by area, field rules; add and delete custom roles), **Groups** (list; group page with name, description, active, members with lead flag and availability, add member), **Categories** and **Locations** (tree: add child, rename in both languages, move up and down, activate, delete), **Skills**, and **Assignment** (rules in order with move up and down, rule form, default for unmatched tickets, "Try the rules" panel).

Incidents: category and location on "Report an incident" (optional) and "Log an incident"; group on "Log an incident"; staff incident page shows category, location, group, the rule that routed it, a group-then-assignee picker and "Route again"; incident list filters by group. Account page: "Available for assignment" switch for staff.

English and Arabic, right to left, WCAG 2.2 AA.

## Demo data

`DemoDirectorySeeder` (`b2-directory`) gives each sample organisation categories, locations (emirate → building → floor), skills, three groups with the demo agent and team lead as members, rules (for example network problems at the main building to the network group by round-robin, Arabic requesters to the Arabic desk) and a default group; the B1 sample incidents get categories, locations and groups.

## Tests

- Unit: rule matching (descendants, language, first match, inactive groups), round-robin order (`TicketRouterTests`).
- Integration against PostgreSQL (`DirectoryApiTests`, `AssignmentApiTests`): tree rules (depth, names, in use, inactive), groups and members (lead rights, membership needs staff), users (add, skills, languages, availability, search), custom roles, rules CRUD and order, routing on create and "Route again", round-robin fairness and 20 concurrent reports spread over the members, nobody available, assignee must be in the group, filters, history with names, row-level security and tenant isolation.
- Web component tests and end-to-end journeys (`directory.spec.ts`): an administrator builds a category, a group and a round-robin rule; a requester reports an incident in that category; it lands with the group's agent; English and Arabic with axe checks.

## Not in B2

- Customer groups and SLA policies (B3); notifying groups or agents of new assignments (B5, now built: see `notifications.md`); the no-code workflow builder for assignment (C3), which will be able to call the same router.
- Assignment by workload (fewest open tickets), working hours and out-of-office calendars: round-robin uses the availability switch only.
- Bulk import of categories, locations, groups and members (bulk import stays users only, E1).
- Webhook events for directory changes.
