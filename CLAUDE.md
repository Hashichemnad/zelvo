# Zelvo — instructions for Claude Code

Zelvo is a bilingual (English/Arabic) ITSM platform for the UAE market, sold as a one-time license and hosted by each customer on its own servers. The product owner reviews and approves all work; Claude Code writes the code.

## Source of truth

- `docs/requirements.md` holds every product requirement. Read the sections relevant to a task before starting it. Never invent features outside it; if something is unclear or missing, stop and ask the product owner.
- This file holds how we build and where we are. Update the **Progress** section at the end of every task.
- Module numbers (1–32) and requirement IDs (LANG-x, GOV-x) in code comments, commits and PR titles refer to `docs/requirements.md`.

## How we work

1. One task per session, one branch per task. The branch is the one the Claude Code session provides (`claude/...`); do not rename it. Pull request titles start with the task ID from the build plan, for example "A2: Identity and access".
2. Read the requirement section, then plan: data model, API, UI, tests. Share the plan before writing large amounts of code.
3. Build with tests. A task is done only when it builds, tests pass, and it runs in Docker Compose.
4. Open a pull request with: what was built, which requirements it covers, how to test it, screenshots for UI work, and anything left out.
5. Update **Progress** below in the same PR.
6. Phases are built in order. A phase starts only after the product owner approves the previous one. Phase 1 also needs an external penetration test.

## Tech stack

| Layer | Choice |
| --- | --- |
| Backend | ASP.NET Core on .NET 10 LTS, C# |
| Frontend | Next.js + React + TypeScript, Tailwind CSS |
| Database | PostgreSQL with pgvector; row-level security for tenant isolation |
| Search | OpenSearch (Arabic and English analysers) |
| Cache / queue | Valkey (Redis-compatible), RabbitMQ |
| Real-time | SignalR |
| Workflow engine | Elsa Workflows 3 (approved; confirmed by a spike in task C3, Temporal is the fallback) |
| Identity | Keycloak (SAML/OIDC, MFA), one realm per tenant, UAE PASS; a small Zelvo Keycloak extension in Java (`src/keycloak`) for email one-time codes, UAE PASS sign-in steps (account level, matching staff by Emirates ID) and, in demo installations only, one-click demo sign-in only |
| AI | Provider-agnostic AI gateway; customer supplies the provider and API key; built-in MCP server |
| Packaging | Docker images; Docker Compose (small installs) and Helm/Kubernetes (medium and large) |
| Observability | OpenTelemetry (Collector), Prometheus, Grafana Tempo (traces), Grafana Loki (logs), Grafana |

Changing any of these needs the product owner's approval.

## Rules that apply to every task

### Security
- Follow OWASP ASVS Level 2. Validate all input on the server, use parameterised queries only, encode output, and protect against CSRF.
- Enforce tenant isolation in the database (PostgreSQL row-level security), never only in application code.
- Every create, change, admin action and view of sensitive data writes to the audit log.
- Mask personal data before any text is sent to an AI provider.
- **Never commit secrets:** no passwords, API keys, tokens, certificates or private keys. Use environment variables and `.env` files, which are git-ignored. Commit only `.env.example` files with placeholder values.

### Licensing code stays out of this repository
- This repository is public during development. The license verification code, license signing tool and signing keys live in a separate **private** repository (`zelvo-licensing`), which is brought into the build only at release time.
- In this repository, code calls a licensing **interface** (`ILicenseService`). A development stub implements it and allows everything in development builds only.
- Never put license checks, fingerprinting logic, watermark logic or keys in this repository.

### Language, RTL and accessibility
- Every user-facing string comes from translation files in both English and Arabic. No hard-coded text in the UI, emails or notifications.
- Layouts work in both left-to-right and right-to-left. Use logical CSS properties (`margin-inline-start`, not `margin-left`).
- Dates support Gregorian and Hijri display; numbers support Western and Arabic-Indic digits. The tenant sets the defaults (initially Gregorian, Western digits); each user can override them in their profile.
- Display language order: signed-in users get their profile setting, then the tenant default, then English; visitors who are not signed in get the browser language (if English or Arabic), then the tenant default, then English. The language switch applies to the current visit and is remembered on the device (cookie); it does not change the profile.
- Meet WCAG 2.2 AA: keyboard access, labels, contrast and screen readers in both languages.

### Configuration over code
- Anything the requirements say an admin can set (forms, SLAs, workflows, branding, catalog, payments, AI provider) is stored as data and edited in Settings, never hard-coded.
- Branding (name, logo, colours, domain) is per tenant. The "Powered by Zelvo" footer is always shown and cannot be removed.

### API first
- Every feature is available through the REST API, with OpenAPI documentation, before or alongside its UI.
- The UI uses the same public API as integrations do.

### Quality
- Backend: unit tests and integration tests against a real PostgreSQL in Docker. Frontend: component tests, plus end-to-end tests for main user journeys.
- Database changes only through migrations.
- Small, focused commits with clear messages.

## Repository layout

```
docs/                    requirements, Phase 1 build plan, architecture notes, security (ASVS checklist), screenshots
src/api/                 ASP.NET Core backend (Zelvo.Api), data layer and migrations (Zelvo.Data), search (Zelvo.Search), ticket core and attachments (Zelvo.Tickets), stored-secret encryption (Zelvo.Secrets), telemetry (Zelvo.Observability), licensing contract and development stub
src/api/tests/           backend unit and integration tests (integration tests use real PostgreSQL via Testcontainers)
src/web/                 Next.js frontend (portal, agent workspace, admin); translations in src/web/messages/{en,ar}.json
src/worker/              background jobs: SLA timers, notifications, integrations, AI
src/keycloak/            Keycloak extension (email one-time code sign-in, UAE PASS sign-in steps) and the zelvo login/email theme (Java, Maven)
deploy/compose/          Docker Compose for development and small installs (observability/ and security/ configuration)
deploy/helm/             Helm charts for Kubernetes installs
tests/e2e/               Playwright end-to-end and accessibility tests
scripts/                 dev-up.sh / dev-down.sh (wrapped by the Makefile)
```

Start everything with `make up`; see README.md. Licensing boundary: `docs/architecture/licensing.md`. Identity and access: `docs/architecture/identity-access.md`. Settings and localization: `docs/architecture/settings-localization.md`. Search: `docs/architecture/search.md`. UAE PASS: `docs/architecture/uae-pass.md`. Observability: `docs/architecture/observability.md`. Stored secrets: `docs/architecture/secrets-encryption.md`. Security baseline: `docs/architecture/security-baseline.md`; ASVS L2 checklist: `docs/security/asvs-l2-checklist.md` (update its rows in every task that touches them). Demo mode: `docs/architecture/demo-mode.md`. Tickets and incidents: `docs/architecture/tickets.md`.

## Progress

Update this at the end of every task.

### Phase 1: Launch (in progress)

- [x] Requirements approved and added (`docs/requirements.md`)
- [x] Project instructions (`CLAUDE.md`), proprietary license, `.gitignore`
- [x] Project setup: repository structure, ASP.NET Core API, worker, Next.js frontend (English/Arabic, RTL), Docker Compose (PostgreSQL + pgvector, Redis, OpenSearch, RabbitMQ, Keycloak), one-command start (`make up`), bilingual welcome page with live service status, CI (build, tests, end-to-end and accessibility tests, CodeQL, Grype image scanning, SBOM, Gitleaks, Dependabot), `ILicenseService` with a development stub
- [x] Phase 1 build plan broken into tasks: `docs/phase1-build-plan.md` (approved 2026-11-13)
- [ ] Modules 1–15 and demo mode: tasks A1–F7 in `docs/phase1-build-plan.md`
  - [x] A1 data, tenancy and audit: `Zelvo.Data` with EF Core migrations and a `migrate` step in Compose; tenants; PostgreSQL row-level security from a per-connection tenant setting, with a guard test for every tenant-owned table; append-only, hash-chained audit log (database triggers) with automatic field-level change capture and a verification endpoint; bilingual `LocalizedText` with missing-translation report; API conventions (problem details with codes, validation, paging); Scalar API explorer. See `docs/architecture/data-tenancy-audit.md` and `docs/architecture/api-conventions.md`
  - [x] A2 identity and access (in review): one Keycloak realm per tenant, provisioned by `migrate` (clients, brute-force protection, English/Arabic, `zelvo` theme, sign-in flow); API accepts only tokens of the request tenant's realm; Zelvo users created as requesters at first sign-in; the six personas as built-in roles whose permissions and field rules are data (defaults given once, admin changes kept, lock-out guards); field-level access (hidden fields left out, read-only fields refused, personal-data views audited); mandatory second factor for agents, team leads and admins through `acr=mfa` step-up (RFC 9470); email one-time code sign-in through a Zelvo Keycloak extension with the tenant setting known users / allowed domains / anyone; anonymous access for public endpoints; web backend-for-frontend with server-side sessions in Valkey, CSRF protection and an API proxy; development users per persona and Mailpit in Compose. See `docs/architecture/identity-access.md`
  - [x] A3 settings and localization (in review): tenant settings store (`tenant_settings`, JSON per key, row-level security, audited; typed definitions in code with defaults); Settings shell with a permission-filtered menu and the missing-translation warning; Settings → Language and region (tenant default language, calendar and digits; `settings.manage`); personal language, calendar and digits on the Account page with live preview; display language order as decided 2026-11-13 (profile → tenant → English when signed in; browser → tenant → English for visitors; language switch per visit, remembered on the device); formal Arabic correspondence guidelines (`docs/guidelines/arabic-formal-correspondence.md`, GOV-8 base). See `docs/architecture/settings-localization.md`
  - [x] A4 search (in review): transactional search outbox (`search_outbox`, row-level security) written with each change; worker relays it tenant by tenant through RabbitMQ (quorum queue, dead-letter queue, republish of stale entries) and writes OpenSearch with external versions (late or duplicate changes never overwrite newer ones); one index per tenant behind an alias, with a tenant filter on every query; Arabic and English analysers with the ICU plugin (diacritics, alef/teh marbuta/alef maqsura/hamza forms, Persian letters, Arabic-Indic digits, light stemming, prefixes for search as you type) in a `zelvo/opensearch` image; versioned mapping with automatic upgrade; search API with readers per document and permission per document type; Settings → Search for administrators (documents, pending updates, last error, audited "Rebuild index") in English and Arabic (module 10, decided 2026-11-18). See `docs/architecture/search.md`
  - [x] A5 observability and security baseline (in review): Keycloak upgraded to 26.8.2 first (image and libraries together; replaces Dependabot PR #31); OpenTelemetry traces, metrics and logs from the API, worker, web app and Keycloak through an OpenTelemetry Collector to Prometheus, Tempo and Loki, with Grafana in Compose (data sources linked, "Zelvo overview" dashboard with the 200 ms p95 target, alert rules; English only; no personal data or query strings in telemetry); API rate limits per signed-in user and per client address, shared by instances through Valkey (429 `rate-limited`, RateLimit headers, fail-open with an alert), client addresses only from trusted proxies (`Network:TrustedProxies`, web `TRUSTED_PROXY_HOPS`; also fixes the audit log's client address); encryption at rest for stored secrets (`Zelvo.Secrets`: per-tenant AES-256-GCM data keys wrapped by a master key file or Vault/OpenBao Transit, `tenant_data_keys` with row-level security and no deletes, `rewrap-keys` for rotation, `keyVault` readiness check); OpenSearch security plugin and RabbitMQ TLS on (TLS 1.3, development CA from a `certs` service, one least-privilege user per service, RabbitMQ `zelvo` virtual host); OWASP ASVS 5.0 L2 checklist started (`docs/security/asvs-l2-checklist.md`, 253 requirements, 86 met). See `docs/architecture/observability.md`, `secrets-encryption.md` and `security-baseline.md`
  - [x] B1 incidents (in review; module 1): shared ticket core (`tickets` with numbering `INC-000123` per record type and tenant, never reused; states New → Assigned → In progress ⇄ On hold with reason → Resolved with notes → Closed, Reopened; parent/child one level deep, resolving a parent resolves its children; follow-ups of closed incidents) and `incidents` (impact × urgency matrix 3 × 3 → P1–P5, editable; major-incident flag with a bilingual communication timeline for staff or also requesters, including requesters of child incidents); field-level audit history from the audit log; row-level security on all six new tables; permissions `incidents.read/create/edit/assign/major` with role defaults and an `incidents` field resource; requesters' own smaller view; If-Match/ETag; search documents; Settings → Incidents (matrix, on-hold reasons, number prefix, auto-close period) and Settings → Attachments (size limit, allowlist); attachments in PostgreSQL behind `IAttachmentStore`, content checked against the type, scanned by ClamAV in the worker before download (infected files rejected, deleted and audited), ClamAV in Compose with offline definition import (`scripts/clamav-import-definitions.sh`); resolved incidents closed by the worker after the tenant's period; "My incidents", "Report an incident" and staff incident pages in English and Arabic; demo incidents for both sample organisations; ASVS V5 rows (94 met). Also before B1: Keycloak image 26.8.2-2 with a Maven build that needs no plugin lookup (replaces Dependabot PR #36), Node.js 24 LTS everywhere, Dependabot ignores major Node (also docker-compose) and OpenTelemetry upgrades. See `docs/architecture/tickets.md`
  - [x] D3 UAE PASS (in review; GOV-1): Settings → Sign-in → UAE PASS (on/off, environment Staging/Production, client ID, client secret that goes only to Keycloak and is audited without its value, redirect URI to register) with the approved rules (UAE PASS as second factor for staff, off and SOP2+ only; staff always SOP2+; requesters' minimum level, SOP1; automatic requester accounts, on); UAE PASS as each realm's identity provider through Keycloak's generic OAuth 2.0 broker, people matched by UAE PASS uuid; Zelvo Keycloak extension steps: account level of each sign-in in tokens, first sign-in that links by Emirates ID (recorded by admins on users), links by email only after proof, or creates a requester as allowed; signing out also ends the UAE PASS session; development UAE PASS mock in Compose. Not tried against real UAE PASS staging yet; integration terms still to confirm with the UAE PASS programme. See `docs/architecture/uae-pass.md`
  - [x] D demo mode foundation (in review): demo installation profile (`make demo`, own Compose project and database; `migrate` refuses to mix demo and other tenants) with the development stub reporting the `Demo` license kind; two fictional sample organisations (government entity `demo-municipality`, private company `demo-company`) with bilingual names and language defaults; seeding framework (`IDemoSeeder`, run per tenant as the application role, once per seeder, by `migrate` and by reset); one demo user per persona and organisation; one-click switching of user and organisation from a bilingual demo panel through a demo-only Zelvo Keycloak extension step (`acr=mfa`, API token checks unchanged); "Reset demo" for demo administrators (`demo.reset`): every tenant-owned table except the audit log emptied and reseeded, other Keycloak users removed, audited; CI job `e2e-demo`. Later modules add their sample data as seeders. See `docs/architecture/demo-mode.md`

### Phase 2: Control (not started)
### Phase 3: Scale (not started)
### Phase 4: Expand (not started)

## Decisions log

| Date | Decision |
| --- | --- |
| 2026-11-13 | Customers host Zelvo themselves; one-time license; fully offline activation; no license portal. |
| 2026-11-13 | Security updates are free; feature updates are paid and unlocked by an upgrade license. Updates are delivered as signed packages imported in Settings. |
| 2026-11-13 | AI uses the customer's own provider and API key; Zelvo includes an MCP server. |
| 2026-11-13 | Repository stays public during development; licensing code is kept in a separate private repository. |
| 2026-11-13 | Setup: .NET 10 LTS with central package management and warnings as errors; Next.js 16 App Router with next-intl (locale in the URL: `/en`, `/ar`); Tailwind CSS 4 with logical properties only (enforced by a test); IBM Plex Sans Arabic bundled locally (works offline); Playwright + axe for end-to-end and accessibility tests. |
| 2026-11-13 | Database roles: a superuser-owned database for migrations and a separate application role without `BYPASSRLS`, so row-level security always applies to the app. |
| 2026-11-13 | Phase 1 build plan (`docs/phase1-build-plan.md`) approved by the product owner: order, splitting and scope per task. |
| 2026-11-13 | Workflow engine: **approved** Elsa Workflows 3 (MIT, embedded in .NET, PostgreSQL persistence), confirmed by a spike at the start of task C3; Temporal stays the fallback. |
| 2026-11-13 | Cache: **approved** Valkey (BSD, Redis-protocol compatible) instead of Redis, because Redis 7.4+ is not permissively licensed and customers receive our images. Client code (StackExchange.Redis) is unchanged. |
| 2026-11-13 | A1: tenant isolation through a per-connection PostgreSQL setting (`zelvo.tenant_id`) read by forced RLS policies; the API picks the tenant from the `X-Zelvo-Tenant` header or the installation default, and from A2 also requires the user's tenant claim to match. Audit hash chain computed and enforced by database triggers so every writer is covered. API explorer: Scalar (MIT, assets bundled). |
| 2026-11-13 | Arabic product wording **approved**: product name "زيلفو"; "Powered by Zelvo" is "بتقنية زيلفو". |
| 2026-11-13 | Branches: work happens on the branch the Claude Code session provides (`claude/...`); pull request titles start with the task ID, for example "A2: Identity and access". |
| 2026-11-13 | A2: **one Keycloak realm per tenant** (`zelvo-<slug>`); the API accepts only tokens issued by the request tenant's realm. |
| 2026-11-13 | A2: Keycloak 26.7.5 has no built-in way to email a sign-in code (checked), so a **small Zelvo Keycloak extension in Java** (`src/keycloak`, with its own tests) adds that login step only. |
| 2026-11-13 | A2: who may sign in with an email code is a **tenant setting**: "only known users" (default), "allowed email domains" (anyone with a listed domain gets a requester account automatically) or "anyone" (public portals). |
| 2026-11-13 | A2: roles are the six personas; what each role may do (permissions and field rules) is data in Zelvo, not in Keycloak; staff roles need a second factor, checked by the API from the token's `acr` and asked for with step-up. Web sessions are server side (Valkey); the browser never holds tokens. |
| 2026-11-13 | A3: language order **approved**: signed-in users get profile → tenant default → English; visitors who are not signed in get browser language (English or Arabic) → tenant default → English. The language switch applies to the current visit and is remembered on the device (cookie); it never changes the profile default. |
| 2026-11-13 | A3: **tenant defaults for calendar and digits** (Gregorian/Hijri, Western/Arabic-Indic), set by the admin in Settings and starting as Gregorian and Western digits; each user can override them in their profile (LANG-8 updated). |
| 2026-11-18 | A4: **Settings → Search** approved (module 10): index status (documents, pending updates, last error) and "Rebuild index", administrators only, English and Arabic, rebuilds audited. No sample documents in A4; demo data stays in task D. |
| 2026-11-18 | A4: search updates are written to a transactional outbox (`search_outbox`) with the change, relayed by the worker through RabbitMQ and written to **one OpenSearch index per tenant**; OpenSearch runs as a Zelvo image (`zelvo/opensearch`) that adds the official ICU analysis plugin. |
| 2026-11-18 | CI dependency audit: high and critical advisories in the web app's runtime dependencies still fail CI, except advisories the product owner accepts in `src/web/audit-exceptions.json`, each with a reason and a review date (`src/web/scripts/audit.mjs`; expired or unused exceptions also fail). First exception: GHSA-fc5q-39xc-vjpf (picomatch, no fix released, only in next-intl's unused file watcher, not in the built image), review by 2026-12-18. Secret scanning keeps covering every branch. |
| 2026-11-18 | D3: UAE PASS settings **approved** (GOV-1 updated), under Settings → Sign-in → UAE PASS: (1) "Accept UAE PASS as the second factor for staff", off by default, and only for verified accounts (SOP2+); (2) staff always need an SOP2+ UAE PASS account (not configurable); the minimum for requesters is a setting, SOP1 by default; (3) "Create requester accounts automatically on first UAE PASS sign-in", on by default; when off, only known users can sign in. Staff accounts are never created automatically; they must exist and are matched by Emirates ID or by email. |
| 2026-11-18 | D3: matching a UAE PASS sign-in to an existing account by **email needs proof** (a link sent to that email, or the account's password), as UAE PASS guidance warns against linking by email alone. Matching by Emirates ID (verified SOP2+ accounts only) links straight away. |
| 2026-11-18 | D: **demo user switching** goes through a demo-only sign-in step in the Zelvo Keycloak extension (signs listed demo users in with one click, second factor counted), added only to demo installations' realms; the API's token checks are unchanged. Widens the extension's scope (tech stack row). |
| 2026-11-18 | D: **both sample organisations** in task D (a fictional government entity and a fictional private company, one tenant each), with an organisation switch in the demo panel. |
| 2026-11-18 | D: **"Reset demo" keeps the audit log** (append-only) and resets everything else; only demo administrators may reset (permission `demo.reset`, tenant administrator role). |
| 2026-11-18 | D3: Keycloak 26.7.5 cannot pass UAE PASS data (account level, Emirates ID) through its OAuth 2.0 broker and cannot find an account by Emirates ID (checked), so the **Zelvo Keycloak extension also gets UAE PASS sign-in steps** (with their own tests). |
| 2026-11-18 | Keycloak upgraded to **26.8.2** (image and `org.keycloak` libraries together, pinned to the same patch version; replaces Dependabot PR #31). 26.8 checks the admin API audience, so `zelvo-provisioner` gets the built-in `kc-admin-api` client scope. Still no built-in email code sign-in (checked), so the extension stays. |
| 2026-11-18 | A5: observability backends **approved**: OpenTelemetry Collector (Apache 2.0) in front of Prometheus (metrics), **Grafana Tempo** (traces) and **Grafana Loki** (logs), shown in Grafana; Tempo and Loki are AGPL-3.0 like Grafana and shipped unmodified. Retention in Compose: traces 7 days, logs and metrics 15 days. |
| 2026-11-18 | A5: encryption at rest for stored secrets **approved**: envelope encryption with per-tenant AES-256-GCM data keys wrapped by a key-encryption key from a **master key file** (small installs) or **HashiCorp Vault / OpenBao Transit** (medium and large); further key stores (Azure Key Vault, AWS KMS) can be added behind the same interface on request. |
| 2026-11-18 | A5: Grafana dashboards and other operator tools (Prometheus, RabbitMQ and OpenSearch consoles) are **English only**; this is an exception to the bilingual rule for third-party operator tools, not for Zelvo's own screens. |
| 2026-11-19 | CI dependency audit: the picomatch exception (GHSA-fc5q-39xc-vjpf) was removed from `src/web/audit-exceptions.json` because the advisory no longer matches the web app's runtime dependencies (`npm audit` reports none), as the audit rules require; no exceptions remain. |
| 2026-11-20 | Node.js **24 LTS** everywhere (web image, UAE PASS mock in Compose, CI, `engines` and `@types/node` in `src/web` and `tests/e2e`), replacing Node 22 and the non-LTS Node 26 that Dependabot PR #37 put in Compose. Dependabot ignores major Node upgrades in every ecosystem (now also docker-compose) and major OpenTelemetry upgrades (NuGet `OpenTelemetry*`, npm `@opentelemetry/*`, Collector image); those are upgraded by hand. |
| 2026-11-20 | Keycloak image tags with a rebuild suffix (`26.8.2-2`, Dependabot PR #36) are accepted; the `org.keycloak` libraries keep the plain version, and CI checks the two match (`scripts/check-keycloak-version.sh`). The image build calls the Maven dependency plugin by full coordinates, because the short `dependency:` prefix needs a lookup in Maven Central that failed the build. |
| 2026-11-22 | B1: **attachments stored in PostgreSQL** behind a storage interface (`IAttachmentStore`), isolated by row-level security and included in database backups. Maximum file size is a tenant setting (default 25 MB) with a file-type allowlist (also a tenant setting). **S3-compatible object storage is added as an option for medium and large installations in task F6.** |
| 2026-11-22 | B1: **ClamAV added to Compose** (official `clamav/clamav` image, GPL-2.0, shipped unmodified). Scanning is on by default; files stay unavailable until scanned clean; infected files are rejected, their content deleted and the rejection audited. Offline installations update definitions by importing definition files (`scripts/clamav-import-definitions.sh`; in Settings with the update packages of task F4). ClamAV's memory use (about 1–1.5 GB, briefly about twice that while it loads new definitions; measured 1.0 GB idle with 1.5.3) is noted in the server sizing (requirements.md). |
| 2026-11-22 | B1: ticket numbers **`INC-000123`**: prefix per record type as a tenant setting, numbers never reset or reused. |
| 2026-11-22 | B1: resolved incidents can be **reopened** by the requester or an agent until closed; they **close automatically** after a tenant-set period (default 5 calendar days; business days come with B3 calendars); **Closed is final** and a follow-up is a new incident linked to the old one. |
| 2026-11-22 | B1: default **priority matrix 3 impact × 3 urgency → P1–P5**, level names and mapping editable by administrators. |
| 2026-11-22 | B1: simple **"My incidents" and "Report an incident"** pages for requesters now (bilingual, accessible), built as components the portal (D2) reuses. |
