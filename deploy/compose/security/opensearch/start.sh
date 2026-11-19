#!/bin/bash
# Starts OpenSearch with the security plugin on (A5): TLS on HTTP and transport with the
# development certificates, and Zelvo's users and roles. Passwords come from the environment and
# are stored only as bcrypt hashes. The security index is created from these files on the first
# start; afterwards password or role changes need `make reset` (or the plugin's securityadmin tool).
set -euo pipefail

config=/usr/share/opensearch/config/opensearch-security
tools=/usr/share/opensearch/plugins/opensearch-security/tools

hash() {
  OPENSEARCH_JAVA_OPTS="-Xms32m -Xmx64m" "$tools/hash.sh" -p "$1" | tail -n 1
}

cat > "$config/internal_users.yml" <<USERS
_meta:
  type: internalusers
  config_version: 2
admin:
  hash: "$(hash "$OPENSEARCH_ADMIN_PASSWORD")"
  reserved: true
  description: Operators (all access)
zelvo-api:
  hash: "$(hash "$OPENSEARCH_API_PASSWORD")"
  description: Zelvo API (searches and reads index status)
zelvo-worker:
  hash: "$(hash "$OPENSEARCH_WORKER_PASSWORD")"
  description: Zelvo worker (creates and writes tenants' search indices)
USERS

cp /zelvo-security/roles.yml /zelvo-security/roles_mapping.yml "$config/"
exec /usr/share/opensearch/opensearch-docker-entrypoint.sh opensearch
