#!/usr/bin/env bash
# Checks that release obfuscation (task F6) keeps Zelvo working:
# 1. publishes the API, obfuscates a copy, starts both without the rest of the stack and
#    compares their OpenAPI documents: every route, parameter and schema must be the same;
# 2. starts the plain and the obfuscated worker the same way: both must still be running
#    after 20 seconds, without an unhandled exception.
# Needs the .NET SDK and curl. Used by CI (job "Release build").
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
work=$(mktemp -d)
pids=()
cleanup() { for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done; rm -rf "$work"; }
trap cleanup EXIT

for app in api:src/api/Zelvo.Api/Zelvo.Api.csproj worker:src/worker/Zelvo.Worker/Zelvo.Worker.csproj; do
  name=${app%%:*}
  dotnet publish "$repo/${app#*:}" -c Release -o "$work/$name-plain" -p:UseAppHost=false --nologo -v quiet
  cp -r "$work/$name-plain" "$work/$name-obfuscated"
  "$repo/build/obfuscar/obfuscate.sh" "$work/$name-obfuscated"
done

# Nothing listens on port 1: the services start, log connection errors and keep trying.
export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=1;Database=zelvo;Username=zelvo"
export ConnectionStrings__Redis="127.0.0.1:1,abortConnect=false"
export ConnectionStrings__RabbitMq="amqp://127.0.0.1:1/"
export Services__OpenSearch=http://127.0.0.1:1/ Services__KeycloakHealth=http://127.0.0.1:1/
export Identity__PublicUrl=http://127.0.0.1:1 Identity__InternalUrl=http://127.0.0.1:1
export Worker__HeartbeatFile="$work/heartbeat"

port=18180
for variant in plain obfuscated; do
  port=$((port + 1))
  (cd "$work/api-$variant" && ASPNETCORE_HTTP_PORTS=$port exec dotnet Zelvo.Api.dll > "$work/api-$variant.log" 2>&1) &
  pids+=($!)
  for _ in $(seq 1 60); do
    curl -sf -o "$work/openapi-$variant.json" "http://127.0.0.1:$port/openapi/v1.json" && break
    sleep 1
  done
  if [[ ! -s "$work/openapi-$variant.json" ]]; then
    echo "check-obfuscation: the $variant API did not serve its OpenAPI document" >&2
    grep -v '^\s*at ' "$work/api-$variant.log" | grep -iE 'unhandled|exception' | head -20 >&2 || true
    exit 1
  fi
  (cd "$work/worker-$variant" && exec dotnet Zelvo.Worker.dll > "$work/worker-$variant.log" 2>&1) &
  pids+=($!)
  declare "worker_$variant=$!"
done

# The documents differ only in the server address (the port).
for variant in plain obfuscated; do
  sed -E 's#http://127\.0\.0\.1:[0-9]+/?##g' "$work/openapi-$variant.json" > "$work/openapi-$variant.cmp"
done
if ! diff -q "$work/openapi-plain.cmp" "$work/openapi-obfuscated.cmp" > /dev/null; then
  echo "check-obfuscation: the obfuscated API's OpenAPI document differs from the plain one" >&2
  diff <(tr ',' '\n' < "$work/openapi-plain.cmp") <(tr ',' '\n' < "$work/openapi-obfuscated.cmp") | head -40 >&2
  exit 1
fi
echo "check-obfuscation: OpenAPI documents match ($(grep -o '"operationId"' "$work/openapi-plain.cmp" | wc -l) operations)"

sleep 20
for variant in plain obfuscated; do
  pid_var="worker_$variant"
  if ! kill -0 "${!pid_var}" 2>/dev/null || grep -q 'Unhandled exception' "$work/worker-$variant.log"; then
    echo "check-obfuscation: the $variant worker stopped" >&2
    grep -v '^\s*at ' "$work/worker-$variant.log" | tail -20 >&2
    exit 1
  fi
done
echo "check-obfuscation: the obfuscated worker keeps running"
