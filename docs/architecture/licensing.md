# Licensing and copy protection (modules 12 and 13)

Every installation runs under a license we issue for its servers. Activation and every check work offline; Zelvo never contacts us (module 12). Product owner decision 2026-11-25: the licensing code (verification, fingerprint, watermark and the offline signing tool) is built in this repository, which is made private when development is complete. **The signing private key is never committed:** the signing tool makes it on the product owner's own computer, and tests make throwaway keys at run time.

## Projects

| Project | Contents |
| --- | --- |
| `src/api/Zelvo.Licensing.Abstractions` | The contract: `ILicenseService`, `LicenseStatus` (with `ProblemCode` and `ActivationRequired`), `LicensedModules`, `LicenseProblems`, `InstallationProfile` and `LicenseStartupCheck`. |
| `src/api/Zelvo.Licensing` | `LicenseService` (verification, upgrades, activation requests, vendor signatures for update packages), signed files (`SignedFile`), the license format (`LicensePayload`, `ActivationRequestFile`, `Fingerprint`), `VendorKeys` with our public keys (`vendor-keys.json`), the installation store (`installation`, `installation_licenses`), `Watermark`, `LicenseIssuer` (used by the signing tool and tests) and `AddZelvoLicensing()`. |
| `src/tools/Zelvo.SigningTool` | `zelvo-sign`, the offline signing tool for the product owner's computer. |
| `src/api/Zelvo.Api/Installation` | Settings → License API, `LicenseGate` (module gating of permissions, operator tenant) and `LicenseEnforcementMiddleware` (activation mode, module gating of endpoints). |

Feature code asks `ILicenseService` (for example `IsModuleLicensed(LicensedModules.Payments)`) and never checks licenses itself.

## The license file

A license is a JSON file in Zelvo's signed format (`zelvo-signed/1`):

```json
{ "format": "zelvo-signed/1", "type": "license", "keyId": "zelvo-20261125",
  "algorithm": "RSA-PSS-SHA256", "payload": "<base64 JSON>", "signature": "<base64>" }
```

- The signature is **RSA-4096 with PSS and SHA-256** (requirements: Ed25519 or RSA-4096; .NET has RSA built in, so no new dependency). It covers the format, the file type and the payload's exact bytes, so nothing is re-serialised before checking and a signature of one file type never fits another (license, upgrade license, update package manifest).
- The payload holds the license id, kind (`Production`, `Test` or `Demo`), the customer (our id and bilingual name), the **watermark**, the licensed modules, the feature version bought (major.minor), the issue time, the activation request it answers and the **fingerprint** it is bound to. Any edit breaks the signature.
- Keys shorter than 3072 bits are refused. Several vendor keys can be trusted at once (key rotation); each file names its key.

An **upgrade license** (`type: upgrade`) names the license it upgrades, the feature version it unlocks and the same fingerprint. It works only next to that license on those servers.

## Fingerprint

| Part | Where it comes from | Checked |
| --- | --- | --- |
| Installation id | A random id made once by `migrate` (`installation.id`). | Always |
| Database id | PostgreSQL's system identifier (`zelvo_database_identifier()`, a security-definer function over `pg_control_system()`); restoring a backup into another database cluster changes it. | Always |
| Machine id | The host's `/etc/machine-id`, mounted read-only into the API and worker (`Licensing:MachineIdFiles`; Compose `ZELVO_MACHINE_ID_FILE`). | When the license lists machine ids |
| Cluster id | `Licensing:ClusterId`, set by the Helm chart (task F6) from the cluster; machine ids are not checked then, as pods move between nodes. | When the license has one |
| Domain | `Licensing:Domain`, else `Branding:PublicHost`. | When the license lists domains |

A copied installation fails at least one part. The license screen shows each part and whether it matches.

## Activation (setup) and server moves

1. The administrator downloads the **activation request** (Settings → License or the first step of the setup wizard; also `POST /api/v1/license/activation-request`). It holds a random request id, the installed version, the installation profile, the license installed now (if any) and the fingerprint; no secrets or personal data.
2. The customer sends it to us (email or USB). On the signing computer: `zelvo-sign issue --request … --key … --license-id … --customer-id … --customer-en … --customer-ar … --modules … --version …`. The tool prints the new watermark for our customer sheet.
3. The administrator imports the license file (`POST /api/v1/license/import`). Zelvo checks the signature, the fingerprint and that the kind matches the installation profile before keeping it; a refused file changes nothing and answers 422 with the reason (`license-signature-invalid`, `license-fingerprint-mismatch`, `license-kind-mismatch`, `license-upgrade-mismatch`, `license-format-invalid` …). A new license replaces the old one and its upgrades.

Every request and import, accepted or refused, is in the audit log (`license.activation-requested`, `license.imported`, `license.import-refused`).

**Server moves** are the same steps: the new servers' fingerprint differs, so the installation waits for activation until the replacement license is imported. **Paid feature releases:** the administrator sends a new activation request, we run `zelvo-sign upgrade --request … --version 1.5`, and the upgrade license is imported the same way.

## What services do with the license

| Status | API | Worker |
| --- | --- | --- |
| Valid | Everything licensed. | All jobs. |
| Waiting for activation (no license, a license for other servers or another installation profile, database not reachable) | **Activation mode**: only `/system`, `/tenant`, `/me`, `/license`, `/setup`, `/branding`, `/translations`, `/demo` and the published privacy notice (`/privacy/notice`) answer; everything else is 503 `license-required`; integrations are refused. The web app shows a banner on every page. | Waits before its jobs start, and checks again every 30 seconds. |
| Tampered (signature not valid, unknown key, malformed, another customer's license in a customer build) | Refuses to start (critical log). | Refuses to start. |

`LicenseRefreshService` verifies again every minute, so a license imported through the API applies in the worker within a minute.

### Module gating

- **Permissions:** `PermissionCatalog.ModuleOf` gives each module's permissions (incidents, catalog and requests, customer voice, workspace, channels, knowledge, SLA, AI, reports, branding, integrations, payments). `LicenseGate` leaves out the permissions of modules the license does not include, so they never apply, `/me` does not list them and the web app hides what needs them.
- **Endpoints:** in `Program.cs` each module's endpoints are mapped through `v1.ForModule(LicensedModules.X)`; they answer 403 `module-not-licensed` without the module. API keys and OAuth clients need module 14.
- **Screens:** `GET /api/v1/system/info` lists the licensed modules; the header hides the portal links of modules the license does not include (help, catalog, contact us, my tickets).

The shared platform (users, roles, settings, directory, forms, notifications, workflow rules, the setup wizard, license and updates) belongs to no module and is always available.

### Who manages the license

Licenses (and update packages, task F4) belong to the whole installation, so `installation.manage` (tenant administrators by default; never for integrations, for the product owner's approval) applies only in the installation's **operator tenant**: `Installation:OperatorTenant`, else `Tenancy:DefaultTenant`. Administrators of other tenants see the license step of the setup wizard as done by the installation's administrator.

## Watermark

Each customer gets a watermark (`ZW-XXXX-XXXX-XXXX-C`, Crockford base32 with a check character, made by the signing tool). It means nothing on its own; our customer sheet maps it to the customer. It is carried by:

- the **license** (signed), and the **database** (`installation.watermark`, written on import);
- **per-customer builds** (task F6): `-p:ZelvoBuildWatermark=ZW-…` compiles it into every assembly (`AssemblyMetadata`); such a build runs only with a license carrying the same watermark (`watermark-mismatch` otherwise);
- **generated files**: `IInstallationWatermark.Current` (registered in every service) gives it to the code that writes files; the activation request names the installed license and customer. The hidden marks in PDF and Excel exports and email headers are added with task F6's release work.

The license screen never shows the watermark.

## Development and release builds

| | Development build (default) | Release build (`-p:ZelvoRelease=true`, task F6) |
| --- | --- | --- |
| No license installed | The **development license**: every module and version, kind `Development` (`Demo` or `Test` for those installation profiles). Never in the `Production` environment. `Licensing:DevelopmentLicense=false` turns it off to try activation. | Activation mode. |
| Trusted keys | `vendor-keys.json` plus `Licensing:DevelopmentKeys` from configuration (a developer's own key). | `vendor-keys.json` only; the build fails while it has no key. |

To try activation locally: `dotnet run --project src/tools/Zelvo.SigningTool -- keygen --out ~/zelvo-dev-keys --id dev-key` (outside the repository), put the printed id and public key in `deploy/compose/.env` as `ZELVO_DEV_SIGNING_KEY_ID` and `ZELVO_DEV_SIGNING_PUBLIC_KEY`, set `ZELVO_DEVELOPMENT_LICENSE=false`, `make up`, then download the activation request and issue a license with that key.

### Signing tool

`zelvo-sign` (run with `dotnet run --project src/tools/Zelvo.SigningTool -- <command>`):

| Command | Does |
| --- | --- |
| `keygen --out <dir> [--id <key-id>]` | A new RSA-4096 key pair. The private key is PKCS#8, encrypted with a passphrase (AES-256, PBKDF2-SHA256, 600,000 rounds; typed without echo, or `ZELVO_SIGNING_PASSPHRASE`), readable by the owner only, never overwritten, and **never written inside a git working tree**. Prints the public key entry for `vendor-keys.json`. |
| `inspect <file>` | Shows an activation request or a license (without checking it). |
| `issue …` | A license for the servers of an activation request; refuses a kind that does not fit the request's installation profile. |
| `upgrade …` | An upgrade license for the license named in the request. |
| `verify <file> [--keys <vendor-keys.json>]` | Checks a signature. |
| `package …` | Signs an update package (task F4). |

`.gitignore` also ignores `*.pem` and `*.private.pem`.

## Installation profiles

`Installation:Profile` says what an installation is for: `Standard` (default; needs a `Production` license), `Demo` (demo mode, `demo-mode.md`; a `Demo` license) or `Test`, the separate test installation every license includes (module 14, `integrations.md`; a `Test` license). Each gets its own license file for its own fingerprint. `ILicenseService.EnvironmentName()` turns the license kind into what integrations see (`production`, `test`, `demo`, `development`).

## Not in F3

Image signatures checked between services, obfuscation and minified builds, the per-customer release build and the hidden marks in generated files come with release packaging (task F6).
