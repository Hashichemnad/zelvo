#!/usr/bin/env bash
# Starts the whole Zelvo development stack with one command.
# First run: creates deploy/compose/.env from .env.example with random passwords.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_dir="$root/deploy/compose"
env_file="$compose_dir/.env"

if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  echo "Docker with the Compose plugin is required: https://docs.docker.com/get-docker/" >&2
  exit 1
fi

random_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 24
  else
    head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'
  fi
}

if [[ ! -f "$env_file" ]]; then
  echo "Creating deploy/compose/.env with random development passwords"
  umask 077
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == *=change-me ]]; then
      printf '%s%s\n' "${line%change-me}" "$(random_secret)"
    else
      printf '%s\n' "$line"
    fi
  done < "$compose_dir/.env.example" > "$env_file"
fi

# Settings added to .env.example after .env was created get their own random value.
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ "$line" =~ ^([A-Z0-9_]+)= ]] || continue
  key="${BASH_REMATCH[1]}"
  if ! grep -q "^${key}=" "$env_file"; then
    echo "Adding ${key} to deploy/compose/.env"
    if [[ "$line" == *=change-me ]]; then
      printf '%s=%s\n' "$key" "$(random_secret)" >> "$env_file"
    else
      printf '%s\n' "$line" >> "$env_file"
    fi
  fi
done < "$compose_dir/.env.example"

# Run from the compose directory so a local compose.override.yaml (git-ignored) is picked up.
cd "$compose_dir"
docker compose up --build --detach --wait --wait-timeout "${ZELVO_WAIT_TIMEOUT:-900}" "$@"

# shellcheck disable=SC1090
set -a; source "$env_file"; set +a
cat <<MSG

Zelvo is running:
  Welcome page        http://localhost:${WEB_PORT:-3000}
  API readiness       http://localhost:${API_PORT:-8080}/health/ready
  OpenAPI document    http://localhost:${API_PORT:-8080}/openapi/v1.json
  Keycloak admin      http://localhost:${KEYCLOAK_PORT:-8180}  (credentials in deploy/compose/.env)
  Sign-in emails      http://localhost:${MAILPIT_PORT:-8025}  (development mail catcher)
  Development users   requester@zelvo.test, agent@, lead@, approver@, admin@, executive@
                      password: ZELVO_DEV_USERS_PASSWORD in deploy/compose/.env
  RabbitMQ admin      http://localhost:${RABBITMQ_MANAGEMENT_PORT:-15672}

Stop it with: make down
MSG
