# Security baseline (task A5)

Rate limiting, transport security and least privilege for the services behind Zelvo, and where the OWASP ASVS Level 2 checklist stands. Related: encryption at rest for stored secrets (`secrets-encryption.md`), observability (`observability.md`), the checklist (`docs/security/asvs-l2-checklist.md`).

## Rate limiting

Baseline limits on `/api`, in the API (`Security/RateLimiting`), counted in fixed one-minute windows in Valkey so that every API instance shares them:

| Caller | Counted per | Default (`RateLimiting:*`) |
| --- | --- | --- |
| Signed in | tenant and user (`sub`) | `SignedInPermits` 600 a minute |
| Visitor | client address | `VisitorPermits` 120 a minute |

- `RateLimiting:Enabled` (true) and `RateLimiting:Window` (1 minute) can be changed per installation. These are installation settings that protect the system as a whole; the admin-configurable limits per API key come with module 14 (task E1).
- Every `/api` response carries `RateLimit-Limit`, `RateLimit-Remaining` and `RateLimit-Reset` (seconds). A refused request gets **429** with problem code `rate-limited` and `Retry-After`; the web app shows "Too many requests…" in English or Arabic.
- Keys in Valkey are SHA-256 hashes (`zelvo:rl:<kind>:<hash>`), so the cache holds no user ids or addresses.
- When Valkey is unavailable the API **allows** requests (an unavailable cache must not stop the service), logs a warning and counts `zelvo_rate_limit_store_errors_total`; an alert fires after 5 minutes. Sign-in itself is protected by Keycloak's brute-force protection either way.
- Probes (`/health/*`), the OpenAPI document and the API explorer are not limited.

## Client addresses behind proxies

The API uses `X-Forwarded-For` and `X-Forwarded-Proto` only from the proxies listed in `Network:TrustedProxies` (addresses or networks, for example `10.0.4.0/24`), and only the last hop. The web app sends the browser's address with every API call: `TRUSTED_PROXY_HOPS` tells it how many reverse proxies are in front of it (0 in development; 1 behind the TLS proxy of an install), and it takes the address the outermost trusted proxy saw, ignoring what the client wrote before. This address is used for visitors' rate limits and recorded as `clientAddress` in the audit log.

In development Compose every local browser reaches the stack from the Docker gateway, so visitors share one limit; `ZELVO_RATE_LIMIT_VISITORS` (default 1200 in development) raises it.

## OpenSearch

The security plugin is on (Compose: `deploy/compose/security/opensearch`):

- HTTPS and node-to-node TLS, **TLS 1.3 only**, with certificates from the installation's CA.
- Users, stored as bcrypt hashes: `admin` (operators, `all_access`), `zelvo-api` (role `zelvo_search_reader`: search, count and alias reads on `zelvo-*`), `zelvo-worker` (role `zelvo_search_indexer`: all index actions on `zelvo-*` only, bulk and reindex). Neither service user can read other indices or the security configuration. Every call of the indexing pipeline was checked against these roles.
- Clients send credentials only over HTTPS (`Search:Username`, `Search:Password`) and trust the CA in `Search:CaFile`.
- In development the security index is created from these files at the first start; changing a password afterwards needs `make reset` or the plugin's `securityadmin` tool (certificate `admin.crt`).

The search integration tests still run OpenSearch with security off (no credentials needed); the Compose stack and end-to-end tests run it with security on.

## RabbitMQ

- No plain AMQP listener: **TLS 1.3** on 5671 only (`deploy/compose/security/rabbitmq/10-zelvo.conf`). Clients use `amqps://` and trust the CA in `RabbitMq:CaFile`; Zelvo refuses plain `amqp://` except to localhost.
- Virtual host `zelvo`. `zelvo-api` may only connect (readiness); `zelvo-worker` may configure, write and read only `zelvo.*` queues and exchanges; the administrator (`RABBITMQ_USER`) uses the management UI. `guest` is not created.
- Users and permissions come from a definitions file written by `scripts/dev-up.sh` from `.env` at every start (`deploy/compose/secrets/rabbitmq-definitions.json`, git-ignored).

## Certificates

The one-off Compose service `certs` (`deploy/compose/security/generate-certs.sh`) creates a development CA and ECDSA P-256 certificates for OpenSearch (node and admin) and RabbitMQ in the `certs` volume, renewed when fewer than 30 days are left. Each service mounts only its own folder; the API and worker mount only the CA certificate. Installs use certificates from their own CA or a public one (task F6).

## Least privilege per service

| Service | PostgreSQL | OpenSearch | RabbitMQ | Valkey |
| --- | --- | --- | --- | --- |
| API | application role, no `BYPASSRLS` (A1) | `zelvo-api` (read) | `zelvo-api` (connect) | shared password |
| Worker | application role | `zelvo-worker` (zelvo-* indices) | `zelvo-worker` (zelvo.*) | — |
| migrate | owner (migrations only) | — | — | — |

Still open (tracked in the ASVS checklist): TLS for PostgreSQL, Valkey and the internal HTTP calls (Keycloak back channel, OTLP), separate Valkey users, service passwords in a secrets manager, CSP and HSTS for the web app.
