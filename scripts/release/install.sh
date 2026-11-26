#!/usr/bin/env bash
# Installs Zelvo from its offline install bundle on one server (small installs, Docker Compose;
# task F6). Run it as root from the unpacked bundle:
#
#   sudo ./install.sh [--dir /opt/zelvo] [--no-start]
#
# 1. checks the bundle's files (SHA256SUMS) and the release manifest's signature (cosign.pub);
# 2. loads the images (Zelvo's and the third-party ones) into Docker, from files, without a registry;
# 3. puts the Compose configuration in the installation folder (default /opt/zelvo) without
#    touching an existing .env, secrets or data;
# 4. makes .env with random passwords, the master key and RabbitMQ's users the first time;
# 5. stops if a required setting is still empty, otherwise starts Zelvo and waits until it is healthy.
#
# Later versions come as signed update packages through Settings → Updates; running install.sh
# of the installed version again repairs the configuration files.
set -euo pipefail

bundle=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
target=/opt/zelvo
start=true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dir) target=$2; shift 2 ;;
    --no-start) start=false; shift ;;
    *) echo "usage: $0 [--dir <folder>] [--no-start]" >&2; exit 2 ;;
  esac
done

say() { printf '\n== %s\n' "$*"; }
fail() { echo "install: $*" >&2; exit 1; }

[[ $(id -u) -eq 0 ]] || fail "run as root (sudo ./install.sh): the master key and .env are made readable by root and the services only"
command -v docker > /dev/null && docker compose version > /dev/null 2>&1 || fail "Docker Engine with the Compose plugin is required"
[[ -f /etc/machine-id ]] || fail "/etc/machine-id is missing: it is part of the license fingerprint"

version=$(jq -r .version "$bundle/release/release-manifest.json" 2>/dev/null) || fail "release/release-manifest.json is missing or unreadable"

say "Checking the bundle (release $version)"
(cd "$bundle" && sha256sum --quiet -c SHA256SUMS) || fail "files of the bundle differ from SHA256SUMS: get the bundle again"
if ! openssl dgst -sha256 -verify "$bundle/cosign.pub" \
    -signature <(base64 -d "$bundle/release/release-manifest.json.sig") "$bundle/release/release-manifest.json" > /dev/null; then
  fail "the release manifest is not signed by Zelvo"
fi
echo "The release manifest is signed by Zelvo."

if [[ -f "$target/.env" ]]; then
  installed=$(grep -E '^ZELVO_VERSION=' "$target/.env" | cut -d= -f2-)
  if [[ -n "$installed" && "$installed" != "$version" ]]; then
    fail "this server runs Zelvo $installed; install $version with its update package in Settings → Updates"
  fi
fi

say "Loading the images"
for archive in "$bundle"/images/*.tar.gz "$bundle"/images/third-party/*.tar.gz; do
  [[ -f "$archive" ]] || continue
  docker load --input "$archive" | sed 's/^/  /'
done

say "Configuration in $target"
mkdir -p "$target"
chmod 0750 "$target"
cp "$bundle/compose/compose.yaml" "$target/compose.yaml"
cp "$bundle/compose/.env.example" "$target/.env.example"
for folder in caddy security postgres observability; do
  rm -rf "${target:?}/$folder"
  cp -r "$bundle/compose/$folder" "$target/$folder"
done
cp "$bundle/compose/restore.sh" "$bundle/compose/clamav-import-definitions.sh" "$target/"
mkdir -p "$target/release"
cp "$bundle/release/release-manifest.json" "$bundle/release/release-manifest.json.sig" "$target/release/"
chmod 0644 "$target/release/"*

random_secret() { openssl rand -hex 24; }
env_file="$target/.env"
umask 077
if [[ ! -f "$env_file" ]]; then
  echo "Making .env with random passwords"
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == *=change-me ]]; then printf '%s%s\n' "${line%change-me}" "$(random_secret)"; else printf '%s\n' "$line"; fi
  done < "$target/.env.example" > "$env_file"
fi
# Settings added in a later .env.example get their default (or a random value).
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ "$line" =~ ^([A-Z0-9_]+)= ]] || continue
  key="${BASH_REMATCH[1]}"
  grep -q "^${key}=" "$env_file" && continue
  if [[ "$line" == *=change-me ]]; then printf '%s=%s\n' "$key" "$(random_secret)" >> "$env_file"; else printf '%s\n' "$line" >> "$env_file"; fi
done < "$target/.env.example"
set_env() { sed -i "s#^$1=.*#$1=$2#" "$env_file"; }
set_env ZELVO_VERSION "$version"
set_env ZELVO_COMPOSE_DIR "$target"
chmod 0600 "$env_file"

mkdir -p "$target/secrets"
chmod 0755 "$target/secrets"
if [[ ! -f "$target/secrets/master.key" ]]; then
  echo "Making the master key (secrets/master.key): keep a copy outside this server"
  openssl rand -base64 32 > "$target/secrets/master.key"
fi
# The API and worker run as the .NET images' user (1654); nobody else may read the key.
chown 1654:1654 "$target/secrets/master.key"
chmod 0400 "$target/secrets/master.key"

# shellcheck disable=SC1090
set -a; source "$env_file"; set +a
umask 022
cat > "$target/secrets/rabbitmq-definitions.json" <<JSON
{
  "users": [
    { "name": "${RABBITMQ_USER}", "password": "${RABBITMQ_PASSWORD}", "tags": ["administrator"] },
    { "name": "zelvo-api", "password": "${RABBITMQ_API_PASSWORD}", "tags": [] },
    { "name": "zelvo-worker", "password": "${RABBITMQ_WORKER_PASSWORD}", "tags": [] }
  ],
  "vhosts": [{ "name": "zelvo" }],
  "permissions": [
    { "user": "${RABBITMQ_USER}", "vhost": "zelvo", "configure": ".*", "write": ".*", "read": ".*" },
    { "user": "zelvo-api", "vhost": "zelvo", "configure": "^\$", "write": "^\$", "read": "^\$" },
    { "user": "zelvo-worker", "vhost": "zelvo", "configure": "^zelvo\\\\..*", "write": "^zelvo\\\\..*", "read": "^zelvo\\\\..*" }
  ]
}
JSON
# RabbitMQ reads it as its own user; the installation folder (0750, root) keeps others out.
chown 0:0 "$target/secrets/rabbitmq-definitions.json"
chmod 0444 "$target/secrets/rabbitmq-definitions.json"

missing=()
for key in ZELVO_PUBLIC_HOST ZELVO_ACME_EMAIL ZELVO_DEFAULT_TENANT_NAME_EN ZELVO_DEFAULT_TENANT_NAME_AR ZELVO_SMTP_HOST ZELVO_SMTP_FROM; do
  [[ -n "${!key:-}" ]] || missing+=("$key")
done
if [[ ${#missing[@]} -gt 0 ]]; then
  say "Fill in $env_file, then run this again"
  printf '  %s\n' "${missing[@]}"
  exit 3
fi

(cd "$target" && docker compose config --quiet) || fail "the Compose configuration is not valid"
if [[ "$start" != true ]]; then
  say "Ready. Start Zelvo with: cd $target && docker compose up --detach --wait"
  exit 0
fi

say "Starting Zelvo $version"
(cd "$target" && docker compose up --detach --wait --wait-timeout "${ZELVO_WAIT_TIMEOUT:-1200}")
cat <<MSG

Zelvo $version is running at https://${ZELVO_PUBLIC_HOST}/
Next: sign in as the administrator, open Settings → License, download the activation request,
send it to Zelvo and import the license file you receive. Keep copies of $target/.env and
$target/secrets/master.key outside this server.
MSG
