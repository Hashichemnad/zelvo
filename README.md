# Zelvo

Bilingual (English/Arabic) IT service management platform for the UAE market, designed to be hosted by each customer on its own servers.

- Requirements: [docs/requirements.md](docs/requirements.md)
- Build instructions and progress: [CLAUDE.md](CLAUDE.md)
- Phase 1 build plan: [docs/phase1-build-plan.md](docs/phase1-build-plan.md)

**Proprietary software. All rights reserved.** This repository is visible publicly during development, but no rights to use, copy, modify or distribute it are granted. See [LICENSE](LICENSE).

## Run it

You need Docker with the Compose plugin and about 5 GB of free memory. From the repository root:

```bash
make up
```

(or `./scripts/dev-up.sh` without `make`). The first run creates `deploy/compose/.env` with random development passwords, builds the images, applies the database migrations (the one-off `migrate` service, which also creates the `default` tenant, its roles, its Keycloak realm and one development user per role) and waits until every service is healthy (a few minutes the first time). Then open:

| What | Address |
| --- | --- |
| Welcome page (English and Arabic) | http://localhost:3000 |
| API readiness | http://localhost:8080/health/ready |
| OpenAPI document | http://localhost:8080/openapi/v1.json |
| API explorer | http://localhost:8080/api-explorer |
| Keycloak admin (credentials in `deploy/compose/.env`) | http://localhost:8180 |
| Sign-in emails (Mailpit, development only) | http://localhost:8025 |
| UAE PASS mock (development only) | http://localhost:8095 |
| RabbitMQ admin (`RABBITMQ_USER` / `RABBITMQ_PASSWORD`) | http://localhost:15672 |
| Monitoring: Grafana dashboards, traces and logs (`GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD`) | http://localhost:3001 |

**Sign in** from the header. Development users: `requester@zelvo.test`, `agent@zelvo.test`, `lead@zelvo.test`, `approver@zelvo.test`, `admin@zelvo.test` and `executive@zelvo.test`, all with the password `ZELVO_DEV_USERS_PASSWORD` from `deploy/compose/.env`. Agents, team leads and admins are asked to set up an authenticator app at their first sign-in. To try the email one-time code, choose "Try Another Way" on the sign-in page; the code arrives in Mailpit. See `docs/architecture/identity-access.md`.

**UAE PASS:** as `admin@zelvo.test`, open Settings → Sign-in → UAE PASS, choose "Development mock", client ID `zelvo-mock` and the secret `UAEPASS_MOCK_CLIENT_SECRET` from `deploy/compose/.env`, and save. The sign-in page then offers UAE PASS. See `docs/architecture/uae-pass.md`.

**Incidents (B1):** requesters report incidents and follow them under "My incidents" (`/en/my-incidents`); staff work them under "Incidents" (`/en/incidents`); administrators set the priority matrix, on-hold reasons, number prefix, auto-close period and attachment rules in Settings. Attachments are scanned for malware by the `clamav` service before anyone can open them; ClamAV takes a minute or two to load its definitions after the first start, and files wait until then. Offline installations import definitions with `scripts/clamav-import-definitions.sh`. See `docs/architecture/tickets.md`.

**Security in development (A5):** OpenSearch and RabbitMQ use TLS with a development CA (made by the one-off `certs` service) and one user per service; the master key that encrypts stored secrets is `deploy/compose/secrets/master.key` (created once, git-ignored, keep it: without it stored secrets cannot be decrypted). See `docs/architecture/security-baseline.md` and `docs/architecture/secrets-encryption.md`.

**Demo installation:** `make demo` starts a separate demo (its own data; stop the development stack first with `make down`) with two sample organisations, a government entity and a private company. Open "Demo" under the header to sign in as any persona with one click, switch organisation, or, as the demo administrator, reset the demo. `make demo-down` stops it, `make demo-wipe` deletes its data. See `docs/architecture/demo-mode.md`.

**Integrations (E1):** as `admin@zelvo.test`, open Settings → Integrations to create API keys (sent in `X-Api-Key`) and OAuth clients, webhooks with a tester and delivery log, and to read the integration log; Settings → Import and export moves users in CSV or Excel. In development, use `http://webhook-receiver:8096/hook` as a webhook URL and see what arrived at http://localhost:8096/received. **Test installation:** `make test-installation` starts a separate installation for building integrations (its own data; "Test installation" on every page and `test` in every API response and webhook); `make test-installation-down` stops it, `make test-installation-wipe` deletes its data. See `docs/architecture/integrations.md`.

**AI (E4):** AI is off until it is set up. As `admin@zelvo.test`, open Settings → AI, connect a provider (Anthropic Claude, OpenAI, Azure OpenAI, Google Gemini or any OpenAI-compatible endpoint) with your own key, test it, and choose a provider and model per feature; Usage and cost shows the month's calls, tokens and cost against the monthly limit, and the AI log every call with personal data masked. In development, the `ai-mock` service needs no key: add an "OpenAI-compatible" provider with the endpoint `http://ai-mock:8097/v1` and the model `zelvo-mock-fast`; http://localhost:8097/received shows what it was sent. See `docs/architecture/ai-gateway.md`.

**Knowledge base (D1):** as `agent@zelvo.test`, open Knowledge → New article, write it in English and/or Arabic (Markdown, with images) and send it for review; as `lead@zelvo.test`, open the review queue and approve it (someone other than the author must). External articles appear in Help (`/en/help`), also for visitors who are not signed in unless Settings → Knowledge turns that off. A resolved incident has "Turn into a knowledge article". See `docs/architecture/knowledge.md`.

`make down` stops the stack, `make reset` also deletes its data, `make logs` follows the logs. Local Compose changes go in `deploy/compose/compose.override.yaml`, which is git-ignored.

## Repository layout

```
docs/            requirements, build plan, architecture notes, security (ASVS L2 checklist)
src/api/         ASP.NET Core API, data layer and migrations (Zelvo.Data), licensing (Zelvo.Licensing), backend tests
src/web/         Next.js frontend (portal, agent workspace, admin)
src/worker/      background jobs
src/updater/     updater service that installs update packages (Settings → Updates)
src/tools/       zelvo-sign, the offline signing tool for licenses and update packages (signing computer only)
src/keycloak/    Keycloak extension (email one-time code sign-in) and login/email theme, built into Zelvo's Keycloak image
build/obfuscar/  release obfuscation (Obfuscar rules and script)
deploy/compose/  Docker Compose for development (compose.yaml) and the hardened small install (compose.production.yaml)
deploy/postgres/ PostgreSQL image with pgBackRest for the hardened install's backups
deploy/helm/     Helm chart for Kubernetes installs (medium and large)
tests/e2e/       Playwright end-to-end tests
scripts/         start and stop scripts; release/ (bundle and install), backup/ (restore drill)
```

## Install for customers

Customers never build Zelvo: the release pipeline (`.github/workflows/release.yml`) builds signed,
obfuscated release images and an offline install bundle. Small installs unpack the bundle and run
`sudo ./install.sh` (hardened Docker Compose, backups with pgBackRest); medium and large installs
use the Helm chart (`deploy/helm/README.md`). See `docs/architecture/release.md` and
`docs/architecture/backup-restore.md`.

## Develop and test

| Part | Tools | Commands |
| --- | --- | --- |
| Backend | .NET 10 SDK, Docker (integration tests start PostgreSQL) | `dotnet build Zelvo.slnx`, `dotnet test --solution Zelvo.slnx`; new migration: `dotnet tool restore`, then `dotnet ef migrations add <Name> -p src/api/Zelvo.Data -s src/api/Zelvo.Data -o Migrations` |
| Keycloak extension | Java 21, Maven | in `src/keycloak`: `mvn verify` (or `make test-keycloak`) |
| Frontend | Node.js 24 | in `src/web`: `npm ci`, `npm run dev`, `npm run lint`, `npm run typecheck`, `npm test` |
| End-to-end | Node.js 24, running stack | `make up`, then `make e2e` |
| Demo and accessibility audit | Node.js 24, the demo installation | `make demo`, then `make e2e-demo` (demo mode, then the WCAG 2.2 AA audit of every page in English and Arabic: `docs/accessibility/wcag-2.2-audit.md`) |
| Load tests | k6, Node.js 24, a dedicated test server | see `tests/load/README.md` (API p95 under 200 ms, 1,000 concurrent agents) |

CI (`.github/workflows`) runs the backend, Keycloak extension and frontend builds and tests, starts the stack and runs the end-to-end and accessibility tests, builds the images, scans them with Grype, produces a CycloneDX SBOM per image, scans for secrets with Gitleaks, and runs CodeQL. Dependabot keeps dependencies current.
