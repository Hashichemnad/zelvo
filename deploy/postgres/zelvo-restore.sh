#!/usr/bin/env bash
# Restores the PostgreSQL cluster from pgBackRest (task F6). Run by scripts/backup/restore.sh in
# a one-off container with the data and repository volumes while PostgreSQL is stopped:
#
#   zelvo-restore.sh latest                    the newest state the archive holds
#   zelvo-restore.sh time "2026-11-26 09:15:00+04"   a moment in the last 35 days
#   zelvo-restore.sh backup <label>            a backup as it was taken
#
# The restored server replays WAL from the repository up to the target and then opens for writes.
set -euo pipefail

stanza=zelvo
case "${1:-}" in
  latest) target=() ;;
  time) target=(--type=time "--target=${2:?give the time}" --target-action=promote) ;;
  backup) target=(--set="${2:?give the backup label}" --type=immediate --target-action=promote) ;;
  *) echo "usage: $0 latest | time <timestamp> | backup <label>" >&2; exit 2 ;;
esac

zelvo-pgbackrest --stanza="$stanza" info
# The restored server fetches WAL through the same wrapper as archiving, which drops the empty
# PGBACKREST_* settings Compose passes (pgBackRest's own restore_command would refuse them).
zelvo-pgbackrest --stanza="$stanza" --delta "${target[@]}" \
  --recovery-option="restore_command=zelvo-pgbackrest --stanza=$stanza archive-get %f \"%p\"" restore
echo "Restored. Start PostgreSQL: it replays the archive and then accepts connections."
