# Communications (task F7)

Every network connection Zelvo accepts or makes: who connects to whom, over which protocol and port, whether it is encrypted today, how the caller is authenticated and which setting limits it. Covers OWASP ASVS 5.0 V13.1.1 and is the firewall reference for installations (task F6). Related: `security-baseline.md` (TLS, least privilege), `docs/security/crypto-inventory.md` (keys and certificates), `integrations.md` (outgoing-request policy).

Checked against `deploy/compose/compose.yaml`, `deploy/compose/caddy/Caddyfile` and the code on 2026-11-26. Ports are the container ports; Compose publishes most of them on `127.0.0.1` only, for development. A task that adds a connection updates this document.

## Outgoing-request policy

Every address a tenant administrator gives Zelvo goes through `OutboundAddressPolicy` (`src/api/Zelvo.Integrations/OutboundAddressPolicy.cs`): checked when saved and again on the address actually connected to after DNS, no redirects followed, no system proxy. Loopback, link-local (including cloud metadata 169.254.169.254), unspecified, multicast and reserved addresses are always refused; private networks only when the operator lists them; plain HTTP only when the operator allows it. A stored credential is never sent to a new address without being entered again.

| Kind of destination | Operator settings | HTTP allowed by default |
| --- | --- | --- |
| Outgoing webhooks (E1) | `Integrations:Webhooks:AllowedPrivateNetworks`, `Integrations:Webhooks:AllowInsecureHttp` | No |
| AI providers (E4) and customers' MCP servers (E6) | `Ai:AllowedPrivateNetworks`, `Ai:AllowInsecureHttp` | No |
| Mailboxes and mail providers (E2), messaging providers and SMS gateways (E3) | `Channels:Network:AllowedPrivateNetworks`, `Channels:Network:AllowInsecureHttp`; Teams answers only to `Channels:TeamsServiceHosts` | No |
| Payment gateways (E8) | `Payments:AllowedPrivateNetworks`, `Payments:AllowInsecureHttp` | No |
| Connectors: directories, CRMs, REST (E7) | `Connectors:Network:AllowedPrivateNetworks`, `Connectors:Network:AllowInsecureHttp`; LDAP hosts checked before connecting; REST calls kept on the base address's host and path | No |

Development Compose allows plain HTTP and the private ranges `10.0.0.0/8`, `172.16.0.0/12` and `192.168.0.0/16` for its local test services (mocks, GreenMail); installations leave both off and list only their own internal networks.

## Inbound (from outside the installation)

| From | To | Protocol, port | TLS today | Authentication | Notes |
| --- | --- | --- | --- | --- | --- |
| Browsers (portal, agents, administrators, visitors) | Caddy (`proxy`) → web app | HTTPS 443, HTTP 80 redirects to HTTPS (Compose publishes 8443 and 8088 on **all** interfaces) | Yes: TLS 1.2 and 1.3 (Caddy defaults); ACME certificate, Caddy's internal CA in development, or a tenant's uploaded certificate | Web session cookie (server-side session), CSRF token and Origin check on changes | TRACE and TRACK answered 405 (`Caddyfile`) |
| Browsers | Keycloak sign-in pages | HTTP 8080 (Compose publishes `127.0.0.1:8180`) | **No** in Compose (`start-dev`); realms require HTTPS for external addresses (`sslRequired: external`) | Password, email code, TOTP, UAE PASS, Entra | Behind HTTPS in F6 |
| WhatsApp (Meta), Twilio, generic SMS gateways, Microsoft Teams (Bot Framework), monitoring tools | Caddy `/hooks/{tenant}/…` → API `/api/v1/hooks/…` | HTTPS 443 | Yes (Caddy) | Meta HMAC-SHA256; Twilio signature; gateway token; Bot Framework JWT; monitoring source token | Caddy sets `X-Zelvo-Tenant` from the path |
| Payment gateways (notifications and payer return) | Caddy → web app `/payments/callback/{reference}` | HTTPS 443 | Yes | Gateway HMAC when configured; every notification only triggers a status API check by Zelvo | `payments.md` |
| Customer websites embedding the chat widget | Caddy → web app `/chat-widget/…`, `chat-widget.js` | HTTPS 443 | Yes | Chat token per visitor; `frame-ancestors` limited to the widget's websites | `channels.md` |
| Integrations (API keys, OAuth clients) and MCP clients (`/api/v1/mcp`) | Caddy `/api/v1/…` → API | HTTPS 443 | Yes (Caddy) | `X-Api-Key` or OAuth client-credentials token | Task M2: the proxy routes `/api/v1/*` to the API in Compose, the hardened Compose and Helm, with HSTS and the TRACE refusal; `/api/v1/hooks` is not routed (webhooks use `/hooks/{tenant}/…`); tenants other than the default send `X-Zelvo-Tenant` |
| Operators | Grafana, RabbitMQ management, OpenSearch, PostgreSQL, Valkey | Published on `127.0.0.1` only in Compose | Grafana and RabbitMQ management plain HTTP; OpenSearch HTTPS | Each tool's own sign-in; Grafana has no anonymous access or sign-up | Not published in F6 |
| Operators (USB) | Updates inbox (`updates` volume) | File system | n/a | Server access | Packages are signed (`updates.md`) |

## Internal (between the installation's services)

| From | To | Protocol, port | TLS today | Authentication |
| --- | --- | --- | --- | --- |
| API, worker, updater | PostgreSQL | PostgreSQL 5432 | **No** | Application role (no `BYPASSRLS`), password; the three share it |
| migrate | PostgreSQL | 5432 | **No** | Owner (superuser), migrations only |
| Keycloak | PostgreSQL (`keycloak` database) | 5432 | **No** | Keycloak's own user |
| API (rate limits) | Valkey (`redis`) | RESP 6379 | **No** | Shared password |
| Web app (sessions, database 1) | Valkey | 6379 | **No** | Same shared password |
| API (readiness), worker (search relay) | RabbitMQ | AMQPS 5671 (no plain listener) | Yes, TLS 1.3, trusting only the installation's CA | `zelvo-api` (connect only), `zelvo-worker` (`zelvo.*` only), vhost `zelvo` |
| API (search), worker (indexing) | OpenSearch | HTTPS 9200 | Yes, TLS 1.3, installation's CA | `zelvo-api` (read), `zelvo-worker` (write `zelvo-*`) |
| Worker | ClamAV (`clamd`) | clamd protocol 3310 | **No** (clamd has no TLS) | None; reachable only on the Compose network |
| Web app (server pages, BFF proxy) | API | HTTP 8080 (`API_INTERNAL_URL`) | **No** | The session's access token; tenant header |
| Web app | Keycloak (token exchange, sign-out) | HTTP 8080 (`KEYCLOAK_INTERNAL_URL`) | **No** | `zelvo-web` confidential client secret |
| API | Keycloak admin API, realm keys, health | HTTP 8080, management 9000 | **No** | `zelvo-provisioner` client credentials, short-lived tokens |
| migrate | Keycloak admin API | HTTP 8080 | **No** | Bootstrap administrator and provisioner client |
| Caddy | Web app | HTTP 3000 | **No** | None (all public traffic) |
| Caddy | API `/api/v1/hooks/…` | HTTP 8080 | **No** | Provider proofs, checked by the API |
| Caddy | API `/internal/tls/ask`, `/internal/tls/certificate` | HTTP 8080 | **No** | `ZELVO_TLS_TOKEN` (query string, because Caddy's `ask` cannot send headers) |
| API, worker, updater | OpenTelemetry Collector | OTLP gRPC 4317 | **No** | None |
| Web app | Collector | OTLP HTTP 4318 | **No** | None |
| Keycloak | Collector | OTLP gRPC 4317 (traces) | **No** | None |
| Collector | Prometheus 9090, Tempo 4317, Loki 3100 | OTLP | **No** (`tls: insecure`) | None |
| Prometheus | Keycloak 9000, RabbitMQ 15692, Collector 8888 | HTTP scrape | **No** | None |
| Grafana | Prometheus 9090, Tempo 3200, Loki 3100 | HTTP | **No** | None |
| Updater | Docker Engine | Unix socket `/var/run/docker.sock` | n/a | File access (root on the host); listens on no port |
| Updater | Compose services' health addresses | HTTP | **No** | None |
| ClamAV (`freshclam`) | API private mirror `/_internal/clamav/…` (M1; not routed by the proxy) | HTTP 8080 | **No** (public definition files; ClamAV checks their Talos signature) | None |
| API, worker | Vault or OpenBao Transit (when used) | HTTPS (`Secrets:Vault:Address`) | Yes, trusting `Secrets:Vault:CaFile` | Token file or AppRole |
| Worker | Headless Chromium (child process) | None: network blocked (`--host-resolver-rules=MAP * ~NOTFOUND`, page CSP `default-src 'none'`) | n/a | n/a |

## Outbound set by the operator

| From | To | Protocol, port | TLS | Authentication | Setting |
| --- | --- | --- | --- | --- | --- |
| Worker (notifications, reports), API (test email) | Installation mail server | SMTP 587 (STARTTLS required) or 465 (TLS on connect); `None` only when chosen | Required unless `Security=None` (F7: `Auto` never falls back to plain) | User and password | `Notifications:Smtp:*` (`ZELVO_SMTP_*`); Compose sends to Mailpit without TLS |
| Keycloak | Mail server for sign-in codes and password resets | SMTP | Per realm settings (`starttls`, `ssl`) | User and password | `Identity:Provisioning:Smtp:*` |
| Caddy | ACME CA | HTTPS 443 (and port 80 or 443 reachable for the challenges) | Yes | ACME account key | `ZELVO_ACME_CA`, `ZELVO_ACME_EMAIL` |
| ClamAV (`freshclam`) | ClamAV's definition servers (`database.clamav.net`), only when the API's private mirror redirects there | HTTPS | Yes | None | Settings → Updates → Malware definitions, "Download definitions automatically" (M1; `MalwareDefinitions:UpstreamMirror`) |
| API | DNS resolvers (domain verification, SPF, DKIM, DMARC checks) | DNS 53 | No (DNS) | None | System resolvers (`BrandingServices`, DnsClient) |
| API, worker | Vault or OpenBao | HTTPS | Yes | Token or AppRole | `Secrets:Vault:*` |

## Outbound set by tenant administrators

Every row goes through the outgoing-request policy above, except the ones Keycloak makes, which Keycloak's own client makes to the addresses Zelvo configures in the realm.

| Feature | From | To (examples) | Protocol | TLS | Authentication | Policy |
| --- | --- | --- | --- | --- | --- | --- |
| Outgoing webhooks (E1) | Worker | Receiver URLs | HTTPS 443 | Required, TLS 1.2–1.3 | Standard Webhooks HMAC signature | `Integrations:Webhooks:*` |
| AI providers (E4, E5; K1 rule builder) | API, worker | `api.anthropic.com`, `api.openai.com`, Azure OpenAI, `generativelanguage.googleapis.com`, OpenAI-compatible or self-hosted endpoints | HTTPS | Required, TLS 1.2–1.3 | Provider API key | `Ai:*` |
| Voice-note transcription (E3) | Worker | OpenAI, Azure OpenAI, Gemini or OpenAI-compatible | HTTPS | Required | Provider API key | `Ai:*` |
| Customers' MCP servers (E6) | API, worker | Streamable HTTP addresses | HTTPS | Required, TLS 1.2–1.3 | Stored credential | `Ai:*` |
| Mailboxes (E2) | Worker (`MailJob`), API (test) | IMAP 993/143, SMTP 465/587; `graph.microsoft.com`, `login.microsoftonline.com`; `gmail.googleapis.com`, `oauth2.googleapis.com` | IMAP, SMTP, HTTPS | IMAP and SMTP require TLS unless "No TLS" is chosen; HTTPS | Password, client secret, service-account JWT or refresh token | `Channels:Network:*` |
| WhatsApp (E3) | Worker, API | `graph.facebook.com` | HTTPS | Yes | Access token | `Channels:Network:*` |
| SMS (E3) | Worker, API | `api.twilio.com` or the generic gateway's address | HTTPS | Yes | Account credential | `Channels:Network:*` |
| Microsoft Teams (E3) | Worker, API | `login.microsoftonline.com`, the Bot Framework service address of the activity (`smba.trafficmanager.net`, `botframework.com`) | HTTPS | Yes | Bot app id and client secret | `Channels:Network:*`, `Channels:TeamsServiceHosts` |
| Payment gateways (E8) | API (`PaymentService`, `PaymentJobs`) | The gateway's create, status and refund addresses | HTTPS | Required unless `Payments:AllowInsecureHttp` | Gateway credentials, optional HMAC | `Payments:*` |
| Entra ID sync (E7) | Worker, API | `graph.microsoft.com`, `login.microsoftonline.com` | HTTPS | Yes | App client secret | `Connectors:Network:*` |
| AD/LDAP sync (E7) | Worker, API | Directory servers, LDAPS 636 (default) or 389 with required StartTLS | LDAP | Required | Read-only service account | `Connectors:Network:*` |
| CRMs (E7) | Worker, API | Dynamics 365 (`*.dynamics.com`), Salesforce (`*.salesforce.com` only), `api.hubapi.com`, `www.zohoapis.com`, `accounts.zoho.com` | HTTPS | Yes | Client credentials, private app token or refresh token | `Connectors:Network:*` |
| REST connectors (E7) | Worker (`connector_calls`), API (test) | The connector's base address | HTTPS | Required unless allowed | Key header, Basic, Bearer or OAuth 2.0 client credentials | `Connectors:Network:*` |
| CMDB discovery: Intune, Entra ID devices (G2) | API (`CmdbDiscoveryJobs`, test) | `graph.microsoft.com`, `login.microsoftonline.com` | HTTPS | Yes | The Entra ID connector's app secret | `Connectors:Network:*` |
| CMDB discovery: Azure (G2) | API | `management.azure.com` (`Connectors:ManagementUrl`), `login.microsoftonline.com` | HTTPS | Yes | The Entra ID connector's app secret | `Connectors:Network:*` |
| CMDB discovery: Active Directory computers (G2) | API | The LDAP connector's directory servers | LDAP | As the connector | The connector's read-only service account | `Connectors:Network:*` |
| CMDB discovery: VMware vCenter (G2) | API | The source's vCenter address | HTTPS | Required; an untrusted certificate only with its SHA-256 fingerprint | Read-only account and password (encrypted) | `Connectors:Network:*` |
| UAE PASS sign-in (D3) | Keycloak | `id.uaepass.ae`, `stg-id.uaepass.ae` | HTTPS | Yes | Client id and secret (stored only in Keycloak) | Keycloak |
| Entra sign-in (E7) | Keycloak | `login.microsoftonline.com` | HTTPS | Yes | Client secret, PKCE | Keycloak |
| LDAP sign-in federation (E7) | Keycloak | Directory servers | LDAPS or StartTLS | As configured | Bind account | Keycloak |

## Firewall summary for an installation

- **Open to users and providers:** 443 (and 80 for redirects and ACME) to the reverse proxy only. Keycloak and the API's integration route are published through the proxy in F6.
- **Internal only:** PostgreSQL, Valkey, RabbitMQ, OpenSearch, ClamAV, the collector, Prometheus, Tempo, Loki, Keycloak's back channel and the API and web app's own ports. Operator consoles only from the operator network.
- **Outbound:** the installation's mail server, DNS, the ACME CA (if used), ClamAV mirrors (if online), Vault (if used), and the provider addresses tenants configure from the last table. Offline installations allow only what their tenants use.

## What F6 changes

- Keycloak behind HTTPS (the proxy or its own TLS), `start --optimized`, production settings; plain ports no longer published.
- TLS with the installation's CA for PostgreSQL, Valkey, the Keycloak back channel, the web app to API calls and OTLP (or a service mesh on Kubernetes); separate Valkey users per service.
- An internal-only Docker network for the data and observability services; outbound access only for the API, worker, Keycloak, Caddy and ClamAV's updater; Kubernetes NetworkPolicies in Helm; an optional egress proxy setting.
- A public route for integrations and MCP clients to the API: done in task M2 (`/api/v1/*` through the proxy).
