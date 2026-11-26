# syntax=docker/dockerfile:1
# Zelvo's PostgreSQL image (task F6): the pgvector image with pgBackRest (MIT) for continuous WAL
# archiving, daily backups kept 35 days and point-in-time restore (product owner decision
# 2026-11-26; recovery point under 15 minutes, recovery time under 4 hours). Used by the hardened
# Compose installs (postgres and backup services); Kubernetes installs use CloudNativePG or the
# customer's managed PostgreSQL. See docs/architecture/backup-restore.md.
FROM pgvector/pgvector:pg17

# pgBackRest from the PostgreSQL project's apt repository, which the image already uses.
RUN apt-get update \
    && apt-get install -y --no-install-recommends pgbackrest \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/pgbackrest /var/log/pgbackrest /var/spool/pgbackrest /etc/pgbackrest/conf.d \
    && chown -R postgres:postgres /var/lib/pgbackrest /var/log/pgbackrest /var/spool/pgbackrest \
    && pgbackrest version

COPY pgbackrest.conf /etc/pgbackrest/pgbackrest.conf
COPY zelvo-pgbackrest zelvo-backup.sh zelvo-restore.sh /usr/local/bin/
RUN chmod 0755 /usr/local/bin/zelvo-pgbackrest /usr/local/bin/zelvo-backup.sh /usr/local/bin/zelvo-restore.sh
