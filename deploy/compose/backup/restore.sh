#!/usr/bin/env bash
# Restores a hardened Zelvo installation's databases from its pgBackRest backups (task F6).
# Run it as root in the installation folder (default /opt/zelvo):
#
#   sudo ./restore.sh latest                          the newest state in the archive
#   sudo ./restore.sh time "2026-11-26 09:15:00+04"   a moment in the last 35 days
#   sudo ./restore.sh backup 20261126-013000F         one backup as it was taken
#   sudo ./restore.sh list                            the backups the repository holds
#
# It stops Zelvo's services, restores the PostgreSQL cluster (the Zelvo and Keycloak databases)
# into the data volume, starts PostgreSQL to replay the archived WAL up to the target, starts
# Zelvo again. Attachments live in the database, so they come back too; each organisation's
# search index is then rebuilt in Settings → Search. .env and secrets/master.key are not in the
# backups: keep copies of them outside the server. See docs/architecture/backup-restore.md.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
[[ $(id -u) -eq 0 ]] || { echo "restore: run as root" >&2; exit 1; }
[[ -f .env && -f compose.yaml ]] || { echo "restore: run it in the installation folder" >&2; exit 1; }

case "${1:-}" in
  list)
    docker compose run --rm --no-deps backup info
    exit 0
    ;;
  latest | time | backup) ;;
  *) sed -n '2,15p' "$0" >&2; exit 2 ;;
esac

started=$(date +%s)
echo "== Stopping Zelvo"
docker compose stop proxy web api worker updater keycloak backup postgres

echo "== Restoring PostgreSQL ($*)"
docker compose run --rm --no-deps --user postgres --entrypoint /usr/local/bin/zelvo-restore.sh postgres "$@"

echo "== Starting PostgreSQL (replays the archive up to the target)"
docker compose up --detach --wait postgres
docker compose up --detach --wait backup

echo "== Starting Zelvo"
docker compose up --detach --wait

echo "Restored in $(( ($(date +%s) - started) / 60 )) minutes."
cat <<'MSG'
Next: the search index still holds documents changed after the moment restored to. An
administrator of each organisation opens Settings → Search and chooses "Rebuild index".
MSG
