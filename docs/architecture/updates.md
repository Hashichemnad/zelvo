# Offline update packages (module 13, task F4)

Customers update Zelvo by importing a signed update package in **Settings → Updates**. It works fully offline: a package can be downloaded on another computer and carried in on USB. Security and bug-fix releases are free; feature releases need an upgrade license (task F3, `licensing.md`).

## The package

`zelvo-1.5.0.zpkg` is a ZIP file with:

- `manifest.zelvo`: the manifest, in Zelvo's signed format (`zelvo-signed/1`, type `package-manifest`, RSA-4096 PSS SHA-256 with a vendor key; the same format and keys as licenses). The manifest (`UpdatePackageManifest`) holds the version (major.minor.patch), the type (`Security` or `Feature`), the oldest installed version it upgrades (`requiresVersion`, optional), English and Arabic release notes, the expected install time in minutes, the Docker images (`zelvo/api:1.5.0` and so on, each a `docker save` archive in `images/`) and **every other file with its size and SHA-256**. Migrations and translations are inside the images (the `migrate` step and the web app's message files).
- the files the manifest lists, and nothing else.

Any change to any file breaks the check: the hashes are in the signed manifest, and an unlisted file is refused.

`zelvo-sign package --manifest <manifest.json> --dir <folder with images/…> --key <key-id>.private.pem --out zelvo-1.5.0.zpkg` builds and signs a package on the signing computer (it fills in the files and their hashes and refuses images not tagged with the package's version). The release pipeline that saves the images comes with task F6.

## Checks before install

`UpdatePackageReader` runs them at import (API) and again in the updater before anything is changed. A failed check stops the import with a clear reason (problem codes in brackets); the file is deleted and the refusal is audited.

| Check | Problem |
| --- | --- |
| A ZIP with a Zelvo manifest | `package-format-invalid` |
| Our signature on the manifest, through `ILicenseService.VerifyVendorSignature` | `package-signature-invalid`, `package-unknown-key` |
| Every file matches its hash, none missing or unlisted, no unsafe names | `package-file-mismatch` |
| Newer than the installed version; the installed version is at least `requiresVersion` | `package-not-newer`, `package-installed-too-old` |
| The type fits the change: security keeps major.minor, a feature release changes it | `package-type-mismatch` |
| Zelvo is activated, and for a feature release `ILicenseService.IsVersionLicensed` (an upgrade license) | `package-not-licensed`, `package-upgrade-license-required` |
| The package carries every Zelvo image this installation runs (`Updates:Images`) | `package-images-missing` |

Settings → Updates shows each imported package with whether it can be installed now (the checks that depend on the installation run again, so importing an upgrade license makes a waiting feature release installable).

## Import

- **Upload** (`POST /api/v1/updates/packages`, multipart `file`): streamed to disk by the API and through the web app's proxy (never buffered in memory), up to `Updates:MaxPackageBytes` (8 GiB by default).
- **Updates inbox** for air-gapped networks and very large packages: the operator copies the file into the inbox of the updates volume (`docker compose cp zelvo-1.5.0.zpkg api:/var/lib/zelvo/updates/inbox/`); Settings → Updates lists it and imports it with the same checks (`POST /api/v1/updates/inbox/{fileName}/import`, plain file names only).

Packages are kept in `packages/` of the updates volume and recorded in `update_packages`. An administrator can remove a package that is not being installed.

## Install

`POST /api/v1/updates/packages/{id}/install` with an optional **maintenance window** (`scheduledFor`, up to 90 days ahead) and the time the **notice** starts (`noticeFrom`, default 24 hours before or now). The checks run again first. Only one install can be scheduled or running at a time (a unique index over both states). A scheduled install can be cancelled until the updater picks it up.

From `noticeFrom` every page shows a banner in the person's language, calendar and digits: the version, when it starts and how long Zelvo is unavailable (`GET /api/v1/system/maintenance`, open to everyone, also while the installation waits for activation).

### The updater service

The install is done by `src/updater/Zelvo.Updater` (Compose service `updater`). It needs the Docker socket, which is root on the host, so:

- it listens on **no port** and takes no input except installs recorded in the database by administrators with `installation.manage`;
- it **checks the package again itself** (signature, hashes, the SHA-256 recorded at import, version, license) before changing anything, so a forged record cannot make it run an image we did not sign;
- it only runs Docker Compose against its own project (`Updater:ComposeDirectory`, mounted at the same path as on the host because bind mounts resolve on the host; `scripts/dev-up.sh` writes `ZELVO_COMPOSE_DIR` into `.env`), with the project name and Compose files the stack was started with.

Every 15 seconds it records that it is there (`installation.updater_seen_at`; Settings → Updates shows "online") and takes the install whose time has come. The steps, each reported to `update_installs.steps` and to the maintenance page:

1. **verify**: the package's checks again, and that the Compose project's files are reachable;
2. **load-images**: `docker load` of each image, checking it held the image the manifest names;
3. **stop**: Zelvo's services (`opensearch`, `keycloak`, `api`, `worker`, `web`); the reverse proxy shows the maintenance page;
4. **backup**: `pg_dump --format=custom --create` of Zelvo's and Keycloak's databases (inside the PostgreSQL container, which has the matching tools), and copies of `.env` and the Compose files, under `backups/` of the updates volume (the last 3 installs are kept);
5. **switch-version**: `ZELVO_VERSION=<new>` in `.env` (every Zelvo image is tagged `zelvo/<name>:${ZELVO_VERSION}`);
6. **migrate**: `docker compose run --rm migrate` of the new version;
7. **start**: the services on the new version;
8. **health**: the API's readiness and the web app answer within 10 minutes.

**Automatic rollback:** when a step after the stop fails, the updater stops the services, **restores the databases** from the backup (`dropdb --force`, then `pg_restore --create --exit-on-error`) when the migrations had started, puts the previous `.env` back, starts the previous version and waits until it is healthy. The install ends as `RolledBack`; if the rollback itself fails it ends as `Failed`, the maintenance page stays, and the operator restores from the backup folder (named in the install). A failure before the stop (a bad package, an image that does not load) changes nothing. After a successful install the updater restarts itself on its new image, the last thing it does.

After a rollback, rebuild the search index in Settings → Search if documents changed during the attempt (the index is not part of the backup).

### Maintenance page

While Zelvo's pages are stopped the reverse proxy (Caddy) answers every request with `deploy/compose/caddy/maintenance/index.html`: in English or Arabic (the visitor's language cookie, else the browser's), its texts from `maintenance/messages.json`, showing the updater's progress from `status/status.json` (state, version and step names only; no messages) and reloading by itself once Zelvo answers again. It is reached through the reverse proxy (`https://localhost:8443` in development), not on the web app's own port.

## History and audit

Every import (`update.package.imported`, `update.package.refused` with the reason), removal, schedule, cancellation, start and outcome (`update.install.started`, `update.install.succeeded`, `update.install.rolled-back`, `update.install.failed` with the steps) is in the audit log of the operator tenant, with who did it (the updater's own entries name the administrator who scheduled the install). Settings → Updates lists the installs with their result.

## Permissions

`installation.manage` (task F3): tenant administrators of the installation's operator tenant; never for integrations.

## Data

| Table | Contents |
| --- | --- |
| `update_packages` | Imported packages: version, type, file, size, SHA-256, the manifest, status (`Imported`, `Installed`, `Removed`), who imported it. |
| `update_installs` | Installs: versions, state (`Scheduled`, `Running`, `Succeeded`, `RolledBack`, `Failed`, `Cancelled`), maintenance window, notice, steps, error, backup folder, who scheduled it. |
| `installation.updater_seen_at` | The updater's last sign of life. |

They belong to the whole installation (no tenant column or row-level security).

## Configuration

| Setting | Default | Used by |
| --- | --- | --- |
| `Updates:Directory` | `/var/lib/zelvo/updates` (the `updates` volume: `packages/`, `inbox/`, `backups/`, `status/`) | API, updater |
| `Updates:MaxPackageBytes` | 8 GiB | API |
| `Updates:Images` | `zelvo/api`, `zelvo/worker`, `zelvo/web`, `zelvo/keycloak`, `zelvo/opensearch`, `zelvo/updater` | API, updater |
| `Updater:ComposeDirectory`, `ComposeProjectName`, `ComposeFileList`, `DatabaseList`, `Services`, `HealthUrls`, `HealthTimeout`, `KeepBackups` | see `UpdaterOptions` | updater |

## Not in F4

Kubernetes installs (Helm upgrades and rollbacks), image signature checks between services and the release pipeline that builds packages come with task F6; only Docker Compose installs are updated by the updater. **Malware definition files** were to be imported "in Settings with the update packages of task F4" (B1 decision, 2026-11-22); they are not in F4: definitions change daily while packages are versioned releases, so a separate definitions import in Settings is proposed for the product owner's decision. Until then `scripts/clamav-import-definitions.sh` stays.
