# Logging inventory (task F7)

What each part of Zelvo logs, where it goes, what it contains and never contains, how long it is kept, who can read it and how it is protected. Covers OWASP ASVS 5.0 V16.1.1 and gives the context for V16.2 (log content and protection) and V16.3 (security events). Related: `docs/architecture/observability.md` (telemetry pipeline), `docs/architecture/data-tenancy-audit.md` (audit log), `docs/security/data-protection-levels.md` (protection levels L1–L5 used below).

Checked against the code and `deploy/compose/` on 2026-11-26. A task that adds a log store or a security event updates this document.

## Where logs go

```
API, worker, updater (.NET)  ──OTLP gRPC──┐
web app server (traces only) ──OTLP HTTP──┼──▶ OpenTelemetry Collector ──▶ Loki (logs, 15 days)
Keycloak (traces)            ──OTLP gRPC──┘                            ├──▶ Tempo (traces, 7 days)
                                                                       └──▶ Prometheus (metrics, 15 days)
Every container ──stdout/stderr──▶ Docker's log driver (container logs)
Zelvo's own records ──▶ PostgreSQL tables (audit log, integration log, AI log, delivery logs …)
Keycloak's events ──▶ Keycloak's database (90 days) and its container log
```

Nothing is sent outside the installation: the collector has one `otlp` receiver and exporters to Prometheus, Tempo and Loki only (`deploy/compose/observability/otel-collector.yaml`); Grafana's update checks, news and plugin downloads are off.

## Rules for every log

- **Structured.** Every .NET log call is a source-generated `LoggerMessage` template with named values (95 in `src/api`, `src/worker` and `src/updater`); values travel as separate OTLP attributes, so a line break in a value cannot forge a record. Audit entries are jsonb written through EF parameters.
- **Never logged (L1, L2):** passwords, API keys, tokens, client secrets, session ids, cookies, private keys, one-time codes, SQL parameter values, request and response bodies of the API. `Microsoft.AspNetCore` and `System.Net.Http.HttpClient` log at Warning (`appsettings.json`), so request URLs (and any query-string token) are not logged; `url.query` is removed from spans (`Program.cs`, `src/web/src/lib/telemetry-redaction.ts`); the web app's spans also drop client addresses, referrers and user ids.
- **Personal data (L3):** not in log text or telemetry; tenants appear by slug, records by id. Where a Zelvo log must keep a payload for people to read (integration log, AI log, delivery logs) it is masked first (below).
- **Business data (L4):** ids only in telemetry and service logs.
- **Time:** UTC timestamps on every record; trace and span ids link logs, traces and audit entries (the audit log stores the correlation id).

## Log stores

| Store | Layer | What it records | Format and place | Contains / never contains | Kept | Who can read | Protection |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Service logs** | API, worker, updater | Warnings and errors, job outcomes, refused tokens (`ZelvoBearerHandler`), security events (below), start-up and licensing checks | OTLP log records with trace ids → Loki | Ids, slugs, route patterns, problem codes; never L1, L2 or L3 | 15 days (`loki.yaml` `retention_period: 360h`) | Operators through Grafana (sign-in required, no anonymous access or sign-up) | Loki and Grafana on the internal network; Grafana published on `127.0.0.1` only in Compose |
| **Traces** | API, worker, updater, web app server, Keycloak | Request and job spans, database query time, outgoing calls, AI calls (model, tokens, outcome, never text) | OTLP → Tempo | No bodies, query strings, tokens or SQL values; no user ids or client addresses from the web app | 7 days (`tempo.yaml` `block_retention: 168h`) | Operators through Grafana | As above |
| **Metrics** | All, plus Keycloak, RabbitMQ and the collector (scraped) | Counters and histograms: latency, errors, rate-limited calls (`zelvo_rate_limit_rejected_total`), refused calls (`zelvo.security.refused`), search, AI usage | Prometheus | Labels are status, route pattern, kind of caller; no personal data | 15 days (`--storage.tsdb.retention.time=15d`) | Operators through Grafana; alert rules in `observability/alerts.yaml` | As above |
| **Container logs** | Every container: .NET console logger (API, worker, updater, migrate), Keycloak (`jboss-logging`, including its event listener), Caddy (runtime log; no access log is configured), PostgreSQL, RabbitMQ, OpenSearch (security audit to log4j), ClamAV, Valkey, observability services | stdout and stderr | Docker's log driver on the host | Same rules for Zelvo's own lines; third-party lines follow each product (Keycloak logs user ids, not passwords) | Docker's default (no rotation set in development Compose); rotation set by the F6 install | Anyone with Docker access on the host | Host access control; F6 sets rotation and may drop the .NET console provider in production |
| **Audit log** (`audit_events`) | API, worker, migrate, database triggers | Every create, change and delete with field-level changes; administrative actions; views of sensitive data (`users.personal-data.viewed`, `forms.personal-data.viewed`, `channels.chat.viewed`, `privacy.consents.viewed`, `report.viewed` …); security events; AI calls (category `Ai`); actor (user, visitor, integration, AI agent, system), tenant, client address, correlation id | PostgreSQL, one SHA-256 hash chain per tenant, appended by a trigger (no updates or deletes) | `[SensitiveData]` values masked, `[AuditLengthOnly]` texts recorded by length; secrets never (changes audited without their value) | Indefinitely: the legal record (F5 keeps it on erasure, showing "Erased person") | `audit.read` (tenant administrators, executives); reading it is itself audited (`audit.events.viewed`) | Row-level security per tenant; append-only triggers; chain verification `GET /api/v1/audit/verification` |
| **Integration log** (`integration_log`) | API (`IntegrationLogMiddleware`), worker (webhooks, connector calls) | Every API key, OAuth client and MCP call; outgoing webhook and connector exchanges | PostgreSQL | Request and response bodies masked by `PersonalDataMasker` (personal data and secrets by name and by pattern) | 30 days (`Integrations:LogRetention`) | `integrations.manage` (Settings → Integrations) | Row-level security |
| **Webhook deliveries** (`webhook_deliveries`) | Worker | Each attempt: status, answer (first 4 KB), next retry | PostgreSQL | Payloads hold ids and changed field names only, no personal data | Finished deliveries 30 days (`Integrations:LogRetention`) | `integrations.manage` | Row-level security |
| **AI log** (`ai_calls`) | API, worker (AI gateway) | Every AI call or refused call: feature, provider, model, tokens, cost, outcome, masked prompt and answer | PostgreSQL | Text only after `PersonalDataPseudonymizer` (placeholders) and token removal; text cleared after the retention period | Text 30 days (`Ai:LogRetention`); the call row stays | `ai.manage` (Settings → AI → AI log) | Row-level security; also an audit entry per call |
| **MCP actions** (`mcp_actions`) | API | Each change an AI agent asked for, its approval or refusal | PostgreSQL | Arguments kept for the approver, audited by length only | With the record | Holders of the change's permission (AI approvals), `ai.manage` | Row-level security |
| **Email delivery log** (`notification_deliveries`) | Worker | Notification emails: event, state, attempts, error | PostgreSQL | Masked recipient addresses; bodies shown only to administrators in a sandboxed frame (viewing audited `notifications.delivery.viewed`) | 30 days (`Notifications:DeliveryRetention`) | `notifications.manage` | Row-level security |
| **Report deliveries** (`report_deliveries`) | Worker | Scheduled report emails per recipient and run | PostgreSQL | Recipient and outcome | 30 days | `reports.manage` | Row-level security |
| **Mailbox log** (`inbound_emails`) | Worker (`MailJob`) | Each email read: accepted, ignored (automatic mail, loops, `dmarc=fail`) or rejected senders, the ticket it went to | PostgreSQL | Masked senders | 90 days (`Channels:InboundLogRetention`) | `channels.manage` | Row-level security |
| **Sent emails** (`outbound_emails`) | Worker | Replies and notifications from a ticket's mailbox, retries | PostgreSQL | Message for "Send again" | 30 days (`Channels:OutboundRetention`) | `channels.manage` | Row-level security |
| **Channel log** (`channel_messages`) | API (hooks), worker (`MessagingJob`) | WhatsApp, SMS and Teams messages in and out, signature failures, delivery and read reports | PostgreSQL | Masked phone numbers | 30 days (`Channels:MessageRetention`) | `channels.manage` | Row-level security |
| **Connector call log** (`connector_calls`) | Worker, API | Queued REST connector calls from workflow rules, attempts and outcome; sync counts per connector | PostgreSQL | Masked bodies (exchanges also in the integration log) | 30 days (`Connectors:CallRetention`) | `connectors.manage` | Row-level security |
| **Update installs** (`update_installs`, `update_packages`) | API, updater | Imports, refusals with reasons, schedules, each install step, outcome, backup folder | PostgreSQL (installation-wide tables) | No secrets | With the installation | `installation.manage` | The application role cannot delete them |
| **Keycloak events** (per realm) | Keycloak | See "Security events" | Keycloak's database; also its container log through the `jboss-logging` listener | User id, IP address, client, identity provider, error; admin events without representations | 90 days (`eventsExpiration` 7,776,000 s) | Keycloak administrators (admin console), not tenant users | Keycloak's database; Keycloak admin access |

## Security events

Defined set of security events and where each is recorded. Events marked **F7** are added in this task.

| Event | Recorded in | Notes |
| --- | --- | --- |
| Sign-in, failed sign-in, sign-out, token exchange and refresh errors, password and second-factor changes, password resets, identity provider sign-ins (UAE PASS, Entra) and first-time links, lockouts, client sign-ins, invalid signatures (**F7**) | Keycloak realm events, 90 days; admin events without details | `KeycloakRealmProvisioner` sets `eventsEnabled`, `enabledEventTypes` (`LOGIN`, `LOGIN_ERROR`, `LOGOUT`, `LOGOUT_ERROR`, `CODE_TO_TOKEN_ERROR`, `REFRESH_TOKEN_ERROR`, `UPDATE_PASSWORD`, `RESET_PASSWORD`, `UPDATE_TOTP`, `REMOVE_TOTP`, `UPDATE_CREDENTIAL`, `REMOVE_CREDENTIAL`, `IDENTITY_PROVIDER_LOGIN`, `IDENTITY_PROVIDER_FIRST_LOGIN`, `USER_DISABLED_BY_TEMPORARY_LOCKOUT`, `CLIENT_LOGIN`, `INVALID_SIGNATURE_ERROR` and others), `adminEventsEnabled` on existing and new realms |
| Refused API calls: 403 from a known caller (**F7**) | Audit log `access.denied` (category Security) with method, route pattern and status; service log warning; counter `zelvo.security.refused` | `src/api/Zelvo.Api/Security/SecurityEventMiddleware.cs`; never the raw path or query string |
| Refused credentials: 401 when a token or API key was sent (**F7**) | Audit log `access.unauthenticated`; service log warning; counter | Same middleware; 401s from visitors without a credential are logged and counted only |
| Refused tokens | Service log (`ZelvoBearerHandler.LogRefused`) | Realm and reason, not the token |
| Integration calls, also refused ones | Audit log `integrations.api.called`, integration log | E1 |
| Rate-limit refusals (429) | Counter `zelvo_rate_limit_rejected_total`, alert; store errors `zelvo_rate_limit_store_errors_total` with the alert `ZelvoRateLimitCacheDown` | A5 |
| Secret, key and client changes: data key created and rewrapped, API key and webhook secret rotation, client revocation, provider and gateway credentials changed | Audit log (category Security or Admin), without values | A5, E1, E4, E8 |
| Malware found in an upload | Audit log (rejected, content deleted) | B1 |
| License and update package refusals | Audit log `license.import-refused`, package refusal entries | F3, F4 |
| Emails failing DMARC, rejected senders | Mailbox log | E2 |
| Provider hook signature failures | Channel log; 401 answers to `/api/v1/hooks` | E3 |
| Erasure, data subject requests, consent changes | Audit log (`privacy.*`) | F5 |
| AI agent actions and approvals | Audit log (actor `AiAgent`, approver in metadata), `mcp_actions` | E6 |
| Audit chain verification | Audit log (category Admin) | A1 |

Not yet recorded (open in the ASVS checklist): CSRF failures in the web proxy, wrong monitoring tokens and wrong anonymous tracking codes beyond the rate limit, and validation failures by problem code.

## Retention summary

| Kept | Stores |
| --- | --- |
| 7 days | Traces |
| 15 days | Service logs (Loki), metrics |
| 30 days | Integration log, finished webhook deliveries, AI log text, email delivery log, report deliveries, sent emails, channel log, connector calls |
| 90 days | Mailbox log, Keycloak events, in-app notifications |
| Indefinitely | Audit log, update installs |
| Docker default | Container logs (rotation with F6) |

All periods except the audit log's are operator settings (`*Retention`) with the defaults above; the Loki, Tempo and Prometheus periods are sized per install in F6.
