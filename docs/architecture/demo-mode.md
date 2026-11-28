# Demo mode (task D)

Covers the **Demo mode** section of `docs/requirements.md`: a separate demo installation with its own demo license, sample organisations, ready-made demo users for each role with one-click switching, and "Reset demo". This task builds the foundation. Every later module adds its sample data (catalog, tickets, SLAs, knowledge, dashboards and so on) through the seeding framework below.

Product owner decisions, 2026-11-18:

- **Switching** goes through a demo-only sign-in step in the Zelvo Keycloak extension.
- **Both sample organisations** (a government entity and a private company) are in this task, with an organisation switch in the demo panel.
- **Reset** keeps the audit log and resets everything else.
- **Only demo administrators** may reset: permission `demo.reset`, held by the tenant administrator role.

## A demo installation

| Piece | What it does |
| --- | --- |
| License kind `Demo` | Demo features exist only when `ILicenseService` reports a valid demo license (`license.IsDemo()`). Development builds report it from the development license when `Installation__Profile=Demo`. A release demo installation gets a demo license file (kind `Demo`, issued with `zelvo-sign issue --kind demo`); see `licensing.md`. |
| Own database | `make demo` starts Compose project `zelvo-demo` (`compose.yaml` + `compose.demo.yaml`), with its own volumes. `migrate` refuses (exit code 4) to run a demo on a database that holds any tenant that is not a sample organisation, so demo data is never mixed with real data. |
| No bootstrap tenant, no development users | In a demo installation, `migrate` creates only the sample organisations. |
| Other installations | `GET /api/v1/demo` and `POST /api/v1/demo/reset` answer 404 `not-demo`; the web app shows no demo panel; realms have no demo sign-in step. |

## Sample organisations

Defined in code (`Zelvo.Api/Demo/DemoCatalog.cs`). The names and people are fictional. Addresses use the reserved `.example` domain, so no email ever reaches a real mailbox.

| Tenant | Kind | Name | Default language |
| --- | --- | --- | --- |
| `demo-municipality` | Government | Department of Municipal Services / دائرة الخدمات البلدية | Arabic |
| `demo-company` | Company | Gulf Horizon Trading / أفق الخليج للتجارة | English |

Each has one demo user per persona: `requester@`, `agent@`, `lead@`, `approver@`, `admin@` and `executive@` at `municipality.example` or `gulfhorizon.example`. A demo user has exactly the built-in role of its persona and no password.

## Seeding framework

- **Seeders.** A seeder implements `IDemoSeeder` (`Key`, `Order`, `SeedAsync`) and is registered with `services.AddDemoSeeder<T>()`. Task D has two:
  - `d-organisation` (order 0): name, language, calendar and digit defaults.
  - `d-users` (order 10): demo users in Keycloak and in Zelvo.
  - Later modules use order 100 and up (B1 from 100, B2 from 200, …), so they run after the users exist.
- **When they run.** `DemoSeeding` runs, for each sample organisation, the seeders that have not run there yet. It keeps their keys in the tenant setting `demo.seeders`.
  - `migrate` runs it at every start, so a release that adds a seeder fills existing demos.
  - "Reset demo" clears the list and runs all of them again.
- **How they run.** Each runs in a scope set to the organisation's tenant, with the **application role's** connection, so row-level security applies as in the API. Writes are audited as the system actor `demo-seed`.
- **Rules for a seeder.**
  - Only bilingual sample data (English and Arabic).
  - Nothing real: no real people, entities, phone numbers or Emirates IDs.
  - Write through the same entities and stores the API uses, never by bypassing audit.
  - Stay safe to run on a tenant where earlier seeders already ran.

## One-click switching

```
Demo panel ──POST /auth/demo──▶ web: end Zelvo session, end Keycloak session (old realm)
            (tenant, persona,    ──▶ /auth/demo/continue?ticket=…  (random, used once, 5 minutes)
             locale, csrf)       ──▶ Keycloak authorize, login_hint=zelvo-demo:<username>
                                 ──▶ demo sign-in step: user set, level 2 (acr=mfa), no form
                                 ──▶ /auth/callback as for any sign-in
```

- **Demo panel.** It sits under the header, in English and Arabic (`DemoPanel`, `DemoResetButton`). It is a native disclosure (`<details>`) with plain POST forms, so it works with the keyboard and without JavaScript. It lists the organisation's six demo users, the other sample organisation and when the demo was last reset. The signed-in demo user is marked `aria-current`.
- **Organisation.** The organisation chosen in the panel is remembered on the device in the http-only `zelvo-tenant` cookie.
  - The web app honours it only for tenants listed in `ZELVO_DEMO_TENANTS`.
  - Outside a demo the web app always serves `ZELVO_TENANT`.
  - A pending sign-in remembers its tenant, so the callback finishes at the same realm.
- **Demo sign-in step** (`zelvo-demo-sign-in`, package `zelvo.keycloak.demo`, with unit tests).
  - `migrate` puts it first in each sample organisation's browser flow and lists that organisation's demo users in its settings.
  - It answers only `login_hint=zelvo-demo:<username>` for a listed, enabled user; every other sign-in passes on to the usual steps.
  - It records level 2 (`AcrStore`), so tokens carry `acr=mfa` and staff personas work without an authenticator app.
  - The API's token checks do not change at all.
- **Who can switch.** Anyone who can open a demo installation can sign in as any demo user, the demo administrator included. This is the purpose of a demo, and it holds no real data. The reset needs `demo.reset` and a confirmation, so a prospect using the requester or agent view cannot wipe the demo by accident.
- **Changes from this site only.** The switch is a same-origin POST; signed-in users also send the session's CSRF token.

## Reset demo

`POST /api/v1/demo/reset` needs `demo.reset` and a second factor (the demo sign-in gives one). It **starts a background job** and answers 202 at once with the job's progress (`DemoResetJobs`); the demo panel follows it with `GET /api/v1/demo/reset`. Since Phase 2 a reset reseeds two organisations with about 35 seeders each, which took longer than the browser, the web proxy or an HTTP client waits (the API test timed out after 100 seconds), so the request no longer waits for it. One reset runs at a time; the job stops only when the API shuts down, never because the person who asked went away. For each sample organisation it:

1. Deletes every row of every tenant-owned table **except `audit_events`**, children before parents, in one transaction.
   - The tables come from the EF model (`ITenantOwned`), so later modules' tables are included automatically.
   - A test checks the list against every table with a `tenant_id` column.
2. Deletes Keycloak users that are not demo users (for example accounts created by email-code or UAE PASS sign-in).
3. Restores the demo users: names, no credentials, no attributes, no UAE PASS link. Their Keycloak ids stay the same, so the presenter stays signed in.
4. Gives the tenant its built-in roles again, then runs every seeder.
5. Records `demo.reset` (Admin, system actor `demo-reset`, with the row counts and who asked) and rebuilds the search index.

The request's own tenant also records `demo.reset-requested` by the user who pressed the button (before the job starts). The audit log keeps growing across demos, and its hash chain stays valid (tested).

**Progress.** The job counts steps: for each organisation, emptying its tables, each seeder and indexing (about 70 in all). `GET /api/v1/demo/reset` answers the state (`Idle` when no reset ran since the API started, `Running`, `Succeeded`, `Failed`), the organisation and stage (`Emptying`, `Seeding` with the seeder's key, `Indexing`), steps done, the total and a percentage. It is open to visitors: it says nothing but progress, and the panel must read it while the presenter's own account is being recreated. Demo installations run one API, so the job's state is kept in memory; after a restart the panel shows the last reset from the tenant settings.

**While it runs**, the sample organisations' tables are empty or half seeded. `DemoResetGuardMiddleware` (right after the tenant is resolved, before sign-in) answers every other API request to a sample organisation with 503 `demo-reset-running` and `Retry-After: 5`, a second `POST /demo/reset` included. Without it, a signed-in person's first request while the users table is empty would create them a new requester account. `GET /api/v1/demo` and `GET /api/v1/demo/reset` are answered without the sign-in.

**When it ends**, the person who asked gets `demo.reset-finished` or `demo.reset-failed` (in the app and by email, English and formal Arabic), found by their sign-in because the reset recreated their Zelvo record. A failure is logged with the error and audited `demo.reset-failed`; the panel says the reset failed and offers it again.

**Demo panel.** After "Reset now" it shows a progress bar (`aria-label` "Progress of the reset"), the percentage in the person's digits and the step ("Gulf Horizon Trading: adding the sample data") in a polite live region, asking every 2 seconds; it reloads the page when the reset finished. A reset already running when the panel opens (another tab or administrator) is followed the same way.

**Speed.** Seeding was also made faster (a reset took about 130 seconds on a developer machine before, about 45 after; the seeding test, one organisation on a fresh database, about 53 then 29 seconds):
- Each seeder runs in its own scope and database context. One shared context kept every earlier seeder's rows in its change tracker, and each save's interceptors (audit, notifications, webhooks, workflow rules and the others) looked at all of them, so every seeder was slower than the one before.
- The workflow rule interceptor (C3) detects changes once per save and turns automatic change detection off while it loads each ticket's facts; each of its tracked-entry lookups had detected changes again over the whole save, which made a save of many new tickets quadratic. It also reads a category's or location's ancestry and a requester's customer groups once per save (`RuleFactLookups`). This helps every large save, not only the demo (bulk actions, imports).

## API

| Endpoint | Access | Returns |
| --- | --- | --- |
| `GET /api/v1/demo` | anonymous, sample tenants of a demo only | this organisation, all organisations, its demo users (persona, role, bilingual role name, name, username), last reset |
| `POST /api/v1/demo/reset` | `demo.reset` | 202 and the job's progress (`Location: /api/v1/demo/reset`); 503 `demo-reset-running` while one runs |
| `GET /api/v1/demo/reset` | anonymous, sample tenants of a demo only | the running or last reset: state, organisation, stage, seeder, steps done and in all, percentage, organisations reset |

## Running it

```bash
make down         # the demo uses the same ports
make demo         # build and start; first start seeds the sample organisations
make e2e-demo     # end-to-end tests of demo mode
make demo-down    # stop, keep data; make demo-wipe also deletes it
```

## Tests

- **API (`DemoModeTests`, `LicensingTests`).**
  - The stub's demo kind; unknown profiles are refused; Production is still refused.
  - Nothing demo-related outside demo installations or outside sample tenants.
  - What seeding creates; seeders run once.
  - Reset: started as a job (202), other requests to the sample organisations answer 503 while it runs, its progress only grows, it ends Succeeded with every step done; changed settings, a visitor account and a changed demo user are restored; the audit log is kept and verifies; the presenter stays signed in and is notified.
  - Who may reset; the tables reset covers.
- **Keycloak extension.** Policy and authenticator tests (`mvn verify`).
- **Web.** Tenant choice (`tenant.test.ts`). Demo panel and reset button: forms and fields, Arabic, current user, confirmation, the job's progress until it ends, a reset already running when the panel opens, failure (`DemoPanel.test.tsx`).
- **End to end (`tests/e2e/specs/demo.spec.ts`, project `demo`, CI job `e2e-demo`).**
  - The panel, with axe checks in both languages.
  - One click to the agent, then to the admin, with Settings open (second factor counted).
  - Reset after confirmation.
  - No reset for requesters; switching organisation.

## Not in this task

- **Sample data of later modules.** Each module adds its seeder. B1 adds `b1-incidents` (`DemoIncidentsSeeder`): six incidents per organisation in its main language, including a major incident with two child incidents and its timeline. B3 adds `b3-sla` (`DemoSlaSeeder`): business calendars with UAE public holidays, a customer group, a VIP and a standard SLA policy, and clocks for the sample incidents. Still to come: catalog with bundles, proactive services, fees and FAQs (C2–C5); knowledge (E-tasks); several months of tickets, complaints, inquiries and suggestions with trends for dashboards and reports; payment gateway in sandbox mode (module 15).
- **Tenant branding** (logos, colours) of the sample organisations: F1.
- **A command-line reset.** The button and `make demo-wipe` cover it.
