# OWASP ASVS 5.0 Level 2 checklist

Zelvo targets OWASP ASVS **Level 2** for every tenant (non-functional requirement "Security standard"). This checklist was started in task A5 and is completed in task F7 (ASVS L2 review before the external penetration test). Every task that touches a listed area updates its rows.

- Standard: [OWASP ASVS 5.0.0](https://github.com/OWASP/ASVS/tree/v5.0.0/5.0) (May 2025), levels 1 and 2: 253 requirements. Requirement texts are in the standard (CC BY-SA 4.0) and are not copied here; look them up by ID.
- Status: **Met** (with evidence), **Partly met**, **Open** (with the task expected to close it), **Not applicable**, **Not assessed** (not reviewed yet).
- Evidence points to code, tests, architecture notes and build plan tasks (A1-F7 in `docs/phase1-build-plan.md`).

**Summary (2026-11-23):** Met: 96 · Partly met: 16 · Open: 8 · Not applicable: 14 · Not assessed: 119.

Main gaps found so far: web app without Content-Security-Policy and HSTS (V3.4); TLS still to add for PostgreSQL, Valkey and internal HTTP (V12.3, F6); service passwords as environment variables rather than a secrets manager (V13.3.1, F6); password policy, breached-password checks and session management settings in Keycloak not yet reviewed (V6.2, V7). File handling (V5) was added with attachments in B1.

## V1 Encoding and Sanitization

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V1.1.1 | 2 | V1.1 Encoding and Sanitization Architecture | Not assessed |  |
| V1.1.2 | 2 | V1.1 Encoding and Sanitization Architecture | Not assessed |  |
| V1.2.1 | 1 | V1.2 Injection Prevention | Not assessed |  |
| V1.2.2 | 1 | V1.2 Injection Prevention | Not assessed |  |
| V1.2.3 | 1 | V1.2 Injection Prevention | Not assessed |  |
| V1.2.4 | 1 | V1.2 Injection Prevention | Met | EF Core and parameterised SQL only (`ExecuteSqlInterpolated` binds parameters); no SQL built from input. The one statement with a variable identifier, "Reset demo"'s `DELETE` (`DemoReset.DeleteStatement`), takes table names from the EF model only, checks them against `^[a-z_][a-z0-9_]*$` and binds the tenant id. A1, D. |
| V1.2.5 | 1 | V1.2 Injection Prevention | Not applicable | No operating system commands are run with user input. |
| V1.2.6 | 2 | V1.2 Injection Prevention | Open | LDAP: none yet; AD/LDAP connector in E7. |
| V1.2.7 | 2 | V1.2 Injection Prevention | Not applicable | No XPath or LaTeX processing. |
| V1.2.8 | 2 | V1.2 Injection Prevention | Not applicable | No XPath or LaTeX processing. |
| V1.2.9 | 2 | V1.2 Injection Prevention | Not assessed |  |
| V1.3.1 | 1 | V1.3 Sanitization | Not assessed |  |
| V1.3.2 | 1 | V1.3 Sanitization | Met | No `eval` or dynamic code execution in the API or web app. |
| V1.3.3 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.4 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.5 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.6 | 2 | V1.3 Sanitization | Met | E1: outgoing webhooks only to `https://` receivers on allowed addresses (`OutboundAddressPolicy`): loopback, link-local and cloud metadata, unspecified, multicast and reserved ranges always refused; private networks only when the operator lists them; checked on the address actually connected to after DNS; no redirects followed; no system proxy. Tests in `IntegrationBuildingBlockTests`, `WebhookApiTests`. Connectors (E7) will use the same policy. |
| V1.3.7 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.8 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.9 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.10 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.3.11 | 2 | V1.3 Sanitization | Not assessed |  |
| V1.4.1 | 2 | V1.4 Memory, String, and Unmanaged Code | Met | Managed languages (C#, TypeScript, Java). |
| V1.4.2 | 2 | V1.4 Memory, String, and Unmanaged Code | Not assessed |  |
| V1.4.3 | 2 | V1.4 Memory, String, and Unmanaged Code | Met | Managed languages (C#, TypeScript, Java). |
| V1.5.1 | 1 | V1.5 Safe Deserialization | Not applicable | No XML parsing yet (SAML is handled by Keycloak). |
| V1.5.2 | 2 | V1.5 Safe Deserialization | Met | System.Text.Json with typed records; no polymorphic or type-name deserialisation. |

## V2 Validation and Business Logic

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V2.1.1 | 1 | V2.1 Validation and Business Logic Documentation | Not assessed |  |
| V2.1.2 | 2 | V2.1 Validation and Business Logic Documentation | Not assessed |  |
| V2.1.3 | 2 | V2.1 Validation and Business Logic Documentation | Not assessed |  |
| V2.2.1 | 1 | V2.2 Input Validation | Not assessed |  |
| V2.2.2 | 1 | V2.2 Input Validation | Met | All validation in the API (`AddValidation`, endpoint checks); the web app only adds usability checks. |
| V2.2.3 | 2 | V2.2 Input Validation | Not assessed |  |
| V2.3.1 | 1 | V2.3 Business Logic Security | Not assessed |  |
| V2.3.2 | 2 | V2.3 Business Logic Security | Not assessed |  |
| V2.3.3 | 2 | V2.3 Business Logic Security | Met | Changes and their audit entries and search outbox entries are saved in one transaction. A1, A4. |
| V2.3.4 | 2 | V2.3 Business Logic Security | Not assessed |  |
| V2.4.1 | 2 | V2.4 Anti-automation | Met | A5: API rate limits per signed-in user and per client address (Valkey, shared by instances); Keycloak brute-force protection for sign-in. E1: a limit per API key and OAuth client, set by the administrator (`ApiClientApiTests`). |

## V3 Web Frontend Security

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V3.2.1 | 1 | V3.2 Unintended Content Interpretation | Not assessed |  |
| V3.2.2 | 1 | V3.2 Unintended Content Interpretation | Not assessed |  |
| V3.3.1 | 1 | V3.3 Cookie Setup | Met | `__Host-` session and sign-in cookies, Secure outside local development (`src/web/src/lib/auth/session.ts`). A2. |
| V3.3.2 | 2 | V3.3 Cookie Setup | Met | Session cookies `SameSite=Lax` (needed for the sign-in redirect); changes also need the CSRF token. A2. |
| V3.3.3 | 2 | V3.3 Cookie Setup | Met | `__Host-` prefix on the session and sign-in cookies. A2. |
| V3.3.4 | 2 | V3.3 Cookie Setup | Met | Session cookie is HttpOnly; tokens stay on the server (backend for frontend). The demo organisation cookie (`zelvo-tenant`) is HttpOnly too and only selects tenants listed in `ZELVO_DEMO_TENANTS`. A2, D. |
| V3.4.1 | 1 | V3.4 Browser Security Mechanism Headers | Open | No HSTS yet: TLS is terminated by the proxy of the hardened install (F6), which adds it; the web app should also send it. |
| V3.4.2 | 1 | V3.4 Browser Security Mechanism Headers | Not assessed |  |
| V3.4.3 | 2 | V3.4 Browser Security Mechanism Headers | Partly met | The API sends a restrictive CSP (`SecurityHeadersMiddleware`); the web app has none yet (follow-up with F1 branding, which sets allowed sources). |
| V3.4.4 | 2 | V3.4 Browser Security Mechanism Headers | Met | `X-Content-Type-Options: nosniff` on API and web responses. |
| V3.4.5 | 2 | V3.4 Browser Security Mechanism Headers | Met | `Referrer-Policy: no-referrer` (API) and `strict-origin-when-cross-origin` (web). |
| V3.4.6 | 2 | V3.4 Browser Security Mechanism Headers | Partly met | API: `frame-ancestors 'none'`; web: `X-Frame-Options: DENY` but no CSP yet (see V3.4.3). |
| V3.5.1 | 1 | V3.5 Browser Origin Separation | Met | Changes through the web proxy need same origin and the session's CSRF token (e2e test). The demo switch (`/auth/demo`, demo installations only) needs same origin and, when signed in, the CSRF token. A2, D. |
| V3.5.2 | 1 | V3.5 Browser Origin Separation | Not assessed |  |
| V3.5.3 | 1 | V3.5 Browser Origin Separation | Met | Changes use PUT/POST/DELETE; GET has no side effects. |
| V3.5.4 | 2 | V3.5 Browser Origin Separation | Not assessed |  |
| V3.5.5 | 2 | V3.5 Browser Origin Separation | Not assessed |  |
| V3.7.1 | 2 | V3.7 Other Browser Security Considerations | Not assessed |  |
| V3.7.2 | 2 | V3.7 Other Browser Security Considerations | Not assessed |  |

## V4 API and Web Service

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V4.1.1 | 1 | V4.1 Generic Web Service Security | Met | JSON and problem details with explicit content types. |
| V4.1.2 | 2 | V4.1 Generic Web Service Security | Not assessed |  |
| V4.1.3 | 2 | V4.1 Generic Web Service Security | Met | A5: `X-Forwarded-For`/`-Proto` used only from `Network:TrustedProxies`; the web app sends the browser's address it determined itself (`TRUSTED_PROXY_HOPS`). Tests: `RateLimitingTests`. |
| V4.2.1 | 2 | V4.2 HTTP Message Structure Validation | Not assessed |  |
| V4.3.1 | 2 | V4.3 GraphQL | Not applicable | No GraphQL. |
| V4.3.2 | 2 | V4.3 GraphQL | Not applicable | No GraphQL. |
| V4.4.1 | 1 | V4.4 WebSocket | Open | WebSockets (SignalR) arrive with the agent workspace in B4. |
| V4.4.2 | 2 | V4.4 WebSocket | Open | WebSockets (SignalR) arrive with the agent workspace in B4. |
| V4.4.3 | 2 | V4.4 WebSocket | Open | WebSockets (SignalR) arrive with the agent workspace in B4. |
| V4.4.4 | 2 | V4.4 WebSocket | Open | WebSockets (SignalR) arrive with the agent workspace in B4. |

## V5 File Handling

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V5.1.1 | 2 | V5.1 File Handling Documentation | Met | Permitted file types (an allowlist of types Zelvo can check, chosen per tenant), maximum size (tenant setting, 25 MB default, 100 MB installation maximum) and the handling of malicious files (scanned by ClamAV before download; infected files rejected, content deleted, audited) are documented in `docs/architecture/tickets.md` (B1). Zelvo never unpacks archives; ClamAV scans inside them within `MaxScanSize`. |
| V5.2.1 | 1 | V5.2 File Upload and Content | Met | Tenant size limit checked per file and an endpoint request size limit of 101 MB (`IncidentAttachmentEndpoints`); clamd `StreamMaxLength` matches. Tests: `IncidentAttachmentTests.The_tenant_sets_the_largest_file_and_the_accepted_types`. E1: bulk imports accept at most 5 MB and 5,000 rows (request size limit on the endpoint). |
| V5.2.2 | 1 | V5.2 File Upload and Content | Met | Extension must be on the tenant's allowlist and the content must match the type (magic bytes; text must be UTF-8 without NUL) (`FileTypes`); the stored media type comes from the type, never the uploader. Tests: `Files_of_other_types_or_with_content_that_does_not_match_are_refused`, `TicketWorkflowTests.Content_must_match_the_file_type`. E1: bulk imports accept `.csv` and `.xlsx` only, and the content must parse as that format (`TabularFile`), otherwise 400. |
| V5.2.3 | 2 | V5.2 File Upload and Content | Partly met | Attachments: Zelvo never decompresses uploaded archives; ClamAV scans inside them with its own limits (`MaxScanSize` 400 MB, default file and recursion limits). E1: bulk import does open `.xlsx` (zip) files, read with a limit on the characters per part (`MaxCharactersInPart`) and from a 5 MB upload; a limit on the number of files in the archive is still to add. |
| V5.3.1 | 1 | V5.3 File Storage | Met | Attachments are stored in PostgreSQL (`ticket_attachment_contents`, row-level security), never in a folder the web server serves or executes (B1, decision 2026-11-22). |
| V5.3.2 | 1 | V5.3 File Storage | Met | No file system paths are built from user input; file names are cleaned of folders, control and bidirectional-override characters (`FileTypes.CleanFileName`, tested). |
| V5.4.1 | 2 | V5.4 File Download | Met | Downloads use the stored, cleaned file name in `Content-Disposition: attachment` with RFC 6266 `filename*` encoding (ASP.NET Core `TypedResults.File`), plus `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`. |
| V5.4.2 | 2 | V5.4 File Download | Met | As V5.4.1: names are cleaned on upload and encoded per RFC 6266 on download. |
| V5.4.3 | 2 | V5.4 File Download | Met | Every upload is scanned by ClamAV (on by default) before it can be downloaded; while the scanner is unavailable files stay unavailable (`AttachmentScanJob`). Tests: `IncidentAttachmentTests`, `RealClamAvTests` (against a real ClamAV), e2e `incidents.spec.ts` (EICAR rejected). |

## V6 Authentication

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V6.1.1 | 1 | V6.1 Authentication Documentation | Not assessed |  |
| V6.1.2 | 2 | V6.1 Authentication Documentation | Not assessed |  |
| V6.1.3 | 2 | V6.1 Authentication Documentation | Met | Sign-in pathways (password, email code, UAE PASS; second factor) documented in `docs/architecture/identity-access.md` and `uae-pass.md`; the demo-only one-click sign-in in `demo-mode.md`. A2, D3, D. |
| V6.2.1 | 1 | V6.2 Password Security | Met | Keycloak password policy on every tenant realm: at least 12 characters, not the username or email, not the last 3. A2. |
| V6.2.2 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.3 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.4 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.5 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.6 | 1 | V6.2 Password Security | Met | Keycloak sign-in page (zelvo theme): password field masked, paste and password managers allowed. |
| V6.2.7 | 1 | V6.2 Password Security | Met | Keycloak sign-in page (zelvo theme): password field masked, paste and password managers allowed. |
| V6.2.8 | 1 | V6.2 Password Security | Not assessed |  |
| V6.2.9 | 2 | V6.2 Password Security | Not assessed |  |
| V6.2.10 | 2 | V6.2 Password Security | Not assessed |  |
| V6.2.11 | 2 | V6.2 Password Security | Not assessed |  |
| V6.2.12 | 2 | V6.2 Password Security | Not assessed |  |
| V6.3.1 | 1 | V6.3 General Authentication Security | Met | Keycloak brute-force protection on every tenant realm; A5 API rate limits. A2. |
| V6.3.2 | 1 | V6.3 General Authentication Security | Partly met | No default accounts in Zelvo or tenant realms; Keycloak's bootstrap administrator in the master realm is temporary and must be replaced at install (F6). |
| V6.3.3 | 2 | V6.3 General Authentication Security | Met | Second factor (authenticator app, or UAE PASS SOP2+ when the tenant allows) mandatory for staff roles, checked by the API from `acr`. Exception by design: the demo sign-in step (`zelvo-demo-sign-in`) signs listed demo users in at `acr=mfa` without credentials; `migrate` adds it only to the realms of a demo installation (demo license, own database, sample organisations only), never to other installations. A2, D3, D. |
| V6.3.4 | 2 | V6.3 General Authentication Security | Met | Sign-in pathways (password, email code, UAE PASS; second factor) documented in `docs/architecture/identity-access.md` and `uae-pass.md`. A2, D3. |
| V6.4.1 | 1 | V6.4 Authentication Factor Lifecycle and Recovery | Not assessed |  |
| V6.4.2 | 1 | V6.4 Authentication Factor Lifecycle and Recovery | Met | No password hints or secret questions. |
| V6.4.3 | 2 | V6.4 Authentication Factor Lifecycle and Recovery | Not assessed |  |
| V6.4.4 | 2 | V6.4 Authentication Factor Lifecycle and Recovery | Not assessed |  |
| V6.5.1 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.2 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.3 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.4 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.5.5 | 2 | V6.5 General Multi-factor authentication requirements | Not assessed |  |
| V6.6.1 | 2 | V6.6 Out-of-Band authentication mechanisms | Not applicable | No SMS or phone one-time passwords for sign-in. |
| V6.6.2 | 2 | V6.6 Out-of-Band authentication mechanisms | Not assessed |  |
| V6.6.3 | 2 | V6.6 Out-of-Band authentication mechanisms | Met | Email codes: limited attempts and lifetime in the Zelvo Keycloak extension, plus brute-force protection. A2. |
| V6.8.1 | 2 | V6.8 Authentication with an Identity Provider | Met | UAE PASS accounts linked by UAE PASS uuid; linking by email needs proof; Emirates ID match only for SOP2+. D3. |
| V6.8.2 | 2 | V6.8 Authentication with an Identity Provider | Met | Tokens verified with the tenant realm's signing keys; required strength from `acr` (RFC 9470 step-up). A2. |
| V6.8.3 | 2 | V6.8 Authentication with an Identity Provider | Not assessed |  |
| V6.8.4 | 2 | V6.8 Authentication with an Identity Provider | Met | Tokens verified with the tenant realm's signing keys; required strength from `acr` (RFC 9470 step-up). A2. |

## V7 Session Management

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V7.1.1 | 2 | V7.1 Session Management Documentation | Met | Idle 30 min and absolute 12 h time-outs, documented in `identity-access.md`. A2. |
| V7.1.2 | 2 | V7.1 Session Management Documentation | Not assessed |  |
| V7.1.3 | 2 | V7.1 Session Management Documentation | Not assessed |  |
| V7.2.1 | 1 | V7.2 Fundamental Session Management Security | Met | Server-side sessions in Valkey with random ids; the browser holds only the session cookie. A2. |
| V7.2.2 | 1 | V7.2 Fundamental Session Management Security | Met | Server-side sessions in Valkey with random ids; the browser holds only the session cookie. A2. |
| V7.2.3 | 1 | V7.2 Fundamental Session Management Security | Met | Server-side sessions in Valkey with random ids; the browser holds only the session cookie. A2. |
| V7.2.4 | 1 | V7.2 Fundamental Session Management Security | Met | A new session id at every sign-in; sign-out deletes the session. A2. |
| V7.3.1 | 2 | V7.3 Session Timeout | Met | Idle and absolute time-outs enforced by the web app's session store. A2. |
| V7.3.2 | 2 | V7.3 Session Timeout | Met | Idle and absolute time-outs enforced by the web app's session store. A2. |
| V7.4.1 | 1 | V7.4 Session Termination | Met | Sign-out deletes the server session and ends the Keycloak (and UAE PASS) session. A2, D3. |
| V7.4.2 | 1 | V7.4 Session Termination | Not assessed |  |
| V7.4.3 | 2 | V7.4 Session Termination | Not assessed |  |
| V7.4.4 | 2 | V7.4 Session Termination | Met | Sign-out in the header of every signed-in page. |
| V7.4.5 | 2 | V7.4 Session Termination | Not assessed |  |
| V7.5.1 | 2 | V7.5 Defenses Against Session Abuse | Not assessed |  |
| V7.5.2 | 2 | V7.5 Defenses Against Session Abuse | Not assessed |  |
| V7.6.1 | 2 | V7.6 Federated Re-authentication | Not assessed |  |
| V7.6.2 | 2 | V7.6 Federated Re-authentication | Not assessed |  |

## V8 Authorization

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V8.1.1 | 1 | V8.1 Authorization Documentation | Met | Permissions and field rules per role are data, documented in `identity-access.md`. A2. |
| V8.1.2 | 2 | V8.1 Authorization Documentation | Met | Permissions and field rules per role are data, documented in `identity-access.md`. A2. |
| V8.2.1 | 1 | V8.2 General Authorization Design | Met | Permission checks and field-level filtering in the API (`PermissionAuthorization`, `FieldFilter`); tests in `AccessControlApiTests`. A2. E1: integrations act with one role narrowed by their scopes and never hold `users.manage`, `roles.manage`, `identity.manage` or `integrations.manage` (`CurrentUser.SetClient`, `ApiClientApiTests`). |
| V8.2.2 | 1 | V8.2 General Authorization Design | Not assessed |  |
| V8.2.3 | 2 | V8.2 General Authorization Design | Met | Permission checks and field-level filtering in the API (`PermissionAuthorization`, `FieldFilter`); tests in `AccessControlApiTests`. A2. E1: field rules of an integration's role apply to its responses. |
| V8.3.1 | 1 | V8.3 Operation Level Authorization | Met | Permission checks and field-level filtering in the API (`PermissionAuthorization`, `FieldFilter`); tests in `AccessControlApiTests`. A2. |
| V8.4.1 | 2 | V8.4 Other Authorization Considerations | Met | PostgreSQL row-level security on every tenant-owned table (guard test), tokens only from the tenant's own realm, one search index per tenant. A1, A2, A4. |

## V9 Self-contained Tokens

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V9.1.1 | 1 | V9.1 Token source and integrity | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.1.2 | 1 | V9.1 Token source and integrity | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.1.3 | 1 | V9.1 Token source and integrity | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.2.1 | 1 | V9.2 Token content | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.2.2 | 2 | V9.2 Token content | Not assessed |  |
| V9.2.3 | 2 | V9.2 Token content | Met | JWTs checked for signature (realm keys from the tenant's realm only), algorithm, issuer, audience and lifetime (`ZelvoBearerHandler`). A2. |
| V9.2.4 | 2 | V9.2 Token content | Not assessed |  |

## V10 OAuth and OIDC

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V10.1.1 | 2 | V10.1 Generic OAuth and OIDC Security | Met | Backend for frontend: tokens stay on the web server, never in the browser. A2. |
| V10.1.2 | 2 | V10.1 Generic OAuth and OIDC Security | Not assessed |  |
| V10.2.1 | 2 | V10.2 OAuth Client | Met | Authorization code flow with PKCE, state and nonce (openid-client). A2. |
| V10.2.2 | 2 | V10.2 OAuth Client | Not assessed |  |
| V10.3.1 | 2 | V10.3 OAuth Resource Server | Met | API accepts only tokens for its audience from the request tenant's realm; users identified by issuer and `sub`; strength from `acr`. A2. |
| V10.3.2 | 2 | V10.3 OAuth Resource Server | Not assessed |  |
| V10.3.3 | 2 | V10.3 OAuth Resource Server | Met | API accepts only tokens for its audience from the request tenant's realm; users identified by issuer and `sub`; strength from `acr`. A2. |
| V10.3.4 | 2 | V10.3 OAuth Resource Server | Met | API accepts only tokens for its audience from the request tenant's realm; users identified by issuer and `sub`; strength from `acr`. A2. |
| V10.4.1 | 1 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. |
| V10.4.2 | 1 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.3 | 1 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.4 | 1 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. E1: integration clients may use only the client credentials grant (no browser flows, no redirect URIs, `fullScopeAllowed` off). |
| V10.4.5 | 1 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.6 | 2 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. |
| V10.4.7 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.8 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.9 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.4.10 | 2 | V10.4 OAuth Authorization Server | Met | Keycloak clients provisioned with exact redirect URIs, only the code grant for the web client, PKCE, confidential client authentication. A2. |
| V10.4.11 | 2 | V10.4 OAuth Authorization Server | Not assessed |  |
| V10.5.1 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.2 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.3 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.4 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.5.5 | 2 | V10.5 OIDC Client | Not assessed |  |
| V10.6.1 | 2 | V10.6 OpenID Provider | Not assessed |  |
| V10.6.2 | 2 | V10.6 OpenID Provider | Not assessed |  |
| V10.7.1 | 2 | V10.7 Consent Management | Not assessed |  |
| V10.7.2 | 2 | V10.7 Consent Management | Not assessed |  |
| V10.7.3 | 2 | V10.7 Consent Management | Not assessed |  |

## V11 Cryptography

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V11.1.1 | 2 | V11.1 Cryptographic Inventory and Documentation | Partly met | A5: key management for stored secrets documented in `docs/architecture/secrets-encryption.md` (data keys, master key file or Vault/OpenBao Transit, rotation with `rewrap-keys`); a full key management policy follows with F6. |
| V11.1.2 | 2 | V11.1 Cryptographic Inventory and Documentation | Partly met | A5: inventory started in `secrets-encryption.md` and `security-baseline.md` (data keys, master key, TLS certificates, audit hash chain, token signing keys in Keycloak). |
| V11.2.1 | 2 | V11.2 Secure Cryptography Implementation | Met | .NET (OpenSSL), Keycloak and OpenBao/Vault implementations; no custom primitives. |
| V11.2.2 | 2 | V11.2 Secure Cryptography Implementation | Met | Versioned formats (`zv1.` values, `kek_id` on data keys, audit chain version) allow algorithm changes. |
| V11.2.3 | 2 | V11.2 Secure Cryptography Implementation | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.3.1 | 1 | V11.3 Encryption Algorithms | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.3.2 | 1 | V11.3 Encryption Algorithms | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.3.3 | 2 | V11.3 Encryption Algorithms | Met | AES-256-GCM (authenticated) for stored secrets and wrapped data keys; ECDSA P-256 certificates. A5. |
| V11.4.1 | 1 | V11.4 Hashing and Hash-based Functions | Met | SHA-256 for the audit hash chain and key ids. A1, A5. |
| V11.4.2 | 2 | V11.4 Hashing and Hash-based Functions | Met | Passwords only in Keycloak (PBKDF2/Argon2 per Keycloak policy); OpenSearch internal users as bcrypt hashes. A2, A5. |
| V11.4.3 | 2 | V11.4 Hashing and Hash-based Functions | Met | SHA-256 for the audit hash chain and key ids. A1, A5. |
| V11.4.4 | 2 | V11.4 Hashing and Hash-based Functions | Not assessed |  |
| V11.5.1 | 2 | V11.5 Random Values | Met | `RandomNumberGenerator` for data keys and nonces; random session ids and CSRF tokens. A2, A5. |
| V11.6.1 | 2 | V11.6 Public Key Cryptography | Not assessed |  |

## V12 Secure Communication

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V12.1.1 | 1 | V12.1 General TLS Security Guidance | Partly met | A5: TLS 1.3 only for OpenSearch and RabbitMQ; public HTTPS (web, API, Keycloak) is terminated by the hardened install's proxy (F6). |
| V12.1.2 | 2 | V12.1 General TLS Security Guidance | Not assessed |  |
| V12.1.3 | 2 | V12.1 General TLS Security Guidance | Not assessed |  |
| V12.2.1 | 1 | V12.2 HTTPS Communication with External Facing Services | Open | External HTTPS with publicly trusted certificates: hardened install (F6) and custom domains (F1). |
| V12.2.2 | 1 | V12.2 HTTPS Communication with External Facing Services | Open | External HTTPS with publicly trusted certificates: hardened install (F6) and custom domains (F1). |
| V12.3.1 | 2 | V12.3 General Service to Service Communication Security | Partly met | A5: TLS for OpenSearch, RabbitMQ and Vault; PostgreSQL, Valkey, Keycloak back channel and the OTLP collector are still plain inside the Compose network (follow-up, F6). |
| V12.3.2 | 2 | V12.3 General Service to Service Communication Security | Met | A5: clients validate server certificates (CA chain and host name) for OpenSearch, RabbitMQ and Vault. Tests: `ServiceTransportSecurityTests`. |
| V12.3.3 | 2 | V12.3 General Service to Service Communication Security | Partly met | See V12.3.1. |
| V12.3.4 | 2 | V12.3 General Service to Service Communication Security | Partly met | A5: internal certificates from a development CA that clients trust explicitly; installs use their own CA (F6). |

## V13 Configuration

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V13.1.1 | 2 | V13.1 Configuration Documentation | Not assessed |  |
| V13.2.1 | 2 | V13.2 Backend Communication Configuration | Partly met | A5: each service has its own least-privilege user for PostgreSQL (no BYPASSRLS), OpenSearch and RabbitMQ; Valkey still has one shared password. |
| V13.2.2 | 2 | V13.2 Backend Communication Configuration | Partly met | A5: each service has its own least-privilege user for PostgreSQL (no BYPASSRLS), OpenSearch and RabbitMQ; Valkey still has one shared password. |
| V13.2.3 | 2 | V13.2 Backend Communication Configuration | Met | No default credentials: `dev-up.sh` generates random passwords; RabbitMQ `guest` is loopback-only and not created. |
| V13.2.4 | 2 | V13.2 Backend Communication Configuration | Partly met | E1: outgoing webhooks reach only addresses the installation allows (`Integrations:Webhooks:AllowedPrivateNetworks`, HTTPS only, internal networks refused by default). The no-code connector builder (E7) will use the same policy. |
| V13.2.5 | 2 | V13.2 Backend Communication Configuration | Not assessed |  |
| V13.3.1 | 2 | V13.3 Secret Management | Partly met | A5: stored secrets encrypted with keys from a master key file or Vault/OpenBao; service passwords are environment variables (`.env`, git-ignored) until the hardened install (F6). |
| V13.3.2 | 2 | V13.3 Secret Management | Met | A5: the API and worker can read only the CA certificate, not other services' keys; the Vault policy allows only encrypt/decrypt/rewrap with Zelvo's key. |
| V13.4.1 | 1 | V13.4 Unintended Information Leakage | Met | Images built from explicit project folders; `.git` is not copied. |
| V13.4.2 | 2 | V13.4 Unintended Information Leakage | Not assessed |  |
| V13.4.3 | 2 | V13.4 Unintended Information Leakage | Not assessed |  |
| V13.4.4 | 2 | V13.4 Unintended Information Leakage | Not assessed |  |
| V13.4.5 | 2 | V13.4 Unintended Information Leakage | Met | OpenAPI and the API explorer are on in Development only (elsewhere `OpenApi:Enabled=true`); Prometheus is not published, Grafana and admin consoles are bound to 127.0.0.1 and need a sign-in. |

## V14 Data Protection

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V14.1.1 | 2 | V14.1 Data Protection Documentation | Not assessed |  |
| V14.1.2 | 2 | V14.1 Data Protection Documentation | Not assessed |  |
| V14.2.1 | 1 | V14.2 General Data Protection | Not assessed |  |
| V14.2.2 | 2 | V14.2 General Data Protection | Not assessed |  |
| V14.2.3 | 2 | V14.2 General Data Protection | Met | A5: telemetry stays on the installation (no calls home); traces carry no query strings, client addresses or user ids. |
| V14.2.4 | 2 | V14.2 General Data Protection | Not assessed |  |
| V14.3.1 | 1 | V14.3 Client-side Data Protection | Not assessed |  |
| V14.3.2 | 2 | V14.3 Client-side Data Protection | Not assessed |  |
| V14.3.3 | 2 | V14.3 Client-side Data Protection | Met | The browser stores only the session cookie and the language cookie, and in demo installations the chosen sample organisation. A2, A3, D. |

## V15 Secure Coding and Architecture

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V15.1.1 | 1 | V15.1 Secure Coding and Architecture Documentation | Not assessed |  |
| V15.1.2 | 2 | V15.1 Secure Coding and Architecture Documentation | Met | CycloneDX SBOM per image in CI; Dependabot; Grype image scanning; NuGet and npm vulnerability gates. |
| V15.1.3 | 2 | V15.1 Secure Coding and Architecture Documentation | Not assessed |  |
| V15.2.1 | 1 | V15.2 Security Architecture and Dependencies | Not assessed |  |
| V15.2.2 | 2 | V15.2 Security Architecture and Dependencies | Not assessed |  |
| V15.2.3 | 2 | V15.2 Security Architecture and Dependencies | Not assessed |  |
| V15.3.1 | 1 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.2 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.3 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.4 | 2 | V15.3 Defensive Coding | Met | A5: client address forwarded by the web app and accepted by the API only from trusted proxies. |
| V15.3.5 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.6 | 2 | V15.3 Defensive Coding | Not assessed |  |
| V15.3.7 | 2 | V15.3 Defensive Coding | Not assessed |  |

## V16 Security Logging and Error Handling

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V16.1.1 | 2 | V16.1 Security Logging Documentation | Not assessed |  |
| V16.2.1 | 2 | V16.2 General Logging | Met | Audit entries record when, who (actor), what and from where (client address, correlation id); logs carry trace ids. A1, A5. |
| V16.2.2 | 2 | V16.2 General Logging | Met | Audit and telemetry timestamps in UTC. |
| V16.2.3 | 2 | V16.2 General Logging | Not assessed |  |
| V16.2.4 | 2 | V16.2 General Logging | Met | A5: logs as OpenTelemetry records (OTLP) in Loki, correlated with traces. |
| V16.2.5 | 2 | V16.2 General Logging | Partly met | A5: no tokens, passwords, query strings or SQL values in telemetry; a written log inventory with protection levels is still to do. E1: the integration log stores request and response bodies with personal data and secrets masked (`PersonalDataMasker`), kept 30 days. |
| V16.3.1 | 2 | V16.3 Security Events | Not assessed |  |
| V16.3.2 | 2 | V16.3 Security Events | Partly met | Refused requests (401/403) are logged by ASP.NET Core and visible in Loki; they are not yet written to the audit log as security events. E1: every call made by an integration, including refused ones, is in the audit log (`integrations.api.called`) and the integration log. |
| V16.3.3 | 2 | V16.3 Security Events | Not assessed |  |
| V16.3.4 | 2 | V16.3 Security Events | Met | A5: TLS and dependency failures are logged and shown by readiness checks; rate-limit cache outages logged and counted. |
| V16.4.1 | 2 | V16.4 Log Protection | Not assessed |  |
| V16.4.2 | 2 | V16.4 Log Protection | Met | Audit log append-only and hash-chained in the database (triggers); verification endpoint. A1. |
| V16.4.3 | 2 | V16.4 Log Protection | Met | A5: logs sent over OTLP to Loki, separate from the services. |
| V16.5.1 | 2 | V16.5 Error Handling | Met | Problem details with stable codes and a trace id; no exception details (health responses tested). A1. |
| V16.5.2 | 2 | V16.5 Error Handling | Met | Search outbox with retries, dead-letter queue; rate limiting fails open with an alert; readiness reports each dependency. A4, A5. |
| V16.5.3 | 2 | V16.5 Error Handling | Met | Secrets that cannot be decrypted, tampered values and missing keys raise errors; nothing proceeds without them. A5. |

## V17 WebRTC

| Requirement | Level | Section | Status | Evidence and notes |
| --- | --- | --- | --- | --- |
| V17.1.1 | 2 | V17.1 TURN Server | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.1 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.2 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.3 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.2.4 | 2 | V17.2 Media | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.3.1 | 2 | V17.3 Signaling | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
| V17.3.2 | 2 | V17.3 Signaling | Not applicable | No WebRTC (screen sharing is a Phase 4 module). |
