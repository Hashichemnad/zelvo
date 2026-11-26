# Backup and restore

Task F6 (NFR "Backup and recovery": daily backups kept 35 days, recovery point under 15 minutes,
recovery time under 4 hours). Product owner decision 2026-11-26: **pgBackRest** (MIT) for Docker
Compose installs and **CloudNativePG** (Apache-2.0) or the customer's managed PostgreSQL for
Kubernetes installs.

## What has to be backed up

| Data | Where | How |
| --- | --- | --- |
| Tickets, settings, the audit log, licenses, **attachments** (in PostgreSQL by default) | Zelvo's database | pgBackRest / CloudNativePG |
| Users, realms, sign-in settings | Keycloak's database (same PostgreSQL cluster) | the same backup |
| Attachments in S3-compatible storage (when chosen) | the bucket | the storage's own versioning or replication |
| **`.env`** (passwords) and **`secrets/master.key`** (decrypts the stored secrets) | the installation folder | **copies kept outside the server** by the operator, once and after changes; never in the backups |
| Search indexes | OpenSearch | not backed up: rebuilt from the database (Settings → Search) |
| Queues, cache, sessions | RabbitMQ, Valkey | not backed up: refilled from the database's outboxes; people sign in again |
| Proxy certificates | `caddy-data` volume | not needed: requested again |

Without the master key the stored secrets (mailbox, AI, payment and connector credentials) cannot
be read; with Vault or OpenBao the key stays there.

## Docker Compose installs

`zelvo/postgres` (`deploy/postgres`) is the pgvector image with pgBackRest.

- **Continuous WAL archiving**: PostgreSQL archives every WAL segment to the repository
  (`archive_command`, asynchronous with a 4 GiB queue), and `archive_timeout=60` closes a segment
  at least every minute. At most about a minute of changes is not yet in the repository, well
  within the 15-minute recovery point.
- **Base backups** by the `backup` service: a full backup on Sundays and a differential one on the
  other days at `ZELVO_BACKUP_TIME` (default 01:30, `ZELVO_TIME_ZONE`), and a full one when the
  service first starts. Retention: the full backups (with their differential backups and WAL)
  needed to restore **any moment of the last 35 days** (`repo1-retention-full-type=time`).
  `/var/lib/pgbackrest/zelvo-status.json` has the last result.
- **The repository** is the `backup-repo` volume: put it on another disk or mount network
  storage, or use S3-compatible storage (`ZELVO_BACKUP_REPO_TYPE=s3` and the bucket settings).
  It is **encrypted** (AES-256-CBC) with `ZELVO_BACKUP_CIPHER_PASS`, which `install.sh` makes and
  which must be kept with `.env`.
- Commands, in the installation folder:

```bash
sudo docker compose exec backup zelvo-backup.sh info
```

```bash
sudo docker compose exec backup zelvo-backup.sh now full
```

### Restore

`restore.sh` (as root, in the installation folder) stops Zelvo, restores the cluster with
`pgbackrest restore --delta`, starts PostgreSQL, which replays the archived WAL up to the target and
then accepts writes, and starts Zelvo again:

```bash
sudo ./restore.sh latest
```

```bash
sudo ./restore.sh time "2026-11-26 09:15:00+04"
```

```bash
sudo ./restore.sh backup 20261126-013000F
```

Then an administrator of each organisation rebuilds the search index (Settings → Search). On a
**new server**: install the same version from its bundle with `install.sh --no-start`, put the
saved `.env` and `secrets/master.key` in place, give the new server the repository (the backup
volume's files or the same S3 bucket), run `restore.sh latest`, and import a license for the new
servers (the fingerprint changed, module 12).

### Recovery time

The time is the restore of the newest full backup plus the replay of WAL since then: about the
database's size divided by the disk speed, plus minutes. A Small install (500 GB disk, up to 50
agents) restores well within 4 hours; operators measure their own with the drill below.

### Drill

`scripts/backup/drill.sh /opt/zelvo` (root) takes a full backup, writes a marker, notes the time,
writes a second marker, restores to the noted time and checks that only the first marker is back,
reporting how long the restore took. CI runs it on every pull request against a hardened install
made from a test bundle (job "Hardened install from a bundle, backup and restore drill").
Operators run it on a test server after installing and after each upgrade.

## Kubernetes installs

PostgreSQL is run by CloudNativePG or is the customer's managed service; the chart connects to it.
`deploy/helm/zelvo/examples/cloudnative-pg.yaml` is a cluster with a replica, continuous WAL
archiving and a daily base backup to S3-compatible object storage kept 35 days, encrypted. Restore
to a moment by creating a new `Cluster` that bootstraps from the backup with a `recoveryTarget`
(`targetTime`), then point `postgres.host` at it. Managed services use their own point-in-time
restore with at least 35 days of retention.

## Not in F6

- pgBackRest has been tested by CI's drill, not on a customer's own storage; S3 repositories and
  CloudNativePG recovery have not been tried.
- No Settings screen for backups: they are an operator task on the server; the backup service's
  status file and logs (Loki) show the results. Alerts on a failed backup come with the
  observability review in F7.
