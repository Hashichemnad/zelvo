#!/usr/bin/env bash
# Stops the development stack. Pass --volumes to also delete its data.
# ZELVO_PROFILE=demo (make demo-down) stops the demo installation instead.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_dir="$root/deploy/compose"

cd "$compose_dir"
if [[ "${ZELVO_PROFILE:-}" == "demo" ]]; then
  export COMPOSE_PROJECT_NAME=zelvo-demo COMPOSE_FILE=compose.yaml:compose.demo.yaml
fi
docker compose down "$@"
