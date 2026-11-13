# Zelvo

Bilingual (English/Arabic) IT service management platform for the UAE market, designed to be hosted by each customer on its own servers.

- Requirements: [docs/requirements.md](docs/requirements.md)
- Build instructions and progress: [CLAUDE.md](CLAUDE.md)
- Phase 1 build plan: [docs/phase1-build-plan.md](docs/phase1-build-plan.md)

**Proprietary software. All rights reserved.** This repository is visible publicly during development, but no rights to use, copy, modify or distribute it are granted. See [LICENSE](LICENSE).

## Run it

You need Docker with the Compose plugin and about 4 GB of free memory. From the repository root:

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
| RabbitMQ admin | http://localhost:15672 |

**Sign in** from the header. Development users: `requester@zelvo.test`, `agent@zelvo.test`, `lead@zelvo.test`, `approver@zelvo.test`, `admin@zelvo.test` and `executive@zelvo.test`, all with the password `ZELVO_DEV_USERS_PASSWORD` from `deploy/compose/.env`. Agents, team leads and admins are asked to set up an authenticator app at their first sign-in. To try the email one-time code, choose "Try Another Way" on the sign-in page; the code arrives in Mailpit. See `docs/architecture/identity-access.md`.

`make down` stops the stack, `make reset` also deletes its data, `make logs` follows the logs. Local Compose changes go in `deploy/compose/compose.override.yaml`, which is git-ignored.

## Repository layout

```
docs/            requirements, build plan, architecture notes (tenancy and audit, identity and access, API conventions, licensing)
src/api/         ASP.NET Core API, data layer and migrations (Zelvo.Data), licensing contract and stub, backend tests
src/web/         Next.js frontend (portal, agent workspace, admin)
src/worker/      background jobs
src/keycloak/    Keycloak extension (email one-time code sign-in) and login/email theme, built into Zelvo's Keycloak image
deploy/compose/  Docker Compose for development
deploy/helm/     Helm charts for Kubernetes installs (later Phase 1 task)
tests/e2e/       Playwright end-to-end tests
scripts/         start and stop scripts
```

## Develop and test

| Part | Tools | Commands |
| --- | --- | --- |
| Backend | .NET 10 SDK, Docker (integration tests start PostgreSQL) | `dotnet build Zelvo.slnx`, `dotnet test --solution Zelvo.slnx`; new migration: `dotnet tool restore`, then `dotnet ef migrations add <Name> -p src/api/Zelvo.Data -s src/api/Zelvo.Data -o Migrations` |
| Keycloak extension | Java 21, Maven | in `src/keycloak`: `mvn verify` (or `make test-keycloak`) |
| Frontend | Node.js 22 | in `src/web`: `npm ci`, `npm run dev`, `npm run lint`, `npm run typecheck`, `npm test` |
| End-to-end | Node.js 22, running stack | `make up`, then `make e2e` |

CI (`.github/workflows`) runs the backend, Keycloak extension and frontend builds and tests, starts the stack and runs the end-to-end and accessibility tests, builds the images, scans them with Grype, produces a CycloneDX SBOM per image, scans for secrets with Gitleaks, and runs CodeQL. Dependabot keeps dependencies current.
