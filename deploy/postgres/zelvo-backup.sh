#!/usr/bin/env bash
# Zelvo's backup service (task F6): runs next to PostgreSQL with the same data, socket and
# repository volumes. Creates the pgBackRest stanza once, checks that WAL archiving works, then
# takes a full backup every Sunday and a differential backup every other day at
# ZELVO_BACKUP_TIME (HH:MM, the container's time zone, default 01:30), removing what is older
# than the retention (35 days). Writes /var/lib/pgbackrest/zelvo-status.json for monitoring.
#
#   zelvo-backup.sh            run the schedule (the backup service)
#   zelvo-backup.sh now [full|diff|incr]   take one backup now
#   zelvo-backup.sh info       list the backups
set -euo pipefail

stanza=zelvo
status=/var/lib/pgbackrest/zelvo-status.json

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

write_status() {
  printf '{"time":"%s","result":"%s","type":"%s"}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" "$2" > "$status.new"
  mv "$status.new" "$status"
}

wait_for_postgres() {
  until pg_isready -h /var/run/postgresql -U "${POSTGRES_USER:-postgres}" -d postgres -q; do sleep 5; done
}

ensure_stanza() {
  wait_for_postgres
  zelvo-pgbackrest --stanza="$stanza" --log-level-console=warn stanza-create
  zelvo-pgbackrest --stanza="$stanza" --log-level-console=warn check
}

backup() {
  local type=$1
  log "Starting a $type backup"
  if zelvo-pgbackrest --stanza="$stanza" --type="$type" backup; then
    write_status ok "$type"
    log "The $type backup finished"
  else
    write_status failed "$type"
    log "The $type backup failed"
    return 1
  fi
}

case "${1:-schedule}" in
  now)
    ensure_stanza
    backup "${2:-full}"
    ;;
  info)
    zelvo-pgbackrest --stanza="$stanza" info
    ;;
  schedule)
    ensure_stanza
    # The first backup is a full one as soon as the service starts.
    if ! zelvo-pgbackrest --stanza="$stanza" --output=json info | grep -q '"label"'; then
      backup full || true
    fi
    at=${ZELVO_BACKUP_TIME:-01:30}
    while true; do
      now=$(date +%s)
      next=$(date -d "today $at" +%s)
      (( next <= now )) && next=$(date -d "tomorrow $at" +%s)
      log "Next backup at $(date -d "@$next" '+%Y-%m-%d %H:%M %Z')"
      sleep $(( next - now ))
      if [[ $(date +%u) == 7 ]]; then backup full || true; else backup diff || true; fi
    done
    ;;
  *)
    echo "usage: $0 [schedule | now [full|diff|incr] | info]" >&2
    exit 2
    ;;
esac
