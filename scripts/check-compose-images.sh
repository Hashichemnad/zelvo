#!/usr/bin/env bash
# Checks that the hardened Compose file (task F6) uses the same third-party image versions as the
# development one, which Dependabot keeps up to date: every image of compose.production.yaml that
# is not Zelvo's must appear in compose.yaml with the same tag (RabbitMQ's development image is the
# management variant of the same version).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../deploy/compose"
images() { grep -E '^\s+image:\s' "$1" | sed -E 's/^\s+image:\s*//; s/\s*#.*$//' | grep -v '^zelvo/' | sort -u; }
status=0
while read -r image; do
  name=${image%%:*} tag=${image#*:}
  dev=$(images compose.yaml | grep -E "^${name}:" | head -n 1 || true)
  dev_tag=${dev#*:}
  if [[ -z "$dev" ]]; then
    echo "compose.production.yaml uses $image, which compose.yaml does not use" >&2; status=1
  elif [[ "$dev_tag" != "$tag" && "${dev_tag/-management/}" != "$tag" ]]; then
    echo "compose.production.yaml uses $image, compose.yaml $dev" >&2; status=1
  fi
done < <(images compose.production.yaml)
[[ $status -eq 0 ]] && echo "check-compose-images: the production images match the development ones"
exit $status
