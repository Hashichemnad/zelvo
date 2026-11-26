# Integrations (task E1, module 14)

API keys and OAuth clients with scopes, roles, revocation and per-key rate limits; outgoing signed webhooks with retries and a delivery log; bulk import and export in CSV and Excel; the integration log with personal data masked; the webhook tester; and the separate test installation. Code: `src/api/Zelvo.Integrations` (shared by the API and the worker), `src/api/Zelvo.Api/Integrations`, `src/web/src/components/settings/integrations`.

Product owner decisions (2026-11-22):

1. Integrations may act with **any role**, but never with `users.manage`, `roles.manage`, `identity.manage` or `integrations.manage`, whatever their role holds. A key or client secret is a strong credential of its own, so roles that need a second factor apply to integrations.
2. Webhooks go over **HTTPS only**. Loopback, link-local (which includes cloud metadata at 169.254.169.254), unspecified, multicast and reserved addresses are always refused. Private networks are refused unless the installation operator lists them (`Integrations:Webhooks:AllowedPrivateNetworks`), for receivers on the customer's internal network.
3. The **test installation** is a `Test` installation profile and license kind behind `ILicenseService`, a bilingual "Test installation" banner on every page, and `test` in every API response (`Zelvo-Environment` header) and webhook (`environment`). Copying configuration from the live installation is not part of E1.
4. E1 ships the **events and bulk types of users and roles**. Ticket, SLA and approval events plug into the same catalog with B1, B3 and C3; later modules register their own bulk types.

## Who calls: people and integrations

| Credential | Sent as | Checked by | Stored |
| --- | --- | --- | --- |
| Person | `Authorization: Bearer` (Keycloak token, via the web app) | `ZelvoBearerHandler` (A2) | — |
| API key | `X-Api-Key: zvk_<prefix>_<secret>` | `ApiKeyAuthenticationHandler` | Prefix (to find it) and SHA-256 of the whole key; the key has 256 random bits and is shown once |
| OAuth client | `Authorization: Bearer` (token from the client credentials grant) | `ZelvoBearerHandler` (same realm, audience and tenant checks), then `ApiClientDirectory` | A confidential Keycloak client `zelvo-int-<id>` in the tenant's realm; Zelvo keeps its client id and service account |

- The policy scheme `Zelvo` picks the API key handler when `X-Api-Key` is sent and the Keycloak handler otherwise.
- OAuth clients are created by the API through the `zelvo-provisioner` account (which now also has `view-clients` and `manage-clients` on tenant realms). Each client may only use the client credentials grant (no browser flows, no redirect URIs, `fullScopeAllowed` off), and its tokens carry the `zelvo-api` audience and two hard-coded claims: `zelvo_tenant_id` and `zelvo_api_client` (the Zelvo id). The API accepts such a token only when the Zelvo record exists, is active and not expired, and the token's `azp` and `sub` are that client's own. A token of any other client that claims to be an integration is refused (401 `credential-revoked`).
- **Revoking** marks the record revoked and turns the Keycloak client off; calls are refused at once, including with tokens issued earlier. **Rotating** replaces the API key (new prefix and hash) or asks Keycloak for a new client secret; the old one stops working at once.
- `CurrentUser.SetClient`: the integration's permissions are its role's permissions that are also in its scopes, minus the four above. Field rules of the role apply (hidden fields are left out of responses for integrations too). The audit actor is `ApiClient` with the integration's id and name. People-only endpoints (`/me`, `/me/preferences`) answer 403 `people-only`.
- Scopes are permission keys. When an integration is created or changed, every scope must be one an integration can be given and one its role holds; if the role later loses a permission, the integration loses it too.

## Rate limits per integration

`RateLimitingMiddleware` counts integrations per tenant and integration with the limit their administrator set (`rateLimitPerMinute`, 1–100,000, default 600), in one-minute windows in Valkey like the A5 limits, with the same `RateLimit-*` headers and 429 `rate-limited`.

## Integration log and audit

`IntegrationLogMiddleware` (after `CurrentUserMiddleware`) records every call made by an integration, after the response and in its own scope:

- `integration_log`: method, path with query values masked, status, time, request and response bodies (JSON, form or text up to 64 KB read, kept to 16 KB) with personal data masked, error, trace id.
- `audit_events`: `integrations.api.called` (security category, actor `ApiClient`, method, route pattern, status) — "every call is audit-logged".
- `api_clients.last_used_at`, at most every five minutes, outside the audit log.

Outgoing webhooks write an `Outgoing` entry per attempt. Entries and finished deliveries are kept for `Integrations:LogRetention` (30 days); the worker removes older ones every hour (`zelvo_purge_integration_records`).

**Masking** (`PersonalDataMasker`), for logs that people read: JSON properties whose name says personal data or secret (email, phone, mobile, Emirates ID, names, address, date of birth, IBAN, card, password, secret, token, API key, authorization…) are replaced whole; every other text is scanned for email addresses, Emirates ID numbers, phone numbers, IBANs, card numbers, bearer tokens and Zelvo keys. UUIDs, ISO dates and Unix timestamps stay readable. It errs on the side of hiding; module 9 (AI) can use the same masker.

## Webhooks

```
change ──SaveChanges──▶ WebhookEventInterceptor ──▶ webhook_deliveries (same transaction)
                                                        │ worker: WebhookDispatcher (lease, FOR UPDATE SKIP LOCKED)
                                                        ▼
                                  WebhookSender ──HTTPS, signed──▶ receiver
                                                        │
                           delivery log + integration_log + retries / give up / disable
```

- **Events.** Modules register an `IWebhookEventSource`; before each save the interceptor asks the sources which events the pending changes describe and queues one delivery per active subscription that chose the event, in the same transaction as the change. E1's source (`IdentityWebhookEvents`): `user.created`, `user.updated` (with the changed fields), `user.disabled`, `role.created`, `role.updated` (fields, `permissions`, `fieldRules`), `role.deleted`. B3 adds `ticket.sla.threshold` and `ticket.sla.breached` (`SlaWebhookEvents`, see `sla.md`). Payloads carry ids, status and the names of changed fields only, **no personal data**; receivers read the details through the API with their own key, which applies their field rules.
- **Body**: `{"id", "type", "occurredAt", "tenant", "environment", "data"}`.
- **Signature** (Standard Webhooks, standardwebhooks.com): headers `webhook-id` (`msg_` + event id; the same on redeliveries, so receivers can drop duplicates), `webhook-timestamp` (Unix seconds) and `webhook-signature` (`v1,` + base64 HMAC-SHA256 over `{id}.{timestamp}.{body}` with the secret's bytes). Also `Zelvo-Event` and `Zelvo-Environment`. Secrets are `whsec_` + 32 random bytes, stored encrypted with `Zelvo.Secrets` (purpose `webhooks.subscription:<id>.secret`) because they are needed to sign, and shown once. After a rotation each webhook carries signatures with the new and the previous secret for 24 hours.
- **Sending** (`WebhookSender`): connections only to addresses `OutboundAddressPolicy` allows, checked on the address actually connected to after DNS (so a name that resolves, or later re-resolves, to an internal address is refused too); no redirects followed; no system proxy; 10-second limit; at most 4 KB of the answer kept. A 2xx answer is success.
- **Retries**: after 1 and 5 minutes, then 30 minutes, 2, 5, 10 and 10 hours (8 attempts over about 28 hours, `Integrations:Webhooks:RetryDelays`). Then the delivery is failed. After 5 deliveries in a row fail for good (`DisableAfterFailures`), Zelvo disables the subscription (audited as the system) until an administrator turns it on again. Address-policy refusals fail at once.
- **Worker**: `WebhookDispatcher` finds tenants with due deliveries (`zelvo_webhook_due_tenants`, the only cross-tenant read), claims a batch per tenant with a two-minute lease, and sends up to 8 at once, each in its own scope with the tenant set, so row-level security applies. A delivery whose worker stopped is picked up when its lease ends.
- **Tester**: `POST /webhooks/{id}/test` sends a signed `webhook.test` event now (also for paused or disabled subscriptions) and answers with the receiver's status, time and the start of its answer, masked. **Redeliver** queues a copy of a delivery with the same event id.

## Bulk import and export

`/api/v1/bulk/{type}/export?format=csv|xlsx`, `/bulk/{type}/template`, `POST /bulk/{type}/import?dryRun=true|false` (multipart field `file`, at most 5 MB and 5,000 rows). Types implement `IBulkType`; E1 has `users` (export needs `users.read`, import `users.manage`).

- CSV is RFC 4180, UTF-8 with a byte order mark (Excel then shows Arabic correctly). Excel files are `.xlsx` read and written with the OpenXML SDK (`DocumentFormat.OpenXml`, MIT); every cell is written as text, and formulas in uploaded files are never evaluated.
- **CSV injection**: exported cells starting with `=`, `+`, `-`, `@`, tab or carriage return get a leading apostrophe; imports remove it again.
- **Export** leaves out the fields the caller may not see and is audited (`bulk.users.exported`, as a view of personal data when email or Emirates ID are included).
- **Import** checks every row first (email, Emirates ID format and uniqueness, known roles, status, duplicates, field rules, the last active tenant administrator, nobody disabling themselves) and changes nothing while any row has a problem: 422 with the problems by row, column and code (translated by the web app). New people are created in the tenant's Keycloak realm (no password; they sign in with an email code, UAE PASS or "Forgot password") before the database transaction; the Zelvo records, roles and Emirates IDs are saved in one transaction with the Emirates IDs copied to Keycloak, as for single changes. Audited per record and as `bulk.users.imported`.

## Test installation

`Installation:Profile=Test`: the development license reports `LicenseKind.Test` (a release test installation gets the test license file issued with the customer's license, `zelvo-sign issue --kind test`; see `licensing.md`). `ILicenseService.EnvironmentName()` gives `production`, `test`, `demo` or `development`, sent in `Zelvo-Environment` on every API response, in `GET /api/v1/system/info` and in every webhook. The web app shows a "Test installation" banner on every page. `make test-installation` starts it as its own Compose project (`zelvo-test`, `deploy/compose/compose.test.yaml`).

## API

| Endpoint | Permission |
| --- | --- |
| `GET /integrations/info` (environment, token URL, scopes, roles, events, retention) | `integrations.manage` |
| `GET/POST /integrations/clients`, `GET/PATCH /integrations/clients/{id}`, `POST …/revoke`, `POST …/rotate` | `integrations.manage` |
| `GET/POST /integrations/webhooks`, `GET/PATCH/DELETE /integrations/webhooks/{id}`, `POST …/rotate-secret`, `POST …/test`, `GET …/deliveries`, `POST …/deliveries/{id}/redeliver` | `integrations.manage` |
| `GET /integrations/log`, `GET /integrations/log/{id}` | `integrations.manage` |
| `GET /bulk`, `GET /bulk/{type}/template`, `GET /bulk/{type}/export`, `POST /bulk/{type}/import` | the type's export or import permission |

`integrations.manage` is given to tenant administrators by default and can never be given to an integration. Every change is audited.

The API explorer (`/api-explorer`) can be tried with an administrator's own test key: create an API key in Settings → Integrations and send it as `X-Api-Key`.

## Configuration (installation operator)

| Setting | Default | Meaning |
| --- | --- | --- |
| `Integrations:LogRetention` | 30 days | Integration log entries and finished webhook deliveries are removed after this |
| `Integrations:Webhooks:AllowedPrivateNetworks:<n>` | none | Internal networks webhooks may reach (for example `10.20.0.0/16`) |
| `Integrations:Webhooks:AllowInsecureHttp` | false | Allow `http://` receivers; development Compose only |
| `Integrations:Webhooks:Timeout` | 10 s | Time a receiver has to answer (at most 60 s) |
| `Integrations:Webhooks:RetryDelays:<n>` | 1 m, 5 m, 30 m, 2 h, 5 h, 10 h, 10 h | Waits before each retry |
| `Integrations:Webhooks:DisableAfterFailures` | 5 | Deliveries in a row that fail for good before the subscription is disabled |
| `Integrations:Webhooks:Concurrency` | 8 | Deliveries one worker sends at once |

Development Compose allows HTTP and the Compose network, and has a `webhook-receiver` (`http://webhook-receiver:8096/hook` as the receiver URL; `http://localhost:8096/received` lists what it got).

## Tests

- `IntegrationBuildingBlockTests`: masking, the outgoing-address policy (every blocked range, operator allowlist, names resolving to loopback), signatures (known value, rotation), CSV and Excel round trips with Arabic and formula neutralising, keys, the test license and `Zelvo-Environment`.
- `ApiClientApiTests` (real PostgreSQL): keys shown once and hashed, scopes and blocked permissions, people-only endpoints, wrong, expired, revoked and other tenants' keys, rotation, integration log with masking and audit entries, OAuth clients and forged or revoked tokens, per-integration rate limits, tenant isolation, retention.
- `WebhookApiTests` (real PostgreSQL and a local receiver): URL rules, the tester, events delivered by the worker with valid signatures and no personal data, retries, giving up, disabling and turning on again, redelivery, rotation with two signatures, paused subscriptions, deletes, refusing a saved URL that now points at this server.
- `BulkApiTests`: exports in CSV and Excel with field rules and audit, checks with problems by row, imports that create Keycloak users and update existing ones, guards, Excel imports, bad files.
- Web: component tests for every panel (English and Arabic, Arabic-Indic digits, confirmations, CSRF header).
- End-to-end (`tests/e2e/specs/integrations.spec.ts`): API key created and used, integration log, webhook tester to the development receiver with signature check, revocation, Arabic RTL, import check, axe on every screen.

## Not in E1

- **Incidents through integrations (B1 × E1):** integrations with incident scopes can read incidents (list, details, history, updates, attachments), but raising an incident, changing its state, declaring a major incident, posting updates and uploading or removing files record the person who acts, so they answer 403 `people-only` to integrations for now. Who an integration acts for (a service user, or the requester named in the call) is still to decide.
- Ticket, SLA breach and approval events (B3 and C3 add theirs; incident events are still to add to the catalog), bulk types of later modules, custom fields in the API (C1).
- Copying configuration from the live installation to the test installation.
- (Done in E7: connectors and the no-code connector builder; see `connectors.md`. Their outgoing calls are in the integration log.) Per-agent MCP credentials (E6) reuse API clients; AI agents may raise incidents through the MCP server for a named requester (`mcp.md`).
- An outgoing HTTP proxy for webhooks (installations that need one: follow-up).
