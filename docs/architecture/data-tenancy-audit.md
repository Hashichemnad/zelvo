# Data, tenant isolation and audit (task A1)

Covers the non-functional requirements **Tenant isolation** and **Audit**, and the bilingual value type of **LANG-5**. Code: `src/api/Zelvo.Data`.

## Database roles

| Role | Used by | Row-level security |
| --- | --- | --- |
| Owner (`POSTGRES_SUPERUSER` in Compose) | `migrate` (migrations, tenant provisioning) | Bypassed (superuser) |
| Application (`ZELVO_APP_DB_USER`) | API and worker | **Always applies**: `NOSUPERUSER NOBYPASSRLS`, not the table owner |

The application role gets `SELECT, INSERT, UPDATE, DELETE` on tables through default privileges set when the database is created (`deploy/compose/postgres/init`). Tests set up the same roles (`DatabaseFixture`).

## Migrations

- EF Core migrations in `src/api/Zelvo.Data/Migrations`; names are snake_case (`EFCore.NamingConventions`).
- Create one: `dotnet tool restore`, then `dotnet ef migrations add <Name> -p src/api/Zelvo.Data -s src/api/Zelvo.Data -o Migrations`.
- Apply: `dotnet Zelvo.Api.dll migrate` with `ConnectionStrings__PostgresOwner` set. In Compose the one-off `migrate` service does this before the API starts, and creates the tenant named by `ZELVO_DEFAULT_TENANT` if it does not exist.
- Hand-written SQL (functions, policies, triggers) goes inline in the migration's `Up`/`Down`, never in shared constants, so old migrations never change.

## Tenant isolation

1. Each request (or job) has a scoped `ITenantContext`. The API sets it in `TenantResolutionMiddleware` from the `X-Zelvo-Tenant` header (a slug) or `Tenancy:DefaultTenant`.
2. `TenantConnectionInterceptor` runs `set_config('zelvo.tenant_id', <id>, false)` every time EF Core opens a connection, or clears it when no tenant is set. Npgsql resets session state when a connection returns to the pool, and the setting is rewritten on every open, so it cannot leak between requests.
3. Every tenant-owned table has a `tenant_id` column and, in its migration, `migrationBuilder.EnableTenantIsolation("<table>")`: row-level security **enabled and forced**, with a policy `tenant_id = zelvo_current_tenant_id()` for reads and writes.
4. `zelvo_current_tenant_id()` returns NULL when the setting is empty, so a connection without a tenant sees no rows and cannot write any (fail closed).
5. `tenants` itself: the application can read and update only its own row, and cannot create or delete tenants. Slugs are resolved with `zelvo_resolve_tenant(slug)`, a `SECURITY DEFINER` function, because no tenant is set yet at that point.
6. Entities implementing `ITenantOwned` get their `TenantId` filled in on insert; writing a row of another tenant throws before reaching the database (which would refuse it too).

**Guard test:** `Every_tenant_owned_table_has_forced_row_level_security_and_a_policy` fails the build when a table with a `tenant_id` column lacks forced RLS or a policy. New modules cannot forget it.

Signed-in requests (task A2) must carry an access token issued by the request tenant's own Keycloak realm, with a `zelvo_tenant_id` claim equal to the tenant (`ZelvoBearerHandler`), so a user of one tenant cannot pick another with the header. See [identity-access.md](identity-access.md).

## Audit log

Table `audit_events`, one hash chain per tenant.

| What | How |
| --- | --- |
| Create, change, delete | Automatic for entities implementing `IAuditedEntity`: `AuditSaveChangesInterceptor` adds one entry per record in the **same transaction**, with field-level `{"field": {"old", "new"}}` changes. Bilingual fields appear as `name.en` / `name.ar`. |
| Sensitive values | Properties marked `[SensitiveData]` are recorded as changed with values `[masked]`. |
| Admin actions, views of sensitive data, security events | `IAuditLog.Add` (saved with the next `SaveChanges`) or `IAuditLog.RecordAsync` (saved now). Reading the audit log is itself recorded (`audit.events.viewed`). |
| Actor and context | `IAuditContext`: actor type (system, user, anonymous, API client, AI agent), id and name; correlation id, client address and user agent. |

**Tamper evidence**, enforced in PostgreSQL so every writer is covered:

- A `BEFORE INSERT` trigger takes a per-tenant advisory lock, assigns the next `sequence`, sets `occurred_at` (clients cannot backdate), stores the previous entry's hash and computes `hash = SHA-256(canonical JSON of the entry + previous hash)` with `zelvo_audit_hash`.
- Triggers refuse `UPDATE`, `DELETE` and `TRUNCATE` for every role, including the owner. The application role also has no update or delete policy.
- `zelvo_verify_audit_chain(tenant)` recomputes the chain and returns the first entry that was changed, removed or reordered, plus the newest entry's sequence and hash. API: `GET /api/v1/audit/verification`.
- Limits: someone with superuser access can disable the triggers and rewrite the whole chain from some point on, or remove the newest entries. Recording the head hash outside Zelvo (for example in a scheduled report or the customer's SIEM) detects both; that export is planned with reporting (F2) and the observability baseline (A5).

Changing `zelvo_audit_hash` invalidates existing chains. A new format needs a new version number (the first array element) and verification that understands both.

## Bilingual values (LANG-5)

`LocalizedText` holds English and Arabic; either may be missing while translating. It maps to two columns, `<field>_en` and `<field>_ar` (`ConfigureLocalizedText`). The API shows it as `{"en": …, "ar": …, "missing": ["ar"]}` and accepts `{"en": …, "ar": …}` with at least one language.

Missing translations: every module with bilingual fields registers an `IMissingTranslationSource`; `GET /api/v1/translations/missing` (tenant administrators) lists all of them. A3 and the Settings screens show the warning to admins.
