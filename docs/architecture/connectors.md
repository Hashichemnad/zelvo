# Connectors and monitoring alerts (task E7)

Covers the ready-made connectors and the no-code connector builder of **module 14** (integrations and developer tools) in `docs/requirements.md`: identity (Microsoft Entra ID, Active Directory / LDAP; UAE PASS came with D3), CRM (Microsoft Dynamics 365, Salesforce, HubSpot, Zoho CRM), the monitoring alert webhook (Zabbix, Prometheus, Datadog and others), and REST connectors set up on screen and called from workflow steps. Messaging connectors (email, WhatsApp, Teams, SMS) are E2 and E3.

Product owner decisions of 2026-11-25:

1. **CRM: a case per ticket.** Each ticket of a synced contact becomes the CRM's case object (Dynamics *Case*, Salesforce *Case*, HubSpot *Ticket*, Zoho *Case*) on the contact, updated with its status and a note at every state change. Contacts come into Zelvo as requesters and their companies as customer groups, on a schedule (every 15 minutes by default; directories every 60 minutes; both adjustable from 5 minutes to 7 days).
2. **Identity: sync and sign-in.** A scheduled, **one-way** sync of users, departments, managers and group membership into Zelvo: people gone from the source or disabled there are **disabled, never deleted**, and enabled again when they return. **Sign-in through Keycloak**: Entra as an OIDC identity provider ("Sign in with Microsoft"), AD/LDAP as **read-only user federation**, so passwords are checked by the directory.
3. **Monitoring: note, resolve optional.** The same alert firing again adds to its open incident instead of raising a new one; when it clears, an internal note is added, and a per-source switch (**off by default**) also resolves the incident.
4. **REST calls in the background, logged, answer as a note.** A workflow rule queues the call with the change; the worker sends it with retries, **never inside the save**; each call is in the integration log; the answer's mapped values are written as an **internal note** on the ticket and can store an **external reference** (`{{ticket.externalRef}}`).

Code: `src/api/Zelvo.Data/Connectors` and `ZelvoDbContext.Connectors.cs` (data, migration `Connectors`), the new project `src/api/Zelvo.Connectors` (settings, clients, directory and CRM sync, the outbox interceptor, REST runner, call sender, worker job, alert intake), `src/api/Zelvo.Api/Connectors/ConnectorEndpoints.cs` (Settings → Connectors and Monitoring, the alert webhook), `IKeycloakAdmin.ApplyDirectorySignInAsync`, the workflow action in `Zelvo.Workflows` (`ConnectorAction`), `src/api/Zelvo.Api/Demo/DemoConnectorsSeeder.cs`, `src/web/src/components/connectors` and the pages `settings/connectors` and `settings/monitoring`.

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `connectors` | Name, kind (EntraId, Ldap, Dynamics365, Salesforce, HubSpot, Zoho, Rest), settings (JSON per kind), the **credential encrypted** (`Zelvo.Secrets`, purpose per connector; Zoho keeps client secret and refresh token together), on/off, minutes between syncs (5 to 10,080), its system account, next sync, lease, last sync (time, success, counts), sync cursor, last error | Audited (credential masked) |
| `connector_links` | A Zelvo record and its counterpart: person ↔ directory or CRM id, department, customer group ↔ CRM company, ticket ↔ case or other record (the external reference); when the source last had it; whether the sync disabled it | Unique both ways per connector |
| `connector_calls` | The outbox: CRM case updates (`created`, `state`) and REST operations asked for by rules, with the ticket, the rule, status (Pending, Succeeded, Failed), attempts, next attempt, lease, last HTTP status, duration, error | Kept 30 days (`zelvo_purge_connector_calls`); bodies are in the integration log, masked |
| `monitoring_sources` | Name, format (Alertmanager, Generic), field mapping and severity levels (JSON), the **SHA-256 of the token** (the token is shown once), category, location, group, resolve on clear, on/off, its system account, last alert | Audited |
| `monitoring_alerts` | An alert's key at the source (fingerprint, event id), its incident, state (Firing, Resolved), times it fired, first and last seen | One firing alert per key |

Also: the ticket channel `Monitoring`, `ConnectorAction` in workflow rules, and the permission `connectors.manage`. All five tables have forced row-level security (the guard test covers them). The worker finds tenants with work through `zelvo_connector_due_tenants()` (ids and slugs only).

**System accounts.** Connectors that write notes and monitoring sources (the requester and author of their incidents and notes) act as an account of their own: disabled, never signing in, named after the source (`users.subject` starts with `system:`, like C6's anonymous account; left out of the user list).

## Identity directories

- **Entra ID** (Microsoft Graph, client credentials of an app registration with the application permissions `User.Read.All` and `GroupMember.Read.All`): `GET /v1.0/users` with `$expand=manager` across pages, optionally only the transitive members of one group, and the transitive members of each mapped group.
- **AD / LDAP** (Novell.Directory.Ldap, MIT, **new dependency for approval**): LDAPS, StartTLS (required) or no TLS, a read-only service account, a base DN and filter, paged search (500); attribute names are settings (Active Directory defaults: `objectGUID`, `mail`, `displayName`, `department`, `manager`, `mobile`, `employeeID`, `memberOf`; disabled accounts from `userAccountControl`). The library opens its own socket, so Zelvo checks every address of the host against the policy first.
- **Sync** (`DirectorySync`, worker, every connector's interval or "Sync now"): people found by their link, else by email, else made as requester accounts that their first sign-in with that email (verified) takes over (E2's `contact:` accounts); name, phone (E.164, UAE local numbers converted), employee number, department (matched by name, else made, named in the directory's language; the other is listed as missing) and manager follow the directory; disabled or missing people are disabled and their link marked, enabled again when they return; members of mapped directory groups join the Zelvo group, synced people who left it are removed (team leads and people added by hand stay). **Roles stay Zelvo's.** Each sync is audited (`connectors.synced` with counts) and the counts show in Settings.
- **Sign-in.** With "Offer sign-in", saving the connector also configures the tenant's Keycloak realm (same transaction pattern as UAE PASS: if Keycloak refuses, nothing is saved, 502 `identity-provider-unavailable`):
  - Entra: identity provider `entra` (OIDC with the directory's v2.0 endpoints, PKCE S256, `client_secret_post`, emails trusted, import), using the same app and secret; Settings shows the redirect address to register with the app (`{Keycloak}/realms/{realm}/broker/entra/endpoint`). Needs the directory's tenant id (not its domain).
  - LDAP: user federation `zelvo-ldap`, **read-only**, emails trusted, Active Directory or other vendor settings; passwords are checked by the directory.
  - Turning sign-in off, turning the connector off or removing it removes them. One of each per tenant.

## CRMs

| CRM | Sign-in | Contacts in | Case out |
| --- | --- | --- | --- |
| Dynamics 365 | Client credentials in the customer's Entra directory, scope `{environment}/.default` | `contacts` changed since the last sync, with the parent account's name | `incidents` with `customerid_contact`, `annotations` as notes, `statuscode` updates, `CloseIncident` when resolved |
| Salesforce | Client credentials flow of a connected app (`/services/oauth2/token`), instance address only on salesforce.com | SOQL on `Contact` (`LastModifiedDate`) with `Account.Name` | `Case` (`Subject`, `Description`, `ContactId`, `Status`, `Origin`), `CaseComment` notes |
| HubSpot | Private app access token | `contacts/search` by `lastmodifieddate` | `tickets` in the default pipeline associated with the contact (16), notes associated with the ticket (228) |
| Zoho CRM | Refresh token of a self client (client id, secret, refresh token), the data centre's accounts and API domains | `Contacts` with `If-Modified-Since` | `Cases` related to the contact, `Notes` on the case |

- **Contacts** become requesters (link, else email, else a `contact:` account); names and phones only fill in what Zelvo lacks (people may be users of their own). With "customer groups", each company becomes a B3 customer group with its contacts as members, for SLA policies by customer.
- **Cases** (`ConnectorOutboxInterceptor`): a ticket created for a requester linked to an active CRM, or whose state changes, gets a `connector_calls` row in the same transaction. The worker creates the case with the number, title, description and a link to the ticket, then updates its status and adds a note (`INC-000123: Resolved` in the tenant's language, with the resolution notes) at every change. Statuses per state are settings (`statusMap`); defaults per CRM: Dynamics 1 / 3 / 5, Salesforce New / Working / Closed, HubSpot pipeline stages 1–4, Zoho New / Escalated / On Hold / Closed.

## REST connectors (the no-code connector builder)

- **Settings:** base address (HTTPS; internal only when the operator allows it), authentication (none, key in a header, Basic, Bearer, OAuth 2.0 client credentials), headers without secrets (Authorization and Host refused), and up to 30 **operations**: key, bilingual name, method, path and JSON body with placeholders, values to read from the answer (dotted paths, bilingual names) and the path of the other system's id.
- **Placeholders:** `ticket.id`, `ticket.number`, `ticket.title`, `ticket.description`, `ticket.state`, `ticket.type`, `ticket.priority`, `ticket.channel`, `ticket.group`, `ticket.category`, `ticket.url`, `ticket.createdAt`, `ticket.resolutionNotes`, `ticket.externalRef`, `requester.name`, `requester.email`, `requester.phone`, `requester.employeeId`, `tenant.name`. Unknown ones are refused when saved. Values are URL-encoded in the path and JSON-escaped in the body; the rendered address must stay on the base address's host and path (`path-outside-base`).
- **Test before saving** (`POST /connectors/rest/test`): the settings in the request (saved or not) with sample values or an existing ticket's, showing what was sent (personal data masked), the answer (masked, cut) and the values read. A saved connector's credential is used only while its address and sign-in stay the same.
- **From workflow rules:** the action *Call a connector* (`{"type":"connector","connectorId","operation"}`, checked against the connector's operations) on any trigger. `WorkflowRuleInterceptor` queues a `connector_calls` row with the change; the worker renders the placeholders from the ticket as it is then, sends the call, and writes the values read as an **internal note** by the connector's account and the id as the ticket's external reference. 2xx succeeds; 4xx (except 408 and 429) fails at once; others are tried again after 1 min, 5 min, 15 min, 1 h, 3 h and 6 h; "Send again" in the call log.

## Monitoring alerts

`POST {Channels:PublicUrl}hooks/{tenant}/monitoring/{source}` (the proxy's `/hooks/` route of E3) with the source's token as `Authorization: Bearer` or `?token=` (compared as SHA-256 in constant time; the API's bearer handler ignores `/api/v1/hooks`).

- **Alertmanager:** its webhook (`alerts[]` with `status`, `fingerprint`, `labels.alertname`, `labels.severity`, `labels.instance`, `annotations.summary`, `annotations.description`); severities critical → impact 1 / urgency 1, warning → 2 / 2, info → 3 / 3.
- **Generic** with a mapping (dotted paths of the alert list, key, status, title, description and severity; statuses meaning cleared; severity levels), with **presets**: Zabbix (a webhook media type posting `eventId`, `status` PROBLEM/RESOLVED, `severity`, `subject`, `message`; Disaster → 1/1 … Information → 3/3) and Datadog (`alertId`, `transition`, `title`, `message`, `priority` P1–P5).
- A new alert raises an incident (channel Monitoring, the source's group or category then B2's rules, location, priority from the matrix, B3 clocks); the same key while firing adds an internal note ("The alert fired again (n times)"); clearing adds a note and, with *resolve on clear*, resolves the incident; an alert whose incident was closed raises a new one. Each request is audited (`monitoring.alerts.received` with counts) and answers 202 with them. Renewing the token stops the old one at once.

## API

All under `/api/v1`, tag *Connectors*.

| Endpoint | Who | Does |
| --- | --- | --- |
| `GET`, `POST /connectors`, `GET`, `PATCH`, `DELETE /connectors/{id}` | `connectors.manage` | Connectors; credentials never returned; a new directory, server, address or app needs the credential again |
| `POST /connectors/{id}/test`, `/sync` | `connectors.manage` | Test the connection; sync now |
| `GET /connectors/{id}/calls`, `POST …/calls/{callId}/resend` | `connectors.manage` | Calls for tickets; send again |
| `POST /connectors/rest/test` | `connectors.manage` | The REST builder's test |
| `GET /connectors/rest/operations` | `workflows.manage` | REST connectors and operations for the rule builder |
| `GET`, `POST /monitoring/sources`, `PATCH`, `DELETE /monitoring/sources/{id}`, `POST …/token`, `GET …/alerts` | `connectors.manage` | Monitoring sources (token shown once) and their alerts |
| `POST /hooks/monitoring/{id}` | The monitoring tool (its token) | Alerts |

`connectors.manage` belongs to tenant administrators and is **not available to integrations** (for the product owner's approval, like `channels.manage`: it holds credentials, and a connector sends ticket data to another system). Problem codes: `connector-not-found`, `source-not-found`, `call-not-found`, `not-failed`, `not-synced`, `token-invalid`, `identity-provider-unavailable`; rule problems `connector-required`, `connector-unknown`, `operation-unknown`.

## Configuration (operator)

| Setting | Default | Meaning |
| --- | --- | --- |
| `Connectors:Network:AllowedPrivateNetworks`, `AllowInsecureHttp` | none, off | Internal networks connected systems may be on (directories usually are) |
| `Connectors:Timeout`, `WorkInterval`, `RetryDelays`, `CallRetention`, `MaxRecords` | 60 s, 10 s, 1 min … 6 h, 30 days, 100,000 | Calls and syncs |
| `Connectors:MicrosoftLoginUrl`, `GraphUrl`, `HubSpotUrl`, `SalesforceApiVersion` | public clouds, `v62.0` | Sovereign clouds or tests |
| `Connectors:WebUrl` | `Notifications:WebUrl` | Links to tickets in cases |

## Demo

Seeder `e7-connectors` (order 260): an Entra ID connector (government entity) or Active Directory (company) and a CRM (Dynamics 365 or Salesforce), all **turned off**; a REST connector "Case system" with an "update the case" operation and the (turned off) rule "Resolved complaints update the case system"; a Prometheus source whose disk alert raised a P1 incident that fired twice.

## Tests

- Integration against PostgreSQL (`ConnectorTests`): an Entra directory synced (new, linked and disabled people, phone in E.164, departments in both languages, managers, group members), people leaving disabled and removed from groups, sign-in configured in Keycloak and removed, the credential needed again for a new app; CRM contacts as requesters and a customer group, the incident's case created and then closed with a note, calls logged with masked bodies; the REST builder's test with sample values, unknown placeholders refused, the rule action checked against operations, the call sent by the worker after the save with the ticket's values, the answer as an internal note and external reference, a refusal failing at once and sent again; Alertmanager alerts raising a P1 incident, repeats as notes, clearing resolving it, wrong and renewed tokens, Zabbix's preset.
- Unit (`ConnectorProviderTests`): Graph users across pages with managers and groups and the app token; Salesforce's client credentials, case and comment with one token; HubSpot's associations; Zoho's refresh token and header; REST authentication and error classes; REST settings problems; LDAP entries (binary `objectGUID`, manager, mapped groups, disabled accounts).
- Web (`connectors.test.tsx`): a directory's sync counts, redirect address and "Sync now"; the REST builder's test in Arabic; a Zabbix source's token shown once. The rule builder offers *Call a connector* when there are REST connectors.

## Not in E7

- Writing to directories or CRMs beyond cases and notes (contacts and accounts are read), CRM custom objects, two-way sync of case changes back into Zelvo.
- Pushes from the systems (Graph change notifications, CRM webhooks): Zelvo polls, so offline installations work.
- REST answers that change ticket fields other than the note and the external reference.
- **Form fields that look up values in other systems** (C1 left them for E7, decision 2026-11-24): not built here. The field model still leaves room for them; they need their own decisions (who may query, caching, personal data) and are listed for the product owner.
- Not tried against real Entra, Active Directory, Dynamics 365, Salesforce, HubSpot, Zoho, Zabbix or Datadog systems in this session (no access); the clients follow the documented APIs and are tested against recorded requests.
