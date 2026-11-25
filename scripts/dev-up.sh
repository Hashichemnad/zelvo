#!/usr/bin/env bash
# Starts the whole Zelvo development stack with one command.
# First run: creates deploy/compose/.env from .env.example with random passwords.
# ZELVO_PROFILE=demo (make demo) starts the demo installation instead: its own Compose project
# (zelvo-demo) with deploy/compose/compose.demo.yaml, so its data never mixes with development data.
# ZELVO_PROFILE=test (make test-installation) starts the test installation (module 14) the same
# way: Compose project zelvo-test with deploy/compose/compose.test.yaml.
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

# Task F4: the updater runs Docker Compose on this project from inside its container, where bind
# mounts resolve on the host, so it needs this directory's absolute path.
abs_compose_dir="$(cd "$compose_dir" && pwd)"
if grep -q '^ZELVO_COMPOSE_DIR=' "$env_file"; then
  sed -i.bak "s#^ZELVO_COMPOSE_DIR=.*#ZELVO_COMPOSE_DIR=${abs_compose_dir}#" "$env_file" && rm -f "$env_file.bak"
else
  printf 'ZELVO_COMPOSE_DIR=%s\n' "$abs_compose_dir" >> "$env_file"
fi

# Files the containers read as Compose secrets (git-ignored, created once).
secrets_dir="$compose_dir/secrets"
mkdir -p "$secrets_dir"

# Master key that protects secrets stored in the database (A5, docs/architecture/secrets-encryption.md).
# Development only: readable by the containers' non-root users. Deleting it makes stored secrets
# unreadable; `make reset` keeps it.
master_key="$secrets_dir/master.key"
if [[ ! -f "$master_key" ]]; then
  echo "Creating deploy/compose/secrets/master.key (development master key)"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 32 > "$master_key"
  else
    head -c 32 /dev/urandom | base64 > "$master_key"
  fi
  chmod 644 "$master_key"
fi

# RabbitMQ users, permissions and the "zelvo" virtual host (A5), from the passwords in .env.
# Each service has its own user: the API may only connect (health), the worker may use queues and
# exchanges named zelvo.* only. Rewritten on every start so it follows .env.
# shellcheck disable=SC1090
set -a; source "$env_file"; set +a
umask 022
cat > "$secrets_dir/rabbitmq-definitions.json" <<JSON
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

# Run from the compose directory so a local compose.override.yaml (git-ignored) is picked up.
cd "$compose_dir"
if [[ "${ZELVO_PROFILE:-}" == "demo" ]]; then
  export COMPOSE_PROJECT_NAME=zelvo-demo COMPOSE_FILE=compose.yaml:compose.demo.yaml
elif [[ "${ZELVO_PROFILE:-}" == "test" ]]; then
  export COMPOSE_PROJECT_NAME=zelvo-test COMPOSE_FILE=compose.yaml:compose.test.yaml
fi
docker compose up --build --detach --wait --wait-timeout "${ZELVO_WAIT_TIMEOUT:-900}" "$@"

# shellcheck disable=SC1090
set -a; source "$env_file"; set +a
if [[ "${ZELVO_PROFILE:-}" == "demo" ]]; then
  cat <<MSG

The Zelvo demo is running:
  Demo                http://localhost:${WEB_PORT:-3000}  (open "Demo" under the header to sign in
                      as a demo user, switch organisation or reset the demo)
  Sample organisations demo-municipality (government entity), demo-company (private company)
  API readiness       http://localhost:${API_PORT:-8080}/health/ready

Stop it with: make demo-down   (make demo-wipe also deletes its data)
MSG
  exit 0
fi

cat <<MSG

Zelvo is running:
  Welcome page        http://localhost:${WEB_PORT:-3000}
  API readiness       http://localhost:${API_PORT:-8080}/health/ready
  OpenAPI document    http://localhost:${API_PORT:-8080}/openapi/v1.json
  Keycloak admin      http://localhost:${KEYCLOAK_PORT:-8180}  (credentials in deploy/compose/.env)
  Sign-in emails      http://localhost:${MAILPIT_PORT:-8025}  (development mail catcher)
  UAE PASS mock       http://localhost:${UAEPASS_MOCK_PORT:-8095}  (Settings → Sign-in → UAE PASS: Development mock,
                      client ID zelvo-mock, secret UAEPASS_MOCK_CLIENT_SECRET in deploy/compose/.env)
  Development users   requester@zelvo.test, agent@, lead@, approver@, admin@, executive@
                      password: ZELVO_DEV_USERS_PASSWORD in deploy/compose/.env
  RabbitMQ admin      http://localhost:${RABBITMQ_MANAGEMENT_PORT:-15672}
  Grafana             http://localhost:${GRAFANA_PORT:-3001}  (dashboards, traces, logs; GRAFANA_ADMIN_* in deploy/compose/.env)
  Webhook receiver    http://webhook-receiver:8096/hook as a webhook URL; what it got: http://localhost:${WEBHOOK_RECEIVER_PORT:-8096}/received
  AI mock             http://ai-mock:8097/v1 as an OpenAI-compatible endpoint in Settings → AI; what it got: http://localhost:${AI_MOCK_PORT:-8097}/received

Stop it with: make down
MSG
if [[ "${ZELVO_PROFILE:-}" == "test" ]]; then
  echo "This is the test installation (Compose project zelvo-test). Stop it with: make test-installation-down"
fi
