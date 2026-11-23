# API conventions

Every feature is available through this REST API before or alongside its UI; the web app uses the same API.

| Topic | Convention |
| --- | --- |
| Versioning | In the path: `/api/v1/...`. A breaking change goes to `/api/v2` while v1 stays available for a deprecation period. |
| Tenant | `X-Zelvo-Tenant: <slug>` header. Single-tenant installations set `Tenancy:DefaultTenant` and clients may omit it. Unknown tenant → 404 `tenant-not-found`; suspended → 403 `tenant-suspended`; missing where needed → 400 `tenant-required`. |
| JSON | camelCase properties; enums as strings (`"Create"`); times in ISO 8601 with offset (UTC); ids are UUIDs (version 7). |
| Bilingual values | `{"en": "...", "ar": "...", "missing": ["ar"]}` in responses, `{"en": "...", "ar": "..."}` in requests. |
| Errors | RFC 9457 problem details (`application/problem+json`) with `type` = `urn:zelvo:problem:<code>`, a stable `code` and a `traceId`. Titles are English and meant for developers; the UI shows a translated message for the `code`. |
| Validation | Server-side for every input (.NET validation with data annotations and `IValidatableObject`). Failures → 400 `validation-failed` with `errors: {"field": ["message"]}`. |
| Paging | `?page=1&pageSize=25` (page from 1, page size 1–100, default 25). Response: `{"items": [...], "page", "pageSize", "totalCount"}`. |
| Authentication and authorization | `Authorization: Bearer <access token>` from the tenant's Keycloak realm (people, and OAuth clients of integrations with the client credentials grant), or `X-Api-Key: zvk_…` for API keys (E1). Integrations act with their role narrowed by their scopes and never manage users, roles, sign-in or integrations; people-only endpoints answer 403 `people-only`; a revoked or expired integration gets 401 `credential-revoked`. Endpoints declare a permission (`RequirePermission("users.read")`) or open themselves to anonymous visitors (`AllowAnonymousAccess()`). No or invalid token → 401 `unauthenticated`; missing permission → 403 `forbidden`; permission that needs a second factor → 401 `mfa-required` with `WWW-Authenticate: Bearer error="insufficient_user_authentication", acr_values="mfa"`; disabled user → 403 `user-disabled`; a field the caller may not change → 403 `fields-read-only` with `fields`. See [identity-access.md](identity-access.md). |
| Rate limits | Per signed-in user (600 a minute) and per client address for visitors (120 a minute), shared by all API instances. Every `/api` response carries `RateLimit-Limit`, `RateLimit-Remaining` and `RateLimit-Reset`; over the limit → 429 `rate-limited` with `Retry-After`. API keys and OAuth clients have their own per-minute limit, set by the administrator (E1). See [security-baseline.md](security-baseline.md) and [integrations.md](integrations.md). |
| Environment | Every response carries `Zelvo-Environment`: `production`, `test` (the test installation every license includes), `demo` or `development` (E1). |
| Bulk | `GET /api/v1/bulk/{type}/export?format=csv\|xlsx`, `POST /api/v1/bulk/{type}/import?dryRun=true` (multipart `file`); see [integrations.md](integrations.md). |
| Audit | Changes to audited records are logged automatically; endpoints that show sensitive data or perform admin actions call `IAuditLog`. |
| Documentation | OpenAPI at `/openapi/v1.json`; interactive explorer at `/api-explorer` (bundled, works offline). On in Development; elsewhere only with `OpenApi:Enabled=true`. Calls can be tried with the admin's own test key: create an API key in Settings → Integrations and send it as `X-Api-Key` (E1). |

Endpoints live next to their feature (`src/api/Zelvo.Api/<Feature>/<Feature>Endpoints.cs`) and are mapped on the `/api/v1` group in `Program.cs`. Request and query types that are validated must be `public` so the validation source generator includes them.
