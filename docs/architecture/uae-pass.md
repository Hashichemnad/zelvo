# UAE PASS sign-in (task D3)

Covers **GOV-1**: sign-in with UAE PASS for portal users and staff, and module 4's "Login by UAE PASS". Builds on identity and access (A2, `identity-access.md`).

Integration terms still have to be confirmed with the UAE PASS programme (requirements, GOV-1). Until Zelvo or a customer is onboarded, it is built against the published UAE PASS web integration (docs.uaepass.ae) and tested against a mock. The UAE PASS staging hosts were not reachable from the build environment, so nothing has been tried against real staging yet.

## How it works

```
Browser ── Zelvo web ── Keycloak realm zelvo-<tenant> ── identity provider "uaepass" ── UAE PASS
                              (OAuth 2.0 broker)            authorize / token / userinfo / logout
```

- **Through Keycloak.** UAE PASS is an identity provider (alias `uaepass`) in each tenant's realm. The API and web app only ever see Keycloak tokens, so every A2 rule still applies: the token must come from the tenant's own realm, users are created as requesters, roles and permissions are Zelvo data, and staff need a second factor.
- **Keycloak's generic OAuth 2.0 broker (`oauth2`).** UAE PASS issues an opaque access token and no ID token, so Keycloak's OpenID Connect broker cannot be used. Keycloak has a generic OAuth 2.0 broker (checked in the running image, 26.7.5 and 26.8.2).
- **Zelvo Keycloak extension (product owner decision 2026-11-18).** Keycloak's own attribute mappers do not work with that broker, and nothing built in finds an account by Emirates ID, so `src/keycloak` (package `zelvo.keycloak.uaepass`, with unit tests) adds two pieces:
  - **`zelvo-uaepass-profile`**, an identity provider mapper. At every UAE PASS sign-in it reads the account level (`userType`: SOP1, SOP2 or SOP3) and, for verified accounts, the Emirates ID (`idn`).
    - The level goes into a user session note, which becomes the `zelvo_uaepass_level` claim in tokens of that session.
    - The Emirates ID is used only by the first sign-in step and is not stored on the user.
    - A missing or unknown level counts as SOP1, so nothing is granted on data UAE PASS did not send.
  - **`zelvo-uaepass-first-login`**, the first sign-in step, in place of Keycloak's "Create User If Unique" (see "Sign-in flows").
  - Keycloak resets session notes before the first sign-in flow and when an account is linked with its password (found against the running server). So the mapper also keeps the level and Emirates ID in the brokered context. With sync mode `FORCE` it sets the level again after linking and at every later sign-in.
- **Settings sent to Keycloak** (`KeycloakAdminClient.UaePassRepresentation`):
  - Client authentication is HTTP Basic (`client_secret_basic`), the only kind UAE PASS accepts.
  - Scope `urn:uae:digitalid:profile:general`.
  - `acr_values=urn:safelayer:tws:policies:authentication:level:low` is added to the authorization address. Keycloak keeps that query and does not pass the Zelvo `acr` on.
  - `ui_locales` follows the sign-in page's language, so UAE PASS shows English or Arabic.
  - People are matched by their UAE PASS `uuid`. UAE PASS recommends this because email and mobile can change.
  - Email, first name and last name (`firstnameEN`, `lastnameEN`) are imported when the account is created (`syncMode=IMPORT`), and later sign-ins do not overwrite them.
  - Nothing else is stored: no Emirates ID (`idn`), mobile, nationality or Arabic names (data minimisation). Storing them waits for the features that need them.
- **Fixed addresses.** The UAE PASS addresses come with the product (`UaePassOptions.EndpointsFor`). An administrator chooses only the environment:

| Environment | Host |
| --- | --- |
| Staging | `stg-id.uaepass.ae` |
| Production | `id.uaepass.ae` |
| Mock | Only when the installation sets `Identity:UaePass:Mock` (development Compose) |

## Settings → Sign-in → UAE PASS

- **API.** `GET` and `PUT /api/v1/identity/uae-pass` need `identity.manage`, which tenant administrators hold. The settings are:
  - on or off;
  - environment;
  - client ID;
  - client secret;
  - the GOV-1 rules (below). A rule left out of a `PUT` keeps its value.
- **Tenant settings** (`tenant_settings`, audited field by field):
  - `identity.uaePass.enabled`
  - `identity.uaePass.environment`
  - `identity.uaePass.clientId`
  - `identity.uaePass.clientSecretChangedAt`
  - `identity.uaePass.acceptAsSecondFactor`
  - `identity.uaePass.requesterMinimumLevel`
  - `identity.uaePass.createRequesters`

### Rules for staff and requesters (GOV-1, product owner decisions 2026-11-18)

"Staff" means a user with a role that needs a second factor: agents, team leads and tenant administrators, and any role an administrator sets to need one.

| Rule | Default | Where it is applied |
| --- | --- | --- |
| Accept UAE PASS as the second factor for staff (verified accounts, SOP2+, only) | Off | API: a UAE PASS session (`zelvo_idp=uaepass`) at SOP2+ counts as the second factor, so staff roles apply without the authenticator app |
| Staff need a verified UAE PASS account (SOP2+); not configurable | Always | API: refuses UAE PASS sessions of staff below SOP2 (403 `uae-pass-level-too-low`) |
| Minimum UAE PASS level for requesters | SOP1 | Keycloak step: new accounts below it are not created. API: refuses UAE PASS sessions below it (403 `uae-pass-level-too-low`) |
| Create requester accounts automatically on first UAE PASS sign-in | On | Keycloak step: when off, people without an account are refused ("There is no account for your UAE PASS yet") |

- The two first sign-in rules are copied into the configuration of the extension step when the settings are saved.
- The web app shows the level refusal as its own sign-in problem in English and Arabic. The Keycloak refusals use the `zelvo` login theme's English and Arabic messages.
- Password and email-code sign-ins are not affected by these rules.
- **The client secret is never stored by Zelvo and never returned.**
  - It goes straight to the realm's identity provider, which is the only place that uses it.
  - Zelvo keeps only the time it last changed, so the audit log records each change without the value.
  - When `clientSecret` is left out, Keycloak keeps the current secret. It gets its own mask back, which was checked against the running server.
- **Turning it on** needs a client ID and a saved secret (400 otherwise).
- **Saving.** The settings are saved and applied to Keycloak together. If Keycloak refuses, nothing changes (502 `identity-provider-unavailable`), as for the email code settings.
- **Redirect URI.** The response gives the address to register with UAE PASS: `{Keycloak public URL}/realms/zelvo-<tenant>/broker/uaepass/endpoint`. The Settings page shows it.
- **For sign-in pages.** `GET /api/v1/identity/sign-in-methods` is open to visitors. It tells sign-in pages whether UAE PASS is on, and gives its sign-out address.
- **Keycloak permissions.** The `zelvo-provisioner` service account now also has `view-identity-providers` and `manage-identity-providers` on tenant realms, and still on tenant realms only.

## Sign-in flows (built by `migrate` in every realm)

**First UAE PASS sign-in (`zelvo uaepass first sign-in`).** The decisions are in `FirstLoginPolicy`:

1. **Emirates ID match.** A verified account (SOP2+) whose Emirates ID an administrator recorded on exactly one account is linked to that account straight away. This is how staff are matched. If that account already has another UAE PASS account linked, the sign-in is refused.
2. **Email match.** Otherwise, if an account has the same email, it is linked only after its owner proves it is theirs: a link sent to that email, or "Try Another Way" and the account's password (decision 2026-11-18). The identity provider does not trust the email (`trustEmail=false`), because UAE PASS warns against linking by email alone.
3. **New person.** Otherwise a new account is created, which Zelvo makes a requester at the first API call, when the tenant creates requester accounts and the level meets the requesters' minimum. Staff accounts are never created here.

There is no profile step: people cannot change what UAE PASS sent, as UAE PASS guidance requires.

**Emirates ID on accounts.**

- **Where:** column `users.emirates_id`. It is 15 digits starting with 784, unique per tenant, personal data, and masked in the audit log.
- **Who sets it:** tenant administrators, with `PATCH /api/v1/users/{id}` and `emiratesId` (an empty string removes it). Field rule `users.emiratesId`: Edit for tenant administrators, given once; hidden from other roles.
- **Keycloak copy:** it is copied to the Keycloak user attribute `zelvo_emirates_id` in the same transaction. `migrate` declares that attribute in each realm's user profile as visible and editable by administrators only.
- **Settings screen:** comes with the users screens (B2).

**After every UAE PASS sign-in (`zelvo uaepass after login`).**

- The sign-in counts as level 1, one factor (`acr=pwd`).
- When the web app asks for `acr=mfa`, as it does for agents, team leads and administrators, Keycloak asks only for the authenticator app. It does not ask for a password.
- Without this flow, Keycloak did not record a level for brokered sign-ins, and a later step-up asked for the password again. This was checked against the running server.

## Signing out

UAE PASS requires service providers to end the UAE PASS session when the user signs out.

- The `zelvo-web` client puts the session's identity provider in the ID token (`zelvo_idp`), and the web session keeps it.
- After a UAE PASS sign-in, `/auth/logout` ends the Zelvo session, then the Keycloak session, and Keycloak returns to `/auth/signed-out/uae-pass`.
- That route sends the browser to the UAE PASS sign-out address from `sign-in-methods`, with `redirect_uri` set to the home page.
- The address always comes from the API, never from the request, so the route cannot be used to redirect elsewhere.

## Development mock

`deploy/compose/uaepass-mock` is plain Node.js with no packages, run in the official `node:22-alpine` image.

- **Port.** It runs at `http://localhost:8095`.
- **Credentials.** Client ID `zelvo-mock`; the client secret is `UAEPASS_MOCK_CLIENT_SECRET` in `deploy/compose/.env`.
- **What it does.** It behaves like the documented endpoints:
  - authorization with `acr_values` and `ui_locales`;
  - a token request with HTTP Basic only, a single-use code and a redirect URI check;
  - user information in UAE PASS's format;
  - sign-out.
- **Test people.** Instead of the UAE PASS app, it lists test people:
  - one of each account type (SOP1, SOP2, SOP3);
  - two who share an email with development users (`agent@zelvo.test`, `requester@zelvo.test`), to show account linking;
  - the team lead's personal UAE PASS account (`layla.personal@uaepass.test`, Emirates ID 784000000000006), to show matching by Emirates ID once an admin records it on `lead@zelvo.test`.
- **To try it:**
  1. Run `make up` and sign in as `admin@zelvo.test`.
  2. In Settings → Sign-in → UAE PASS, choose **Development mock**, client ID `zelvo-mock`, and that secret.
  3. Sign out and choose UAE PASS on the sign-in page.
- **Installs.** They do not set `Identity:UaePass:Mock`, so "Mock" is refused there.

To try UAE PASS staging, use the programme's published test client:

- Choose **Staging**, client ID `sandbox_stage` and secret `sandbox_stage` (docs.uaepass.ae, "Testing Credentials for POC").
- Sign in with a UAE PASS staging account in the staging app.
- The programme issues a tenant's own staging credentials during onboarding.

## Tests

- **Keycloak extension** (`mvn verify`): profile reading, the first sign-in decisions, the mapper, and the step's Emirates ID and refusal paths against mocked Keycloak services.
- **API rules** (`UaePassRulesApiTests`): the approved defaults; rules reaching the Keycloak step; levels for staff and requesters; UAE PASS as the second factor only when accepted and only with the UAE PASS claim; other sign-ins unaffected; Emirates ID format, uniqueness, field access, audit masking, and Keycloak refusal.
- **API** (`UaePassApiTests`):
  - defaults and the redirect URI;
  - the secret only reaching Keycloak, never in responses or the audit log;
  - keeping the secret;
  - addresses per environment;
  - the mock only when configured;
  - validation;
  - nothing changing when Keycloak refuses;
  - permissions and tenant isolation;
  - the Keycloak representation.
- **Web:** component tests for the Settings form (CSRF, the secret is write-only, field errors in the page's language, the redirect URI).
- **End to end** (`tests/e2e/specs/uae-pass.spec.ts`, its own Playwright project after tenant settings):
  - an admin turns UAE PASS on in English and Arabic, with axe checks;
  - a citizen signs in from the Arabic site and gets a requester account, and signing out ends the UAE PASS session;
  - a requester links by email link;
  - an agent links with their password and is then asked only for the authenticator app;
  - a team lead is matched by the Emirates ID an admin recorded, without email proof;
  - an agent skips the authenticator app when the tenant accepts UAE PASS as the second factor;
  - "known users only" refuses an unknown person, and the requesters' minimum refuses a new SOP1 person (in Arabic).

## Not in D3

- **Official UAE PASS button design.** The UAE PASS design guidelines' button and logo for the sign-in page are not applied yet. The sign-in page shows Keycloak's standard button with the text "UAE PASS". This comes with tenant branding of the Keycloak pages (F1), using the assets the programme provides at onboarding.
- **Settings screen for Emirates IDs.** Recording them uses the API until the users screens (B2).
- **UAE PASS mobile app-to-app sign-in, visitor profiles, digital signature and eSeal.**
