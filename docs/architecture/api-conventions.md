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
| Authorization | Endpoints declare a policy (`tenant-admin`, `audit-read`, ...). Until task A2 adds sign-in, protected endpoints answer 401. |
| Audit | Changes to audited records are logged automatically; endpoints that show sensitive data or perform admin actions call `IAuditLog`. |
| Documentation | OpenAPI at `/openapi/v1.json`; interactive explorer at `/api-explorer` (bundled, works offline). On in Development; elsewhere only with `OpenApi:Enabled=true`. Module 14 (E1) adds trying calls with the admin's own test key. |

Endpoints live next to their feature (`src/api/Zelvo.Api/<Feature>/<Feature>Endpoints.cs`) and are mapped on the `/api/v1` group in `Program.cs`. Request and query types that are validated must be `public` so the validation source generator includes them.
