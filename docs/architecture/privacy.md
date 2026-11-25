# Privacy and the UAE PDPL (task F5)

Covers **GOV-5** (UAE Personal Data Protection Law and sector rules: consent, retention periods, data subject requests, export and deletion). **GOV-6** (UAE Information Assurance) is covered by the control mapping in [`docs/security/uae-ia-mapping.md`](../security/uae-ia-mapping.md); the PDPL mapping is in [`docs/security/uae-pdpl-mapping.md`](../security/uae-pdpl-mapping.md).

Code: `src/api/Zelvo.Api/Privacy`, entities in `src/api/Zelvo.Data/Privacy`, migration `Privacy`.

## Product owner decisions (2026-11-25)

1. **Audit log kept as the legal record.** Erasing a person never changes the append-only, hash-chained audit log (A1). Screens show their entries as "Erased person" without a name (`actor.erased` in `GET /audit/events`; no actor name in an incident's history). The privacy notice template says the organisation keeps this record.
2. **Erasure anonymises, it does not delete tickets.** Tickets stay for reports and SLA history but lose their personal data; staff notes and replies stay; payments and receipts stay (financial records).
3. **Retention: anonymise when the period ends, off by default.** Nothing changes until an administrator sets a period; a preview shows how many records a period would reach.
4. **Consent: a versioned privacy notice plus optional purposes.** People accept the notice at first sign-in and again on a new version; visitors accept it before chatting or sending an anonymous message; administrators define optional purposes that people grant or withdraw, with the full history kept.

## Data

| Table | What | Notes |
| --- | --- | --- |
| `privacy_notices` | Versions of the notice: bilingual title and Markdown body, Draft → Published → Superseded | One draft and one published version per tenant (unique partial indexes). Publishing needs both languages. |
| `consent_purposes` | Optional purposes (`key`, bilingual name and description, active, order) | The key never changes (integrations use it); inactive purposes keep their history. |
| `consent_records` | The consent history: notice accepted, purpose granted or withdrawn, with channel (Web, Chat, Anonymous, Staff, Api) and who recorded it | **Never changed** (trigger `zelvo_consent_records_immutable`, also for the owner). Visitors are recorded by what they did (`chat:<id>`, `ticket:<id>`), never by email. Kept when a person is erased: it holds only ids. |
| `data_subject_requests` | `DSR-000123` per tenant: type, state, subject, channel, details, identity check, handler, due date, resolution, exported and erased times | Numbers under a per-tenant advisory lock, never reused. |
| `users.erased_at` | When the person was erased | |
| `tickets.anonymised_at` | When the ticket's personal data was removed | Shown as `anonymisedAt` on incidents. |
| `ticket_comments.erased_at` | When a comment's text was removed | B4's immutability trigger now allows exactly one more change: body to `''` with `erased_at` set, once. |

All four new tables have forced row-level security (guard test). `zelvo_privacy_job_tenants()` (SECURITY DEFINER) lists active tenants with a retention setting or open requests.

## Settings → Privacy (`privacy.manage`)

- **Privacy notice**: edit the next version as a draft (starts from the published one), preview, publish, discard. Published versions never change.
- **Optional purposes**: add, reword, reorder, switch off.
- **Retention**: days after closing per record type (Incident, ServiceRequest, Complaint, Inquiry, Suggestion) and days after which unused requester accounts are erased; 30 days to 50 years; preview counts (`GET /settings/privacy/retention-preview`). Stored as `privacy.retention`.
- **Requests**: days to answer a data subject request (`privacy.requestDueDays`, 30 by default, 1–90).

## Consent

- `GET /privacy/notice` (visitors too) returns the published notice. `GET /me/privacy` gives the signed-in person the notice, whether they accepted it, the purposes with their choices and their history; `POST /me/privacy/notice-acceptance` and `PUT /me/privacy/purposes/{id}` record their choices. The web app asks signed-in people to accept a new version.
- Visitors starting a live chat, leaving a chat message or sending an anonymous complaint, inquiry or suggestion send `privacyNoticeAccepted: true` while a notice is published; otherwise 400. Signed-in people accept through the web app instead.
- Integrations with `privacy.consents.read` read one person's state (`GET /privacy/consents/{userId}`) or who currently agrees to a purpose (`GET /privacy/purposes/{key}/granted`), both audited. Optional purposes are recorded and available to integrations; they do not yet switch Zelvo features on or off (for a later decision).

## Data subject requests (`privacy.requests`)

| Step | Who | API |
| --- | --- | --- |
| Ask (Access, Correction, Erasure, Restriction, Objection; at most five open) | The person, signed in | `POST /me/privacy/requests`, `GET`, `POST …/{id}/withdraw` |
| Log for a person after checking who they are (identity check required) | Officer | `POST /privacy/requests` |
| List (open first, soonest due), detail with what Zelvo holds (counts) and whether the person can be erased | Officer | `GET /privacy/requests`, `GET /privacy/requests/{id}` (audited view) |
| Start, complete with what was done, refuse with the reason | Officer | `…/start`, `…/complete`, `…/reject` |
| A copy of the data | Officer, or the person themselves | `GET /privacy/requests/{id}/export`, `GET /me/privacy/export` |
| Erase (Erasure requests only, number typed again) | Officer | `POST /privacy/requests/{id}/erase` |

Notifications (B5 engine, English and formal Arabic, email always on): `privacy.request.received`, `privacy.request.completed`, `privacy.request.rejected` to the person, `privacy.request.new` to holders of `privacy.requests`. After an erasure the person is not emailed: their address is gone.

**The copy** is a ZIP built in a temporary file: `personal-data.json` (format `zelvo-personal-data/1`: profile, roles and groups, tickets they raised with form answers including personal answers and the public conversation, their messages elsewhere, chats, consents, ratings, article feedback, the latest 1,000 notifications, payments, approval decisions, privacy requests) and the clean files they uploaded under `files/` (up to 500 files and 500 MB; others listed). Staff's internal notes are not included.

## Erasure and anonymisation (`PersonalDataEraser`)

Plain SQL on the tenant's connection (row-level security applies) in one transaction, so field-level audit does not copy removed values; one audit entry with counts (`privacy.person.erased`, `privacy.tickets.anonymised`).

**A ticket is anonymised**: title emptied, description removed, form answers and personal answers emptied, the requester's messages emptied, the requester's files (and form uploads) deleted, chat transcripts and email copies emptied, its notifications, AI insights, translations, jobs and PDF exports removed, AI reasons, customer-voice comments, rating comments, proactive answers and approval reasons cleared, the search document removed. Numbers, dates, states, category, group, priority and SLA clocks stay. When a **retention period** ends the staff's messages, resolution notes, task notes, approval comments and every file go too, since the whole record's period is over.

**A person is erased**: every ticket they raised anonymised, their own messages elsewhere, chats, email copies to and from them, notifications, preferences, rating and feedback comments, pending proactive offers and form uploads removed; roles, groups, skills, customer groups and report subscriptions removed; the account kept as an empty, disabled record (`subject = erased:<id>`, no name, email, phone, Emirates ID, employee number, manager, department or location); their Keycloak user deleted (last, so a Keycloak failure rolls back the erasure). An erased account cannot be changed (`user-erased`).

**Messaging and connectors (E3, E7).** A ticket's WhatsApp, SMS and Teams messages lose their text, address and payload, and its voice notes (with transcripts) are removed. Erasing a person also removes their messaging contacts (phone numbers, Teams ids) and switches their WhatsApp, SMS and Teams notifications off. Their identity or CRM connector link is kept as a marker: directory and CRM syncs skip an erased account, so they never fill it in again or make a new account for the same person; an administrator decides that.

Refused: erasing yourself (`erase-self`), the last active tenant administrator (`last-administrator`), or a person with tickets that are not resolved or closed (`open-tickets`).

## Retention jobs

`PrivacyJobs` in the API (it deletes Keycloak users), every hour (`Privacy:Jobs:Interval`, off with `Privacy:Jobs:Enabled=false`), one instance per tenant (advisory lock), at most 200 tickets per record type and 200 accounts per run:

- closed tickets whose `closed_at` is older than their type's period are anonymised;
- requester accounts (no other role, not a system account) unused (`last_seen_at`, else `created_at`) for longer than the period, with no ticket that is not closed, none closed within the period and no open request, are erased. Staff accounts are never erased by retention.

## Not included

- Breach notification workflow to the UAE Data Office (organisational process; see the PDPL mapping).
- Optional purposes switching features off automatically.
- Retention of the audit log itself (kept, decided 2026-11-25) and of backups (operators' backup rotation, F6).
