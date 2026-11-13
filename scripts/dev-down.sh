#!/usr/bin/env bash
# Stops the development stack. Pass --volumes to also delete its data.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_dir="$root/deploy/compose"

cd "$compose_dir"
docker compose down "$@"
