#!/usr/bin/env bash
# Task M1: the Helm chart's Caddyfile must apply the same security directives as the Compose
# Caddyfile (HSTS, ASVS V3.4.1; no TRACE or TRACK, V13.4.4). Both files mark them with
# "# zelvo:security-begin" and "# zelvo:security-end"; this compares the directives between the
# markers (comments and indentation ignored) and checks that the required ones are there.
# Run from anywhere: scripts/check-caddy-security.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
compose="$root/deploy/compose/caddy/Caddyfile"
helm="$root/deploy/helm/zelvo/files/Caddyfile"

directives() {
  local file="$1" begins ends
  begins=$(grep -c '^[[:space:]]*# zelvo:security-begin$' "$file" || true)
  ends=$(grep -c '^[[:space:]]*# zelvo:security-end$' "$file" || true)
  if [[ "$begins" != 1 || "$ends" != 1 ]]; then
    echo "$file: expected one '# zelvo:security-begin' and one '# zelvo:security-end' marker." >&2
    return 1
  fi
  sed -n '/# zelvo:security-begin$/,/# zelvo:security-end$/p' "$file" \
    | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//' | grep -v -e '^#' -e '^$'
}

compose_directives=$(directives "$compose")
helm_directives=$(directives "$helm")

required=(
  '@hsts not host localhost 127.0.0.1'
  'header @hsts Strict-Transport-Security "max-age=31536000"'
  '@trace method TRACE TRACK'
  'handle @trace {'
  'respond 405'
)
for line in "${required[@]}"; do
  if ! grep -qxF -- "$line" <<< "$compose_directives"; then
    echo "$compose: the security directives lack: $line" >&2
    exit 1
  fi
done

if [[ "$compose_directives" != "$helm_directives" ]]; then
  echo "The security directives of the two Caddyfiles differ:" >&2
  diff <(echo "$compose_directives") <(echo "$helm_directives") >&2 || true
  exit 1
fi
echo "Both Caddyfiles apply the same security directives ($(wc -l <<< "$compose_directives") lines)."
