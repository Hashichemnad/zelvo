# Third-party components and resource-demanding functionality (task F7)

The third-party components Zelvo is built from and ships, how risky each is, how vulnerabilities in them are found and how quickly they must be fixed, and which functions use a lot of resources and what limits them. Covers OWASP ASVS 5.0 V15.1.1 (remediation time frames), V15.1.2 (SBOM and inventory), V15.1.3 (resource-demanding functionality), V15.1.4 and V15.2.1 (risky components and keeping to the time frames). Related: `docs/architecture/communications.md`, `docs/security/crypto-inventory.md`, `docs/architecture/security-baseline.md` (rate limits).

Versions as pinned on 2026-11-26 in `Directory.Packages.props`, `src/web/package.json`, `src/keycloak/pom.xml`, the Dockerfiles and `deploy/compose/compose.yaml`. The CycloneDX SBOM built for each image in CI (below) is the complete list including transitive dependencies; this document lists what matters for risk.

## Remediation time frames

**Proposed, for the product owner's approval.** Counted from the day an advisory affecting a shipped component is published (or reaches us through the tools below).

| Severity (CVSS or the advisory's rating) | Fixed in | Delivered as |
| --- | --- | --- |
| Critical, or known to be exploited | 7 days | Security update package (F4), free for every customer |
| High | 30 days | Security update package |
| Medium and low | The next release | The next security or feature release |

- An advisory that does not affect Zelvo (the vulnerable code is not used or not in the image) may be accepted instead of fixed: for npm in `src/web/audit-exceptions.json` with a reason and a review date at most 30 days ahead (`src/web/scripts/audit.mjs` fails CI on expired or unused exceptions); for NuGet and image findings the same rule applies, recorded in the pull request that accepts them. Accepting needs the product owner's approval; none are accepted today.
- General updates: Dependabot minor and patch updates are reviewed within 30 days; LTS runtimes (.NET, Node.js, PostgreSQL) are kept on a supported LTS line and their majors reviewed once a year. Major upgrades that Dependabot ignores (Node.js, OpenTelemetry) are done by hand (decisions log 2026-11-20).
- Before the Phase 1 penetration test, open Dependabot pull requests are checked against these windows (several tasks deliberately left them unhandled).

## How vulnerabilities are found

| Tool | What it covers | When | Fails the build on | Reference |
| --- | --- | --- | --- | --- |
| Dependabot | NuGet, npm (`src/web`, `tests/e2e`), Maven (`src/keycloak`), Dockerfiles, Compose images, GitHub Actions | Weekly pull requests; security alerts as published | n/a (pull requests) | `.github/dependabot.yml` |
| NuGet vulnerability check | Every .NET package, transitive included | Every pull request and push to `main` | Any vulnerable package | `ci.yml` (`dotnet list … package --vulnerable --include-transitive`) |
| npm audit script | The web app's runtime dependencies | Every pull request and push | High or critical, unless an approved exception applies | `src/web/scripts/audit.mjs`, `audit-exceptions.json` |
| Grype | Each image: api, worker, web, keycloak, opensearch, updater | Every pull request and push | Fixable critical findings | `ci.yml` job `images` (`anchore/scan-action`, `severity-cutoff: critical`, `only-fixed: true`) |
| SBOM | Each of those images, CycloneDX JSON | Every pull request and push; kept 30 days as a CI artifact | n/a | `ci.yml` (`anchore/sbom-action`) |
| CodeQL | C#, JavaScript/TypeScript, Java, GitHub Actions | Every pull request, push and weekly (Monday) | Alerts reviewed in GitHub | `.github/workflows/codeql.yml` |
| Gitleaks | Secrets in commits | Every pull request (its commits) and push (full history) | Any finding | `ci.yml` job `secrets` |

Gaps against the time frames (ASVS V15.2.1): `ci.yml` has no schedule, so a new advisory against unchanged dependencies is seen only through Dependabot or the next pull request; Grype fails only on critical findings, so high findings in images are not reported in CI. Proposed: a weekly scheduled run of the NuGet check, the npm audit and Grype, and a non-failing Grype report at `high`.

## Risk groups

### Dangerous functionality (untrusted input or wide privileges)

| Component | Version | What it does in Zelvo | Why it is risky | Controls |
| --- | --- | --- | --- | --- |
| Docker Engine socket (updater) | Docker CLI 29.9.3, Compose 5.8.0 | Loads images, stops and starts services, runs backups (F4) | Root on the host | No port; acts only on installs recorded by `installation.manage` holders for packages whose vendor signature it checks again; one install at a time (unique index) |
| Chromium headless shell (worker) | From `playwright-core` 1.65.1 | Renders branded PDF exports from Zelvo's own HTML (F1, F2) | Large browser engine rendering tenant text; runs with `--no-sandbox` in the container | Network blocked (`--host-resolver-rules=MAP * ~NOTFOUND`), page CSP `default-src 'none'` (no scripts), extensions off, fresh profile per run, 60-second timeout and the whole process tree killed, worker runs as a non-root user (`DocumentExports.cs`) |
| ClamAV | 1.5.4 (official image) | Scans every attachment, form upload, voice note, service and branding file (B1) | Parses untrusted files, including archives | Separate container reachable only by the worker; size limits `StreamMaxLength`/`MaxFileSize` 105 MB, `MaxScanSize` 400 MB; files unavailable until clean |
| MailKit / MimeKit | 4.18.2 | Reads mailboxes (IMAP, Graph, Gmail), parses incoming email, sends and DKIM-signs email (B5, E2) | Parses untrusted MIME from the internet | HTML read as text, attachments under the tenant's rules and scanned, automatic mail and `dmarc=fail` dropped, TLS required to mail servers |
| DocumentFormat.OpenXml | 3.5.1 | Reads uploaded Excel files (bulk import, payment settlement files) and writes exports | Parses untrusted ZIP/XML | 5 MB / 5,000 rows (bulk), 10 MB / 20,000 rows (settlement), `MaxCharactersInPart`; formula injection neutralised on export. Open: entry count and total unpacked size not limited before opening (ASVS V5.2.3) |
| `System.IO.Compression` (update packages) | .NET 10 | Reads `.zpkg` packages up to 8 GiB | Untrusted archive until verified | Manifest size checked, signature verified before any other entry is opened, entry list and sizes must match the signed manifest (`UpdatePackage.cs`) |
| Elsa Workflows | 3.10.2 | Runs approval processes embedded in the API (C3) | Workflow engine with its own persistence | No Elsa Studio and no Elsa HTTP API; definitions are Zelvo's code, rules are Zelvo data; Elsa tables under forced row-level security; advisory locks in PostgreSQL |
| Novell.Directory.Ldap.NETStandard | 4.1.0 | Syncs AD/LDAP directories (E7) | Network protocol client to customer servers; community project with few maintainers | LDAPS or required StartTLS, hosts checked by the outgoing-request policy, read-only service account, paged search |
| ModelContextProtocol (C# SDK) | 2.4.0 | MCP server for AI agents (E6) and client for customers' MCP servers | Tool calls from AI agents; answers from external servers | Server only for integrations made AI agents, off by default, approval for changes; client through the AI outgoing-request policy, answers masked and treated as data |
| Keycloak and the Zelvo extension | 26.8.2 (image 26.8.2-3), extension 0.1.0 | Sign-in, tokens, MFA, UAE PASS and Entra brokering, LDAP federation | Identity provider for every tenant; the extension runs inside sign-in flows | One realm per tenant, brute-force protection, extension tested (`src/keycloak`), image and libraries pinned to the same version (`scripts/check-keycloak-version.sh`) |
| Caddy | 2.10.2 | Public TLS, on-demand ACME, routing to the web app and hooks (F1) | Internet-facing; on-demand certificates could be abused | Certificates only for hosts `/internal/tls/ask` approves (shared secret), TRACE/TRACK refused |
| DnsClient | 1.8.0 | DNS checks for domains, SPF, DKIM, DMARC (F1) | Parses DNS answers; community project with few maintainers | 5-second timeout, one retry, no cache, results only shown as checks |

### Core runtime and libraries

| Area | Components and versions |
| --- | --- |
| .NET backend | .NET 10 (SDK and ASP.NET Core images `mcr.microsoft.com/dotnet/*:10.0`), ASP.NET Core OpenAPI 10.0.15, Microsoft.Extensions.Hosting 11.0.0, EF Core 10.0.15, Npgsql 11.0.0, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.4, EFCore.NamingConventions 10.0.1, StackExchange.Redis 3.4.35, RabbitMQ.Client 7.3.0, Microsoft.IdentityModel 8.23.0, OpenTelemetry 1.21.0, AspNetCore.HealthChecks 9.0.0, DistributedLock.Postgres 1.3.2, Scalar.AspNetCore 3.2.0 (API explorer, off in production) |
| Web app | Node.js 24 LTS (`node:24-alpine`), Next.js 16.4, React 19.3, next-intl 4.15, openid-client 6.9, redis 6.3, @microsoft/signalr 10.0.11, react-markdown 10.1.0 with remark-gfm 4.0.1 (no raw HTML), dnd-kit (core 6.3.1, sortable 10.0.0), @vercel/otel 2.3 with OpenTelemetry JS, bundled @fontsource fonts (OFL-1.1) |
| Keycloak extension | Java, `org.keycloak` 26.8.2 SPI libraries, built with Maven on Eclipse Temurin |
| Data and messaging services | PostgreSQL 17 with pgvector (`pgvector/pgvector:pg17`), Valkey 9.2, RabbitMQ 4.4, OpenSearch 3.10.0 with the ICU plugin (`zelvo/opensearch`) |
| Observability | OpenTelemetry Collector 0.165.0, Prometheus 3.16.0, Tempo 3.1.1, Loki 3.7.10, Grafana 13.3.1 |
| Development only (not for installations) | Mailpit 1.34, GreenMail 2.1.5, the Node mocks (`ai-mock`, `payment-mock`, `uaepass-mock`, `webhook-receiver`), xunit, Testcontainers, Playwright, Vitest, k6 scripts |

## Resource-demanding functionality

Functions that can use a lot of CPU, memory, disk, network or money, and the limits that protect them. Rule for new features: anything that may take longer than about 10 seconds is queued and polled, not done inside the request (the web proxy gives API calls 10 seconds by default, `src/web/src/lib/api.ts`).

| Function | Cost | Limits (verified in code) |
| --- | --- | --- |
| Every API call | CPU, database | Per user 600 a minute, per visitor address 120 a minute, per integration its own limit; 429 `rate-limited` (`Security/RateLimiting`) |
| PDF exports of tickets and reports (F1, F2) | Chromium process, memory | Queued for the worker (`document_exports`); at most 5 pending per person (429 `too-many-exports`); 60-second render timeout (`Pdf:Timeout`), process tree killed; kept 1 day |
| Report dashboards and CSV/Excel exports (F2) | Aggregate SQL | Period at most 731 days; filters at most 50 groups and 50 categories; aggregate SQL on the tenant's connection; Npgsql's default 30-second command timeout |
| Scheduled reports (F2) | PDF and email per recipient | Worker job with leases; at most 50 recipients per schedule (`ReportSchedule.MaxRecipients`) |
| Bulk import (E1, C1, C2) | Parsing, validation | 5 MB request, 5,000 rows, checked before anything changes (`BulkEndpoints`) |
| Payment settlement import (E8) | Parsing | 10 MB, 20,000 rows (`PaymentEndpoints`) |
| Bulk incident actions (B4) | Many changes in one transaction | 100 incidents (`BulkIncidentEndpoints.MaxTickets`) |
| Attachments and uploads (B1, C1) | Storage, scanning | Tenant limit up to 100 MB per file (`AttachmentPolicy.InstallationMaxSizeMb`, default 25 MB), request size limits on the upload endpoints, forms 1–20 files; scanning asynchronous in the worker |
| Voice notes and messaging media (E3) | Storage, transcription | 16 MB per item (`Channels:MaxMediaBytes`); transcription off by default |
| Search index rebuild (A4) | Re-indexing every record | `search.manage` only; queued through the outbox and indexed by the worker in batches through RabbitMQ; the status shows a rebuild in progress. No limit on repeated requests was found |
| AI features (E4, E5) | Provider cost, latency | Monthly spending limit in USD per tenant (warning at 80 %, stop at 100 %, `AiBudget`); background work queued (`ai_jobs`, retries after 1, 5, 30 minutes); 60-second call timeout (`Ai:Timeout`) |
| Update package upload and install (F4) | Disk, downtime | Streamed to disk, at most 8 GiB (`Updates:MaxPackageBytes`), the web proxy allows 1 hour for the upload; one install scheduled or running at a time; `installation.manage` only |
| Outgoing calls: webhooks, connectors, mail, messaging, payments (E1–E8) | Network, worker time | Worker queues with retries; timeouts 10 s (webhooks), 60 s (AI, channels, connectors), 30 s (payments); answers capped (webhooks 4 KB kept, connectors and payments 1 MB) |
| Mailbox reading (E2) | Network, parsing | Every 30 seconds per mailbox with leases and back-off |
| Privacy retention jobs (F5) | Anonymisation | Hourly, advisory lock per tenant, at most 200 tickets per record type and 200 accounts per run |
| Reset demo (D) | Empties and reseeds tenants | Demo installations and `demo.reset` only; the web proxy allows 300 seconds |
| Live updates (B4) | Open connections | SignalR over Server-Sent Events and long polling through the web proxy; ids only |
| License import (F3) | Parsing | 1 MB request limit |

Open points: an explicit command timeout for report queries (ASVS V15.2.2) and the Excel archive limits (V5.2.3).
