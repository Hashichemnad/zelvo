# Offline update packages (module 13, task F4)

Customers update Zelvo by importing a signed update package in **Settings → Updates**. It works fully offline: a package can be downloaded on another computer and carried in on USB. Security and bug-fix releases are free; feature releases need an upgrade license (task F3, `licensing.md`).

## The package

`zelvo-1.5.0.zpkg` is a ZIP file with:

- `manifest.zelvo`: the manifest, in Zelvo's signed format (`zelvo-signed/1`, type `package-manifest`, RSA-4096 PSS SHA-256 with a vendor key; the same format and keys as licenses). The manifest (`UpdatePackageManifest`) holds the version (major.minor.patch), the type (`Security` or `Feature`), the oldest installed version it upgrades (`requiresVersion`, optional), English and Arabic release notes, the expected install time in minutes, the Docker images (`zelvo/api:1.5.0` and so on, each a `docker save` archive in `images/`) and **every other file with its size and SHA-256**. Migrations and translations are inside the images (the `migrate` step and the web app's message files).
- the files the manifest lists, and nothing else.

Any change to any file breaks the check: the hashes are in the signed manifest, and an unlisted file is refused.

`zelvo-sign package --manifest <manifest.json> --dir <folder with images/…> --key <key-id>.private.pem --out zelvo-1.5.0.zpkg` builds and signs a package on the signing computer (it fills in the files and their hashes and refuses images not tagged with the package's version). The release pipeline (task F6, `release.md`) builds the install bundle with the images saved as files, the signed release manifest in `release/` and a `package-manifest.json`; the product owner signs the package from the unpacked bundle. A package that carries `release/release-manifest.json` and its `.sig` has them installed with the new version (and put back on rollback), so the new services pass their release integrity check.

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
6. **migrate**: `docker compose run --rm --pull never migrate` of the new version (images only ever come from the package, never from a registry);
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

Kubernetes installs upgrade and roll back with Helm (task F6, `deploy/helm/README.md`); only Docker Compose installs are updated by the updater, which also checks the running containers' images against the release manifest (`release.md`). Malware definition files are not update packages (they change daily while packages are versioned releases); they have their own page, below (task M1).

## Malware definitions (task M1)

**Settings → Updates → Malware definitions** (`/settings/updates/malware-definitions`, `installation.manage`, the operator tenant's administrators; approved 2026-11-26) keeps ClamAV's definitions current, also on installations without internet access. Zelvo is **ClamAV's private mirror**: freshclam in the unmodified ClamAV image reads `PrivateMirror http://api:8080/internal/clamav` (Compose `FRESHCLAM_CONF_PrivateMirror`; on Kubernetes the chart's api service, with a network policy that lets ClamAV reach the API) every 30 minutes (`FRESHCLAM_CHECKS=48`, freshclam's limit is 50 a day). No Docker socket is needed, so Compose and Kubernetes work the same. `/internal/clamav/…` is mapped outside `/api`, is not in the OpenAPI document and is never routed by the proxy or the web app's API proxy; the files it serves are ClamAV's public definitions.

### The screen

- **Status:** whether malware scanning is on, the definitions ClamAV has loaded (clamd's `VERSION`: the daily version and its build date, in the person's calendar and digits), the last update (imported by whom and when, or loaded from elsewhere) and when the scanner was last asked; a red warning when the loaded definitions are older than the warning period.
- **Import:** a file picker for several files at once, or files the operator copied into the updates inbox (`docker compose cp daily.cvd api:/var/lib/zelvo/updates/inbox/`). Each file is listed with its type, version and date before it is sent (read from its header in the browser; the inbox list is read by the API) and "Import" is enabled only when every file passed; the API checks everything again and imports **all or nothing**, answering 422 with each file's outcome (`import`, `same` when Zelvo already serves that version, `refused` with its reason).
- **Files Zelvo gives ClamAV**, **Settings** (the warning period, 1–90 days, default 7; "Download definitions automatically") and **History** (the last 20 imports, refusals and versions ClamAV loaded, with who imported them).
- Settings shows a banner while the definitions are out of date, and the operator tenant's holders of `installation.manage` get the notification `malware.definitions-outdated` (in the app and by email, English and formal Arabic) once per daily version that becomes too old.

### Checks

| Check | Problem |
| --- | --- |
| A definition file's name: `main`, `daily` or `bytecode` `.cvd` or `.cld`, or `<database>-<version>.cvd.sign` (ClamAV 1.5's separate signature, optional) | `definitions-not-clamav` |
| A ClamAV database header (`ClamAV-VDB:…`, 512 bytes); a signature file starts with `#clamsign-` | `definitions-not-clamav` |
| A `.cvd`'s content matches the MD5 its header publishes (damaged or cut-off copies) | `definitions-damaged` |
| At most `MalwareDefinitions:MaxFileBytes` (256 MiB; main.cvd is about 90 MB), 64 KiB for signature files, 10 files per import | `definitions-too-large` |
| Each database once per import | `definitions-duplicate` |
| Newer than the version Zelvo serves, and the daily database not older than the one ClamAV has loaded (the same version is left out) | `definitions-older` |
| A signature file belongs to a `.cvd` of this import or the one served | `definitions-sign-unmatched` |
| The first import brings main, daily and bytecode (ClamAV cannot start from nothing without all three) | `definitions-incomplete` |

The **Talos signature** is checked by ClamAV itself: freshclam tests every file it downloads and refuses one that is not signed by ClamAV's publisher, so a file that passes Zelvo's checks but was not made by Talos is never loaded (the history then shows no "loaded" entry). `.cdiff` files are not imported: freshclam takes the version to fetch from the mirror's `daily.cvd` or `daily.cld` header, so incremental updates alone would never be used; freshclam on a computer with internet access writes up-to-date `.cld` files, and ClamAV's cvdupdate tool full `.cvd` files, which are imported instead.

### The mirror

freshclam asks for `daily.cld`, then `daily.cvd` (first its 512-byte header with `Range: bytes=0-511`, then the file), with the loaded file's build time as `If-Modified-Since`, and in ClamAV 1.5 for `daily-<version>.cvd.sign`; the same for main and bytecode. Zelvo answers (`DefinitionMirror.Decide`, checked with ClamAV 1.5.5's freshclam):

| Request | Answer |
| --- | --- |
| An imported file newer than ClamAV's, under its own name (`.cld` or `.cvd`) | 200, or 206 for a range (streamed from PostgreSQL), `Last-Modified` its build time |
| The other name of that database | 404, so freshclam asks for the right one |
| Nothing newer, "Download definitions automatically" on | 302 to `MalwareDefinitions:UpstreamMirror` (`https://database.clamav.net`) with the same file name, incremental `.cdiff` files too |
| Nothing newer, automatic downloads off | 304 for the `.cvd` (freshclam: "up to date"; it asks nobody else), 404 for a `.cld` Zelvo does not have |
| Anything else | 404 |

So with automatic downloads off ClamAV reads only the imported files; with them on, imported files still win while they are newer, and ClamAV's servers are used otherwise. Files an operator copies into ClamAV's own folder with `scripts/clamav-import-definitions.sh` (kept for operators) are replaced only by newer ones. A ClamAV container that starts without any definitions (a new Kubernetes pod, whose definitions are on an `emptyDir`) needs either imported files or automatic downloads; ClamAV's own image brings definitions on Compose.

### Who records what

- The **worker** (the only service that reaches clamd) asks clamd's `VERSION` every 5 minutes (`AttachmentScanning:StatusInterval`) and records the answer, or why it could not ask, on `installation` (`ClamAvStatusJob`).
- The **API** (`MalwareDefinitionJobs`, every minute; `MalwareDefinitions:JobsEnabled`, `JobInterval`) records a daily version ClamAV newly loaded in the history ("loaded" when it was imported, "downloaded" otherwise, which includes the definitions shipped in ClamAV's image) and in the operator tenant's audit log (`malware.definitions.loaded`), and sends the outdated warning (`malware.definitions.outdated` in the audit log). Each is claimed by a single `UPDATE … RETURNING`, so several API instances never record or send one twice.
- Imports (`malware.definitions.imported`, with each file's SHA-256, version and outcome), refusals (`malware.definitions.refused`, with the reasons) and settings changes (`malware.definitions.settings-changed`) are in the audit log of the operator tenant, with who did it.

### Data

| Table | Contents |
| --- | --- |
| `malware_definition_files` | Imported files: database, kind (`cvd`, `cld`, `sign`), version, build time, name, size, SHA-256, who imported them and how (`Upload`, `Inbox`). Only the current file of each database (and its signature file) keeps its **content** (`bytea`, so every API instance serves the same files and backups include them); replaced files keep their record. |
| `malware_definition_events` | The history: `Imported`, `Refused`, `Loaded`, `Downloaded`, with the versions. |
| `installation` columns | What ClamAV loaded (`clamav_engine`, `clamav_daily_version`, `clamav_daily_built_at`, `clamav_checked_at`, `clamav_error`), the warning period and automatic downloads, and the versions already recorded and warned about. |

Like the update tables they belong to the whole installation (no tenant column or row-level security); the application role may add files and history but never delete either (migration `20261126110000_MalwareDefinitions`). Uploads are streamed to `staging/` on the updates volume (the web app's proxy streams them too) and removed after the import.

### Configuration

| Setting | Default | Used by |
| --- | --- | --- |
| `MalwareDefinitions:MaxFileBytes` | 256 MiB | API |
| `MalwareDefinitions:UpstreamMirror` | `https://database.clamav.net` (must be HTTPS) | API |
| `MalwareDefinitions:JobInterval`, `JobsEnabled` | 1 minute, on | API |
| `AttachmentScanning:StatusInterval` | 5 minutes | worker |
| `FRESHCLAM_CONF_PrivateMirror`, `FRESHCLAM_CHECKS` | the API's internal address, 48 | ClamAV (Compose, Helm) |

`CLAMAV_NO_FRESHCLAMD` is no longer used: freshclam always runs, because it is what reads Zelvo's mirror; "Download definitions automatically" off replaces it for offline installations.

Not tried against ClamAV's own servers from here (the build environment has no access to them); the redirect, ranges, If-Modified-Since and fallback behaviour were checked against ClamAV 1.5.5's freshclam with local mirrors.
