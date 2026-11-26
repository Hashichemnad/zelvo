# Data protection levels (task F7)

How the data Zelvo holds is classified and what protection each level gets: encryption, integrity, access, masking in logs and AI, audit of views, retention, export and erasure. Covers OWASP ASVS 5.0 V14.1.1 (data identified and classified), V14.1.2 (protection requirements per level) and the V14.2 checks against them. Related: `docs/architecture/privacy.md` (UAE PDPL: notice, consent, retention, data subject requests, erasure), `docs/security/uae-pdpl-mapping.md`, `docs/security/crypto-inventory.md` (keys), `docs/security/logging-inventory.md` (what each log holds).

Checked against the code on 2026-11-26. A task that adds a new kind of data names its level in its architecture note and follows the row below.

## Levels

| Level | What it is | Examples in Zelvo |
| --- | --- | --- |
| **L1 Secrets** | Values that grant access to Zelvo or to another system, or keys that protect other data | Master key, tenant data keys, vendor signing key, DKIM and uploaded TLS private keys, AI provider keys, mailbox, messaging, payment and connector credentials, webhook secrets, `ZELVO_TLS_TOKEN`, service passwords, Keycloak client secrets |
| **L2 Authentication data** | Proves who someone is; useless once hashed | People's passwords and TOTP seeds (Keycloak only), API keys, OAuth client secrets, web session ids and the tokens in sessions, CSRF tokens, chat tokens, anonymous tracking codes, monitoring tokens, email sign-in codes |
| **L3 Personal data** | Identifies or describes a person (UAE PDPL personal data) | Names, email addresses, phone numbers, employee numbers, manager, department and location of users; the requester on tickets; ticket titles, descriptions and messages; form answers; chat and email transcripts; voice notes and transcripts; attachments; ratings and comments; consent records; data subject requests; client addresses in the audit log |
| **L3+ Sensitive personal data** | L3 that needs more care: national identifiers, signatures, payment details, data marked personal in forms | Emirates ID (`users.emiratesId`), UAE PASS uuid, form fields marked as personal data (`personal_answers`), drawn signatures, IBANs in form answers, payment records (amounts, receipts; card details never reach Zelvo) |
| **L4 Tenant business data** | The tenant's own records without people in them | Categories, groups, SLA policies, workflow rules, service cards (draft and internal), internal knowledge articles, reports, settings |
| **L5 Public** | Meant to be read by visitors | Published external knowledge articles (when the tenant allows visitors), public services, published branding, the published privacy notice, live service status |

## Protection per level

| Requirement | L1 Secrets | L2 Authentication data | L3 / L3+ Personal data | L4 Business data | L5 Public |
| --- | --- | --- | --- | --- | --- |
| **Encryption at rest in Zelvo** | AES-256-GCM with the tenant's data key, bound to tenant and purpose (`Zelvo.Secrets`); vendor key in an encrypted PKCS#8 file off the installation | Kept only as SHA-256 hashes (API keys, chat tokens, tracking codes, monitoring tokens, session keys); passwords and TOTP seeds only in Keycloak | No column-level encryption; protected by row-level security, field rules and volume encryption | Row-level security | None needed |
| **Database or disk encryption** | Additionally as for L3 | As for L3 | Not provided by PostgreSQL; the operator encrypts volumes and backups (LUKS or the provider's disk encryption; F6 install guidance) | Same | Same |
| **In transit** | HTTPS to Zelvo; TLS to providers (`communications.md`); never sent to a new address without being entered again | HTTPS; tokens never in URLs except the protocol-imposed cases listed in the ASVS checklist (V14.2.1) | HTTPS for users and integrations; internal links in Compose partly plain until F6 | Same as L3 | HTTPS |
| **Tenant isolation** | Forced row-level security; associated data includes the tenant | Row-level security; tokens checked against the request tenant's realm | Forced row-level security on every tenant-owned table (guard test); one OpenSearch index per tenant with a tenant filter | Same | Same, per tenant |
| **Access in the application** | Never returned by the API (only "is set"); only the API and worker decrypt, for the current tenant | Shown once at creation (API keys, tracking codes, monitoring tokens) | Permissions per record type, field rules (`FieldFilter`: hidden fields left out, read-only fields refused), requesters see only their own tickets and the team, never the agent | Permissions | Open to visitors where the tenant allows it |
| **Audit** | Changes audited without the value | Creation, rotation and revocation audited | Every create and change with field-level history (`[SensitiveData]` values masked, `[AuditLengthOnly]` texts by length); **views audited** for L3+ and personal-data screens (`users.personal-data.viewed`, `forms.personal-data.viewed`, `channels.chat.viewed`, `privacy.consents.viewed`, `privacy.request.viewed`) | Changes audited | Publishing audited |
| **Logs and telemetry** | Never | Never | Not in service logs or telemetry; masked in the integration log (`PersonalDataMasker`), email, mailbox and channel logs (addresses and numbers masked) | Ids only | Allowed |
| **AI providers** | Never sent; Bearer tokens and Zelvo keys removed from text | Never sent | Replaced by placeholders before sending and put back on Zelvo's side (`PersonalDataPseudonymizer`); personal form answers not sent; the AI log keeps masked text 30 days. Exception: voice-note audio for transcription cannot be masked and is sent only when an administrator turns transcription on (Settings says so) | Sent only for features that are on | Allowed |
| **Search index** | Never | Never | Ticket texts indexed per tenant with readers per document (requesters see their own); OpenSearch users and TLS 1.3 | Indexed with readers | Indexed |
| **Caching** | Unwrapped data keys in process memory only, 10 minutes | Sessions in Valkey with idle and absolute time-outs, deleted at sign-out | Not cached by the web app's data cache (requests with a user's token are `no-store`); downloads `private, no-store` | Same as L3 | Localisation, branding and the privacy notice cached 60 seconds |
| **Retention** | Kept while in use; replaced on rotation | Until expiry, revocation or sign-out | Per record type after closing, and for unused requester accounts, **off by default** (`PrivacyJobs`, anonymisation); logs 30–90 days; exports 1 day; form uploads 1 day; in-app notifications 90 days | Tenant's choice | Tenant's choice |
| **Export and erasure** | Not part of any bulk export or personal-data copy | Not exported | Copy of the person's data as a ZIP (`personal-data.json` and their clean files); erasure anonymises their tickets and empties and disables the account (`PersonalDataEraser`); the audit log is kept as the legal record | Bulk export for administrators (formulas neutralised) | n/a |
| **Backups** | Encrypted values stay encrypted in dumps; the master key is not in the database | Hashes only | Database dumps by the updater (`updates` volume, last 3 kept) are not encrypted by Zelvo; the operator protects the volume (F6) | Same | Same |

## How the levels are marked in the code

| Marker | Meaning | Where |
| --- | --- | --- |
| `[SensitiveData]` | L3+/L3 value masked in the audit log | Entity properties such as `UserAccount.Email`, `UserAccount.EmiratesId` (46 uses of the two markers) |
| `[AuditLengthOnly]` | Long L3 text audited by length only | Article bodies, comment texts |
| Field rules (`FieldAccess`) | Hidden, read-only or editable per role; tenant administrators edit Emirates ID and phone, other staff roles read email only | `PermissionCatalog.cs` |
| `personal_answers` | Form answers to fields marked as personal data, kept apart from other answers | `forms.md` |
| Secret purposes | L1 values, one purpose per field | `crypto-inventory.md` |

## Known gaps

- API answers now default to `Cache-Control: no-store` (`SecurityHeadersMiddleware`, F7); the web proxy's own default for API answers is part of the same ASVS row (V14.3.2).
- Sign-out does not yet send `Clear-Site-Data` (ASVS V14.3.1).
- Web sessions in Valkey hold access and refresh tokens (L2) in clear, protected by the Valkey password and the internal network only.
- Optional consent purposes do not switch features off yet (`privacy.md`).
