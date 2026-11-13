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
| Identity | Keycloak (SAML/OIDC, MFA), one realm per tenant, UAE PASS; a small Zelvo Keycloak extension in Java (`src/keycloak`) for email one-time codes only |
| AI | Provider-agnostic AI gateway; customer supplies the provider and API key; built-in MCP server |
| Packaging | Docker images; Docker Compose (small installs) and Helm/Kubernetes (medium and large) |
| Observability | OpenTelemetry, Prometheus, Grafana |

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
docs/                    requirements, Phase 1 build plan, architecture notes, screenshots
src/api/                 ASP.NET Core backend (Zelvo.Api), data layer and migrations (Zelvo.Data), licensing contract and development stub
src/api/tests/           backend unit and integration tests (integration tests use real PostgreSQL via Testcontainers)
src/web/                 Next.js frontend (portal, agent workspace, admin); translations in src/web/messages/{en,ar}.json
src/worker/              background jobs: SLA timers, notifications, integrations, AI
src/keycloak/            Keycloak extension (email one-time code sign-in) and the zelvo login/email theme (Java, Maven)
deploy/compose/          Docker Compose for development and small installs
deploy/helm/             Helm charts for Kubernetes installs
tests/e2e/               Playwright end-to-end and accessibility tests
scripts/                 dev-up.sh / dev-down.sh (wrapped by the Makefile)
```

Start everything with `make up`; see README.md. Licensing boundary: `docs/architecture/licensing.md`. Identity and access: `docs/architecture/identity-access.md`.

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
