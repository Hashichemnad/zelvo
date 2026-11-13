# Zelvo — instructions for Claude Code

Zelvo is a bilingual (English/Arabic) ITSM platform for the UAE market, sold as a one-time license and hosted by each customer on its own servers. The product owner reviews and approves all work; Claude Code writes the code.

## Source of truth

- `docs/requirements.md` holds every product requirement. Read the sections relevant to a task before starting it. Never invent features outside it; if something is unclear or missing, stop and ask the product owner.
- This file holds how we build and where we are. Update the **Progress** section at the end of every task.
- Module numbers (1–32) and requirement IDs (LANG-x, GOV-x) in code comments, commits and PR titles refer to `docs/requirements.md`.

## How we work

1. One task per session, one branch per task: `phase<N>/<module>-<short-name>` (for example `phase1/m01-incidents`).
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
| Identity | Keycloak (SAML/OIDC, MFA), UAE PASS |
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
- Dates support Gregorian and Hijri display; numbers support Western and Arabic-Indic digits.
- Display language order: the user's own setting, then the tenant default, then the browser language, then English.
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
src/api/                 ASP.NET Core backend (Zelvo.Api), licensing contract and development stub
src/api/tests/           backend unit and integration tests (integration tests use real PostgreSQL via Testcontainers)
src/web/                 Next.js frontend (portal, agent workspace, admin); translations in src/web/messages/{en,ar}.json
src/worker/              background jobs: SLA timers, notifications, integrations, AI
deploy/compose/          Docker Compose for development and small installs
deploy/helm/             Helm charts for Kubernetes installs
tests/e2e/               Playwright end-to-end and accessibility tests
scripts/                 dev-up.sh / dev-down.sh (wrapped by the Makefile)
```

Start everything with `make up`; see README.md. Licensing boundary: `docs/architecture/licensing.md`.

## Progress

Update this at the end of every task.

### Phase 1: Launch (in progress)

- [x] Requirements approved and added (`docs/requirements.md`)
- [x] Project instructions (`CLAUDE.md`), proprietary license, `.gitignore`
- [x] Project setup: repository structure, ASP.NET Core API, worker, Next.js frontend (English/Arabic, RTL), Docker Compose (PostgreSQL + pgvector, Redis, OpenSearch, RabbitMQ, Keycloak), one-command start (`make up`), bilingual welcome page with live service status, CI (build, tests, end-to-end and accessibility tests, CodeQL, Grype image scanning, SBOM, Gitleaks, Dependabot), `ILicenseService` with a development stub
- [x] Phase 1 build plan broken into tasks: `docs/phase1-build-plan.md` (approved 2026-11-13)
- [ ] Modules 1–15 and demo mode: tasks A1–F7 in `docs/phase1-build-plan.md`
  - [ ] A1 data, tenancy and audit (in review)

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
| 2026-11-13 | Arabic product wording **approved**: product name "زيلفو"; "Powered by Zelvo" is "بتقنية زيلفو". |
