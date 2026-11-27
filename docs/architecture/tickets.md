# Tickets and incidents (task B1)

Covers **module 1** (incident management) of `docs/requirements.md` and the shared ticket core that later record types (service requests, complaints, inquiries, suggestions) build on. Product owner decisions of 2026-11-22: numbering, reopening and auto-close, the priority matrix, attachments in PostgreSQL, malware scanning with ClamAV, and simple requester pages.

Code: `src/api/Zelvo.Data/Tickets` (entities), `src/api/Zelvo.Tickets` (life cycle, settings, search documents, attachment store, scanner, worker jobs), `src/api/Zelvo.Api/Incidents` (API), `src/web/src/components/incidents` and the `incidents`, `my-incidents` and `settings/incidents`, `settings/attachments` pages.

## Data

| Table | Holds | Notes |
| --- | --- | --- |
| `tickets` | Shared core: type, number, title, description, state, channel, requester, assignee, parent, follow-up of, on-hold reason, resolution notes, reopen count, timestamps | Every field change audited; `xmin` version for If-Match |
| `incidents` | Impact, urgency, priority, major-incident flag | One row per incident ticket, same id |
| `ticket_number_sequences` | Last number per tenant and record type | Taken with `INSERT … ON CONFLICT DO UPDATE … RETURNING` inside the request's transaction: concurrent requests wait on the row, a rollback gives the number back, so numbers never repeat or skip |
| `ticket_attachments` | File name (cleaned), media type (from the type), size, SHA-256, scan status and result, uploader | Audited |
| `ticket_attachment_contents` | The file's bytes | The PostgreSQL attachment store; not audited (the attachment is) |
| `major_incident_updates` | Bilingual timeline updates, audience (Internal or Requesters) | Audited |

All six tables have forced row-level security (`EnableTenantIsolation`); the guard test covers them. Worker jobs find tenants with work through `SECURITY DEFINER` functions that return only ids and slugs (`zelvo_tenants_with_resolved_tickets`, `zelvo_tenants_with_pending_scans`) and then work with that tenant set.

## Numbers

`INC-000123`: the record type's prefix (tenant setting `tickets.numberPrefixes`, `INC` by default; 1–10 capital letters or digits, different per type), a hyphen and at least six digits. Numbers are never reset or reused; changing the prefix applies to new incidents only.

## Life cycle

```
New ──assign──▶ Assigned ──▶ In progress ⇄ On hold (reason)
 │                 │              │
 └────────────────┴──────────────┴──▶ Resolved (notes) ──▶ Closed (final)
                                          │
                                          └──▶ Reopened ──▶ In progress …
```

- Assigning a new incident makes it Assigned; removing the assignee of an Assigned incident makes it New again. Starting work needs an assignee.
- On hold needs an **active** on-hold reason (tenant setting `incidents.onHoldReasons`; reasons in use can be made inactive but not removed). Resolving needs resolution notes.
- **Reopening:** the requester or an agent can reopen a resolved incident until it closes. **Auto-close:** the worker (`TicketAutoCloseJob`, every minute) closes incidents that stayed resolved for the tenant's period (`tickets.autoCloseDays`, 5 calendar days by default; with `tickets.autoCloseBusinessDays` the days are working days of the default business calendar, task B3). **Closed is final**: nothing changes any more; a follow-up is a new incident with `followUpOfId` pointing to the closed one.
- **Parent and child** (one level): a parent has no parent, a child has no children. Resolving a parent resolves its open children with the same notes (module 1 story).
- **Major incidents:** holders of `incidents.major` (team leads, administrators) declare or stand down a major incident and post bilingual updates to its timeline, for staff only or also for requesters. Requesters of the incident **and of its child incidents** see the updates meant for them.

## Priority

Priority comes from the tenant's impact × urgency matrix (`incidents.priorityMatrix`): 3 impact and 3 urgency levels (1 is the highest) and 5 priorities with names in English and Arabic; by default high/high gives P1 and low/low P5. Administrators change the names and the priority of each combination in Settings → Incidents. A changed matrix gives new priorities to incidents as they change (the stored priority is recalculated on every impact or urgency change). Requesters' incidents start at medium impact and urgency (P3 by default); staff set impact and urgency.

## Who sees and does what

| Permission | Built-in roles | Gives |
| --- | --- | --- |
| `incidents.read` | agent, team lead, administrator, executive/auditor | Every incident, filtered by field rules (resource `incidents`), and the history |
| `incidents.create` | agent, team lead, administrator | Log incidents for others, from any channel, with impact and urgency |
| `incidents.edit` | agent, team lead, administrator | Change title, description, impact, urgency, state, parent; attach and remove any file |
| `incidents.assign` | agent, team lead, administrator | Assign incidents to users whose roles hold `incidents.edit` |
| `incidents.major` | team lead, administrator | Declare major incidents and post to their timeline |

Requesters need no permission for their own incidents: they report them (channel Portal), see a smaller view (no assignee, impact, urgency, channel, parent or children), attach and remove their own files, read updates meant for requesters and reopen resolved incidents. Anything else answers 404, so incident ids cannot be probed.

## API

`/api/v1/incidents` (OpenAPI tag "Incidents"): list with filters (`state`, `open`, `priority`, `major`, `assignee=me|none|<id>`, `parentId`, `number`, `mine`), create, get (with `ETag`), `PATCH` (with optional `If-Match`; 412 `version-mismatch`), `PUT …/assignee`, `POST …/transitions`, `PUT …/parent`, `PUT …/major`, `GET/POST …/updates`, `GET …/history`, `GET /assignees`, and attachments (below). Settings: `GET/PUT /api/v1/settings/incidents` and `/api/v1/settings/attachments` (`settings.manage` to change; every signed-in user can read, because screens show the names).

Problem codes: `incident-not-found`, `ticket-closed`, `transition-not-allowed`, `assignee-required`, `parent-has-parent`, `incident-has-children`, `not-major-incident`, `on-hold-reason-in-use`, `version-mismatch`, `attachment-not-found`, `attachment-too-large`, `attachment-type-not-allowed`, `attachment-content-mismatch`, `attachment-not-available`.

**History** (`GET …/history`) reads the audit log entries of the ticket and its incident details (field changes with old and new values, plus attachment and timeline actions recorded against the ticket), leaves out fields the caller may not see, and shows people and incidents by name and number.

**Search:** each incident is a search document (`incident`) written through the outbox with every change. Readers: the requester, the assignee and the roles that hold `incidents.read` when the incident changes (a role given that permission later finds older incidents after a rebuild in Settings → Search).

## Attachments

Product owner decision 2026-11-22: attachments in PostgreSQL behind `IAttachmentStore` (S3-compatible object storage for medium and large installations came with F6: `Attachments:Storage=S3`, `ObjectAttachmentStore`; the content row stays as the pointer under row-level security, objects are written before the transaction commits under `attachments/<tenant>/<file>`, branding images and rows stored before the switch stay in the database, and the worker's `ObjectStorageSweeper` removes objects whose row is gone; see `release.md`), a size limit and a file-type allowlist per tenant, and malware scanning with ClamAV before anyone can open a file.

1. **Upload** (`POST …/attachments`, multipart field `file`): the requester or holders of `incidents.edit`, while the incident is not closed. Refused when larger than the tenant's limit (`attachments.policy`, 25 MB default, at most 100 MB), when the type is not allowed, or when the content does not match the type (magic bytes for PDF, images, Office and ZIP files, Outlook messages; text must be UTF-8 without NUL). The file name is cleaned of folders and of control and bidirectional-override characters; the media type comes from the type. The file is stored as **Pending**.
2. **Scan** (worker, `AttachmentScanJob`, every 2 seconds): each pending file is streamed to ClamAV (`INSTREAM`). Clean files become **Clean**; infected files become **Infected**, their content is deleted and a Security audit entry `tickets.attachment.infected` records the file name, hash and signature. When ClamAV is unavailable, files stay Pending (not downloadable) and are tried again.
3. **Download** (`GET …/attachments/{id}/content`): only Clean files (or **NotScanned** files when scanning is turned off); anything else answers 409 `attachment-not-available`. Always `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox` and `Cache-Control: private, no-store`. Each download is audited (`tickets.attachment.downloaded`, category View).
4. **Remove** (`DELETE`): the uploader or holders of `incidents.edit`, while the incident is not closed; audited.

### ClamAV

- Compose service `clamav`: the official `clamav/clamav` image, unmodified (GPL-2.0), with its definitions in the `clamav-db` volume. clamd accepts streams up to 105 MB (`CLAMD_CONF_StreamMaxLength`). Only the worker connects, inside the Compose network.
- **Updates (task M1):** freshclam in the image reads Zelvo as its private mirror: administrators import definition files in Settings → Updates → Malware definitions (also for offline installations), and with "Download definitions automatically" on ClamAV downloads from ClamAV's own servers when Zelvo has nothing newer. See `updates.md` (Malware definitions). Operators can still copy files into ClamAV with `scripts/clamav-import-definitions.sh <folder>`, which checks each file with `sigtool`, moves them into place and reloads clamd without a restart.
- **Memory:** about 1–1.5 GB with the full definitions (1.0 GB measured idle with 1.5.3), briefly about twice that while new definitions load; included in the server sizing.
- **Turning scanning off** (`AttachmentScanning__Enabled=false` for the API and the worker, `ZELVO_ATTACHMENT_SCANNING` in Compose): files are stored as NotScanned and can be downloaded; Settings → Attachments says so.

## Settings → Incidents and Settings → Attachments

Administrators (`settings.manage`) edit the matrix (level names in English and Arabic, priority per combination), on-hold reasons (key, names, active), the number prefix and the auto-close period; and the largest file and accepted types. Names missing a language show in the missing-translation report (LANG-5). Changes are audited as tenant settings.

## Screens

- **Requesters:** "My incidents" (list), "Report an incident" (title and description; follow-up of a closed incident) and the incident page (state, resolution, updates meant for them, attachments, reopen, report a follow-up). Built from components in `src/components/incidents` so the self-service portal (D2) reuses them.
- **Staff:** "Incidents" (filters by state, priority, assignee, still open, major), "Log an incident" (requester, channel, impact, urgency, assignee, with a priority preview) and the incident page (facts, work panel, major-incident timeline, attachments, history). The one-screen agent workspace is built in B4 (`workspace.md`); the requester's incident page also shows the conversation (public replies) and lets them answer.
- English and Arabic, right to left, WCAG 2.2 AA (axe checks in `tests/e2e/specs/incidents.spec.ts` and `settings.spec.ts`).

## Demo data

`DemoIncidentsSeeder` (`b1-incidents`) adds six incidents to each sample organisation in its main language: a major incident with two child incidents and two timeline updates (one for requesters, one internal), and incidents that are new, assigned, on hold, resolved and closed.

## Tests

- Unit: life cycle, matrix, numbers, file names and content checks, ClamAV replies (`TicketWorkflowTests`).
- Integration against PostgreSQL (`IncidentApiTests`, `IncidentAttachmentTests`): reporting and the requester's view, staff-only fields, 20 concurrent reports numbered 1–20, the full life cycle and reopening, closed is final and follow-ups, assigning, matrix changes and refused settings, If-Match, parent and children, major incidents and who sees which update, history with old and new values, the executive's read-only access, auto-close, tenant isolation; uploads, scanning (clean, infected, scanner down), type and size rules, access, removal, scanning off, the ClamAV wire protocol. `RealClamAvTests` runs against a real ClamAV when `ZELVO_TEST_CLAMAV=host:port` is set.
- Web component tests (`incidents.test.tsx`, `IncidentSettingsForm.test.tsx`) and the end-to-end journey (`incidents.spec.ts`): report, attach a clean and an infected file, assign, work, resolve, reopen; in English and Arabic.

## Not in B1

- Groups, categories, locations, skills and assignment rules or round-robin (B2, now built: see `directory-assignment.md`); SLA timers and business calendars (B3, now built: see `sla.md`); the one-screen agent workspace, public replies and internal notes (B4, now built: see `workspace.md`); notifications about incidents and timeline updates (B5, now built: see `notifications.md`); creating incidents from email, chat and WhatsApp (E2, E3) and API keys for integrations (E1). The data model already records the channel.
- Groups, categories, locations, skills and assignment rules or round-robin (B2, now built: see `directory-assignment.md`); SLA timers and business calendars (B3, now built: see `sla.md`); the one-screen agent workspace, public replies and internal notes (B4); notifications about incidents and timeline updates (B5, now built: see `notifications.md`); creating incidents from email and chat (E2, now built: see `channels.md`) and WhatsApp (E3) and API keys for integrations (E1). The data model already records the channel.
- A search box on the incident list (the search API already finds incidents).
- Importing malware definitions in Settings: proposed as task M1 of `docs/phase2-build-plan.md` (not in F4).
