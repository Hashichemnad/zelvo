#!/usr/bin/env bash
# Backup and restore drill of a hardened Compose installation (task F6; recovery point under 15
# minutes, recovery time under 4 hours). CI runs it after installing a test bundle; operators
# can run it on a test server. As root:
#
#   scripts/backup/drill.sh /opt/zelvo
#
# 1. takes a full backup; 2. writes a first marker and waits until its WAL is archived;
# 3. notes the time, writes a second marker; 4. restores to the noted time with restore.sh;
# 5. checks that the first marker is back and the second is not, and reports how long it took.
set -euo pipefail
dir=${1:-/opt/zelvo}
cd "$dir"
# shellcheck disable=SC1091
set -a; source .env; set +a

psql() { docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$POSTGRES_SUPERUSER" -d "$POSTGRES_DB" -tAc "$1"; }

echo "== Full backup"
docker compose exec -T backup /usr/local/bin/zelvo-backup.sh now full

echo "== First marker"
psql "CREATE TABLE IF NOT EXISTS zelvo_drill (marker text, at timestamptz DEFAULT now())"
psql "INSERT INTO zelvo_drill (marker) VALUES ('before')"
psql "SELECT pg_switch_wal()" > /dev/null
sleep 5
target=$(psql "SELECT to_char(now(), 'YYYY-MM-DD HH24:MI:SS.USOF')")
sleep 2
echo "== Second marker (after the restore point $target)"
psql "INSERT INTO zelvo_drill (marker) VALUES ('after')"
psql "SELECT pg_switch_wal()" > /dev/null
# archive_timeout is 60 s; wait until the archive has the segment with the target.
for _ in $(seq 1 30); do
  archived=$(psql "SELECT last_archived_time > '$target'::timestamptz FROM pg_stat_archiver" || echo f)
  [[ "$archived" == t ]] && break
  sleep 5
done
[[ "${archived:-f}" == t ]] || { echo "drill: WAL was not archived within 150 seconds" >&2; exit 1; }

echo "== Restore to $target"
started=$(date +%s)
./restore.sh time "$target"
seconds=$(( $(date +%s) - started ))

markers=$(psql "SELECT string_agg(marker, ',' ORDER BY at) FROM zelvo_drill")
psql "DROP TABLE zelvo_drill"
[[ "$markers" == before ]] || { echo "drill: after the restore the markers are '$markers', expected 'before'" >&2; exit 1; }
echo "drill: restored to the point in time in ${seconds} seconds; the later change is gone as expected."
