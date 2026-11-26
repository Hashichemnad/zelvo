# Release packaging

Task F6 (module 12 compiled delivery; NFR deployment). Product owner decisions 2026-11-26: images
are signed with **cosign and Zelvo's own key** (no public transparency log), published to a
**private GitHub Container Registry** and shipped as **image files** in the install bundle and the
update packages; .NET code is obfuscated with **Obfuscar**; tamper detection works through a
**signed release manifest**; Helm installs use **operators or managed services** for the data
services. Backups: `backup-restore.md`.

## What a release is

| Part | Where it comes from |
| --- | --- |
| Images `zelvo/api`, `worker`, `updater`, `web`, `keycloak`, `opensearch`, `postgres` | `release.yml`, release builds (below) |
| Their cosign signatures and CycloneDX SBOM attestations | `release.yml`, job "sign" (environment `release`) |
| The release manifest `release-manifest.json` and its signature `.sig` | `release.yml`, job "sign" |
| The install bundle `zelvo-<tag>.tar` | `release.yml`, job "bundle" (`scripts/release/bundle.sh`) |
| The update package `zelvo-<version>.zpkg` | the product owner, on the signing computer: `zelvo-sign package` (F4) over the unpacked bundle |
| The obfuscation mappings | `release.yml` artifact, kept by us to read stack traces; never shipped |

The pipeline runs on a `v<major>.<minor>.<patch>` tag, or by hand with a version and, for a
**per-customer build**, the customer's watermark (`ZW-…`, from `zelvo-sign issue`). Per-customer
images are tagged `<version>-<watermark in lower case>` and only run with a license carrying the
same watermark (F3). Before the first release the product owner:

1. makes the key pair on their own computer: `cosign generate-key-pair` (cosign.key is encrypted
   with a password);
2. puts `cosign.key` and its password into the GitHub environment `release` as
   `COSIGN_PRIVATE_KEY` and `COSIGN_PASSWORD` (the environment can require the product owner's
   approval for each run);
3. adds the **public** key with `scripts/release/add-image-signing-key.sh <id> cosign.pub` (both
   `image-signing-keys.json` files) and commits it, and adds the vendor public key from
   `zelvo-sign keygen` to `vendor-keys.json`. Release builds fail without either.

## Release builds

`docker build --build-arg ZELVO_RELEASE=true --build-arg ZELVO_OBFUSCATE=true [--build-arg ZELVO_BUILD_WATERMARK=ZW-…]`:

- **Release licensing build** (`-p:ZelvoRelease=true`): no development license, no keys from
  configuration, the vendor and image-signing public keys required (F3).
- **Obfuscation** (`build/obfuscar`): Obfuscar 2.2.50 (MIT, a local dotnet tool) over every
  `Zelvo.*.dll` of the published app. Private, internal and public member names, parameter names
  and string literals are obfuscated; what .NET finds by name stays: type names (OpenAPI schemas,
  EF Core, Elsa, log categories), properties and events (JSON, EF Core, options), enum values
  (stored as text), constructors (JSON and configuration binding), minimal API handlers (types
  with "Endpoint" in the name and four listed classes) and lambdas, middleware `Invoke`, SignalR
  hubs (`build/obfuscar/rules.xml`). Debug symbols are removed. `scripts/check-obfuscation.sh`
  (CI job "Release build checks") starts the plain and the obfuscated API and worker without the
  rest of the stack and requires identical OpenAPI documents (533 operations) and a running
  worker; a new by-name convention fails it. The hardened install test of CI runs obfuscated
  images too.
- **Minified web build**: Next.js production build without browser source maps; `*.map` files are
  removed and the pipeline checks that no image holds source maps, Zelvo symbols or the mapping.
- **Watermark** (`-p:ZelvoBuildWatermark`): compiled into every assembly (checked for its format).
  Generated files carry it hidden (`InstallationMark`, encoded, not the `ZW-` text): PDF exports
  (a comment before `%%EOF`), Excel exports (custom property `ContentId`) and emails (header
  `X-Entity-Ref-ID`). `zelvo-sign mark <file or value>` reads it back.

## Signatures and tamper detection

Every image is signed by digest (`cosign sign --key … --tlog-upload=false`) with its SBOM attached
as a signed attestation; the job checks them with the committed public key. The **release
manifest** (`zelvo-release/1`) lists each component's image, registry digest, **image id** (which
stays the same through `docker save` and `load`) and, for Zelvo's own code, the **hash of its
application files** (`find . -type f | sort | sha256sum` over `/app`, without paths it writes while
running such as `.next/cache`; `scripts/release/component.sh`), and is signed with the same key
(`cosign sign-blob`, ECDSA P-256 over SHA-256).

Installs keep the manifest next to their configuration (`release/`, or the chart's ConfigMap).
At startup:

- **api, worker, updater** (`ReleaseIntegrityCheck`) and **web** (`src/web/src/lib/release-integrity.ts`,
  in `instrumentation.ts`) check the manifest's signature with the compiled-in public key and hash
  their own files. A release build with no manifest, a foreign one or changed files **refuses to
  start** with a critical log entry; development builds log a warning.
- Each .NET component reports its result to `installation_components` (installation-wide table, no
  tenant data, rows never deleted by the application role). The API (`ReleaseComponentMonitor`)
  reads the reports every minute and logs a component that failed, runs another release or reports
  `ImageMismatch` as critical, once per change.
- The **updater** (`ImageIntegrityJob`, Compose installs) compares the image id of every running
  container with the manifest every 10 minutes and reports `images` (`ImagesVerified` or
  `ImageMismatch`).
- **Kubernetes**: the chart's optional **Kyverno** policy admits only Zelvo images signed with
  Zelvo's key (`ignoreTlog`, `ignoreSCT`), pinned to the signed digest.

Settings → License shows the release and each component's last check in English and Arabic
(`GET /api/v1/license/components`, `installation.manage`). Update packages carry the new
manifest (`release/` in the package); the updater installs it with the new version and puts the
old one back on rollback.

Limits: someone with root on the server can change both a component and its check; obfuscation,
the image checks from outside the component, the watermark and the license agreement make that
slow, visible and traceable, not impossible.

## Small installs: hardened Docker Compose

`deploy/compose/compose.production.yaml` (shipped in the bundle as `compose.yaml`, with
`.env.example` from `.env.production.example`). `install.sh` (as root, from the unpacked bundle)
checks `SHA256SUMS` and the manifest's signature, loads every image from files (Zelvo's and the
third-party ones under `images/third-party/`, so no registry is needed), copies the configuration
to `/opt/zelvo` without touching an existing `.env`, secrets or data, makes `.env` with random
passwords (mode 0600), the master key (owner the services' user, mode 0400) and RabbitMQ's users,
stops for the required settings (public host, ACME email, organisation names, mail server) and
then starts and waits until everything is healthy. Later versions come as update packages.

Hardening: release images only (`pull_policy: never`); read-only root file systems with `tmpfs`
where needed (all but RabbitMQ, OpenSearch, ClamAV and Keycloak, whose images write to their own
folders); `cap_drop: ALL` with the few capabilities an image's entrypoint needs added back
(PostgreSQL, RabbitMQ, ClamAV: changing owners and switching user; the proxy: binding 80 and 443;
the updater: managing the installation's files); `no-new-privileges`; CPU, memory and process
limits sized for the Small server (8 vCPU, 32 GB); rotated logs; only the proxy publishes ports
(80 and 443); Keycloak in production mode behind the proxy at `/auth` with its admin console not
published (`127.0.0.1:8180` for an SSH tunnel), Grafana on `127.0.0.1:3001`; **TLS 1.3 inside the
server** for PostgreSQL (`pg_hba.conf` refuses plain connections; clients `VerifyFull` against the
installation's CA), Valkey (TLS port only; `Redis:CaFile`), RabbitMQ and OpenSearch, with
certificates from the `certs` service; observability as the Compose profile `observability`
(on by default). `scripts/check-compose-images.sh` keeps the third-party image versions the same
as the development file's, which Dependabot updates.

CI job "Hardened install from a bundle" builds a test bundle (`scripts/release/test-bundle.sh`:
obfuscated images, manifest signed with a throwaway key), installs it with `install.sh` as a
customer would, checks that only ports 80 and 443 are published, that sign-in works through
`/auth` and the admin console answers 404, and runs the backup and point-in-time restore drill.

## Medium and large installs: Helm

`deploy/helm/zelvo` (see `deploy/helm/README.md`): api and web with autoscaling, worker, Keycloak
(production mode, sessions shared through the database), ClamAV, the Caddy proxy (one replica,
unprivileged ports, its certificates on a volume) and the migrate job (`post-install`,
`pre-upgrade`). PostgreSQL with pgvector, OpenSearch with the ICU plugin, RabbitMQ and Valkey are
given by address, with TLS checked against `caSecret`; `examples/` has a CloudNativePG cluster
with 35-day backups, the database roles and the Secret. Every pod meets the `restricted` Pod
Security Standard except ClamAV's; no service account tokens; PodDisruptionBudgets; network
policies admit only the proxy from outside; the license's cluster id is the `kube-system`
namespace's uid. Helm upgrades and rollbacks replace the updater; the release manifest is set with
`--set-file`. CI lints and renders the chart and checks these rules (`ci/check-rendered.sh`).

## Attachments in object storage

`Attachments:Storage=S3` (Helm `attachments.storage`): attachment content in S3-compatible storage
through the AWS SDK for .NET (Apache-2.0). The content row stays as the pointer (row-level security
still decides who reads a file), objects are written before the transaction commits under
`attachments/<tenant>/<file>`, rows stored before the switch and branding images are read from the
database, and the worker removes objects whose row is gone once a day. See `tickets.md`.

## Not in F6

- No release has run yet: the first needs the keys above. The pipeline's jobs that need them
  (push, sign, bundle) have not been tried; CI tests everything else with throwaway keys.
- Not tried on a real Kubernetes cluster, against CloudNativePG, Kyverno or a real S3 service.
- Service passwords are environment values from a root-only `.env` (Compose) or a Kubernetes
  Secret, not a secrets manager; stored secrets keep Vault/OpenBao as an option (A5).
- Kubernetes installs have no maintenance page or Settings → Updates install; they upgrade with Helm.
- Updates restart Zelvo's services and the backup service with the new images; the PostgreSQL
  container keeps running through an update (the updater backs it up from inside), so a new
  `zelvo/postgres` image applies to it when the operator restarts it (`docker compose up -d postgres`).
