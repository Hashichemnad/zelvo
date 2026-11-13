# Identity and access (task A2)

Covers the non-functional requirements **Authentication** (SSO, mandatory MFA for agents and admins) and **Authorization** (role-based down to field level, least privilege), module 4's logins (single sign-on, email + one-time code, anonymous for public forms) and the six personas. UAE PASS is task D3; API keys and OAuth clients for integrations are task E1.

## Pieces

| Piece | Where | Does |
| --- | --- | --- |
| Keycloak, one realm per tenant | `zelvo-<tenant slug>`, created by `migrate` | Who the person is: passwords, email one-time codes, authenticator apps, brute-force protection, sessions, English and Arabic sign-in pages |
| Zelvo Keycloak extension | `src/keycloak` (Java, built into `zelvo/keycloak`) | Sign-in with a one-time code sent by email; the `zelvo` login and email theme |
| API | `src/api/Zelvo.Api/Identity` | Accepts only tokens of the request tenant's realm; Zelvo user records, roles, permissions, field rules |
| Web app | `src/web/src/lib/auth`, `src/web/src/app/auth` | Server-side session (backend-for-frontend); the browser never holds a token |

## Realms (product owner decision 2026-11-13: one realm per tenant)

`migrate` (`KeycloakRealmProvisioner`) creates or updates each tenant's realm, so it is repeatable on every start:

- Security: brute-force protection (5 failures, growing wait), password policy (12+ characters, not the username or email, not the last 3), `sslRequired=external`, registration off, access tokens 5 minutes, SSO idle 30 minutes and maximum 12 hours, refresh tokens rotated and single-use.
- English and Arabic, login and email theme `zelvo`, the installation's SMTP server.
- Clients: `zelvo-web` (confidential, authorization code + PKCE S256, exact redirect URI, adds the `zelvo-api` audience and a hard-coded `zelvo_tenant_id` claim) and `zelvo-api` (audience only).
- Browser flow `zelvo browser`:
  - existing session cookie, **or**
  - level 1: password **or** email one-time code ("Try Another Way"), then
  - level 2, only when the client asks for `acr_values=mfa`: a code from an authenticator app (TOTP). Staff without one are asked to set it up.
  - `acr` in tokens: `pwd` after level 1, `mfa` after level 2 (`acr.loa.map`).
- The master-realm service account `zelvo-provisioner` gets `view/manage-realm` and `view/manage-users` on this realm only. The API uses it at run time to apply sign-in settings; `migrate` uses the bootstrap administrator once to set it up.

## Email one-time code (product owner decision 2026-11-13: Keycloak extension)

Keycloak 26.7.5 has no built-in authenticator that emails a sign-in code (checked against the running server before building), so `src/keycloak` adds one, limited to that step. Its rules:

- Step 1 asks for the address; step 2 asks for the 6-digit code. Step 2 looks the same whether or not a code was sent, so the page does not reveal who has an account.
- Codes are random (`SecureRandom`), stored only as a salted SHA-256 hash in the Keycloak authentication session (server side), valid for 10 minutes, at most 5 tries, compared in constant time; a new code at most once a minute.
- **Who may sign in** (tenant setting, `PUT /api/v1/identity/settings`):
  - `KnownUsers` (default): only existing accounts.
  - `AllowedDomains`: also anyone with a listed domain (exact match, for example `ajman.ae`); their account is created **after** a correct code, as a requester.
  - `Anyone`: any address (public portals).
- Entering a correct code also marks the address as verified. It is one factor: staff still need the second.
- Tests: `mvn verify` (policy, codes, and the sign-in steps against mocked Keycloak services), plus the end-to-end tests below.

## The API

1. `TenantResolutionMiddleware` sets the tenant (header or installation default).
2. `ZelvoBearerHandler` checks the bearer token: issuer must be **this tenant's** realm (`Identity:PublicUrl/realms/zelvo-<slug>`), audience `zelvo-api`, signature with that realm's keys (fetched over `Identity:InternalUrl`, cached), lifetime, and `zelvo_tenant_id` equal to the tenant. A token of another tenant is invalid whatever header is sent.
3. `CurrentUserMiddleware` finds the user's Zelvo record by `sub`, creating it at the first sign-in **as a requester** (least privilege), copies the email from Keycloak, refuses disabled users (403 `user-disabled`) and works out the permissions that apply.
4. Endpoints declare `RequirePermission("<key>")`. When the user holds the permission only through a role that needs a second factor and the token's `acr` is not `mfa`, the API answers 401 `mfa-required` with `WWW-Authenticate: Bearer error="insufficient_user_authentication", acr_values="mfa"` (RFC 9470). The web app then signs in again asking for `acr_values=mfa`.

`GET /api/v1/me` returns the user, their roles, the permissions that apply now, those that would apply after a second factor (`pendingPermissions`), and `mfa: { satisfied, required }`.

### Roles and permissions: data, not code

| Persona | Role key | Second factor | Default permissions |
| --- | --- | --- | --- |
| End user / customer | `requester` | no | none (self-service only) |
| Agent | `agent` | **always** | `users.read` |
| Team lead / supervisor | `team-lead` | **always** | `users.read` |
| Approver | `approver` | no (admin may require it) | none |
| Tenant administrator | `tenant-admin` | **always** | all |
| Executive / auditor | `executive-auditor` | no (admin may require it) | `audit.read`, `users.read` |

- The catalog of what can be granted is code (`PermissionCatalog`): permission keys and resources with fields. Each module adds its own.
- What each role holds is data (`roles`, `role_permissions`, `role_field_rules`), changed by tenant administrators through `/api/v1/roles` (the Settings screens come with task B2) and audited field by field.
- Defaults are given once per tenant (`role_default_grants` remembers them), so a later release adds new defaults without undoing an administrator's change.
- Guards: tenant administrators always keep `users.manage` and `roles.manage` (409 `role-locked`); agents, team leads and tenant administrators always need a second factor (409 `role-mfa-locked`); a tenant always keeps one active tenant administrator (409 `last-tenant-admin`); nobody disables themselves.

Permissions in A2: `tenant.manage`, `translations.read`, `audit.read`, `users.read`, `users.manage`, `roles.manage`, `identity.manage`.

### Field-level access

For each field of a resource in the catalog, a role's access is its explicit rule (`Hidden`, `Read`, `Edit`) or, without one, what its permissions give: read with the resource's read permission, edit with its edit permission. **Sensitive fields (personal data) are hidden unless a rule grants access.** The user gets the highest access of the roles that apply now. Fields the API never changes are at most `Read`.

- Responses leave hidden fields out (`FieldFilter.Apply`).
- Changes to fields without `Edit` are refused: 403 `fields-read-only` listing the fields.
- Showing personal data is audited (`users.personal-data.viewed`, category `View`).

First resource: `users` (`displayName`, `email` (sensitive), `status`, `roles`, `lastSeenAt`).

### Anonymous access

`AllowAnonymousAccess()` opens an endpoint to visitors without sign-in for public forms (complaints, inquiries; modules 2 and 3). The request still needs a tenant, and audit entries record the actor as anonymous. `GET /api/v1/tenant` uses it.

## The web app (backend-for-frontend)

- `/auth/login`: authorization code flow with PKCE, `state` and `nonce`, `ui_locales` = the page's language, `acr_values=mfa` when a second factor is needed. The pending sign-in is stored on the server for 10 minutes and used once; a short-lived cookie binds it to the browser.
- `/auth/callback`: checks state, PKCE and nonce, exchanges the code, asks the API for `/me`, and if the user's roles need a second factor that was not used, goes straight back to Keycloak for it. Then it creates the session.
- Sessions live in Valkey (database 1), keyed by a SHA-256 hash of a random 256-bit id. The browser gets only that id in an http-only, `SameSite=Lax`, `__Host-` cookie. Idle time-out 30 minutes and absolute 12 hours (`SESSION_IDLE_TIMEOUT_SECONDS`, `SESSION_ABSOLUTE_TIMEOUT_SECONDS`). Access tokens are refreshed on the server.
- `/auth/logout`: POST only, same origin, with the session's CSRF token. It ends the Zelvo session and then the Keycloak session.
- `/bff/api/v1/...`: the browser's way to the public API. It adds the session's access token. Changing requests need the same origin and the `X-CSRF-Token` header.
- `/[locale]/account` shows the user, roles and second-factor status. `/[locale]/sign-in-problem` explains failed sign-ins. Both are in English and Arabic.

## Configuration

| Setting | Used by | Meaning |
| --- | --- | --- |
| `Identity__PublicUrl`, `KEYCLOAK_PUBLIC_URL` | API, migrate, web | Keycloak as browsers see it (token issuer) |
| `Identity__InternalUrl`, `KEYCLOAK_INTERNAL_URL` | API, migrate, web | Keycloak from the servers |
| `KEYCLOAK_PROVISIONER_SECRET` | API, migrate | `zelvo-provisioner` service account |
| `ZELVO_WEB_CLIENT_SECRET` | migrate, web | `zelvo-web` client in every realm |
| `Identity__Provisioning__Smtp__*` | migrate | Mail server for sign-in emails (Mailpit in development) |
| `ZELVO_DEV_USERS_PASSWORD` | migrate | **Development only:** one user per persona (`requester@zelvo.test`, `agent@`, `lead@`, `approver@`, `admin@`, `executive@`) |
| `SESSION_STORE_URL` | web | Valkey for sessions (required in production) |

## Tests

- API (`src/api/tests`): tokens signed with a test key go through the real token checks (wrong realm, audience, key, tenant claim, expiry); first sign-in; step-up; field rules; role guards; sign-in settings and their Keycloak sync (faked); row-level security on every new table.
- Keycloak extension: `mvn verify`.
- Web: component tests for the account menu, and tests for redirect, CSRF and session-key helpers.
- End-to-end (`tests/e2e/specs/identity.spec.ts`, against `make up`): password sign-in and sign-out; email code in Arabic; unknown address; agent setting up an authenticator app; CSRF on the API proxy; forged callback; axe checks on the new pages.

## Not in A2

- Passkeys (WebAuthn) as a second factor. Keycloak needs a different flow shape so that people without any second factor are asked to set one up. Follow-up.
- UAE PASS (D3), Entra ID / LDAP (E7), API keys and OAuth clients (E1), Settings screens for users and roles (B2), demo user switching (D), rate limiting (A5), tenant branding of the Keycloak pages (F1).
