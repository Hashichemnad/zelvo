#!/usr/bin/env bash
# Makes the Secret the chart reads (task F6) with random passwords; fill in the ones of services
# you already run (database, cache, queue, search, mail) with --from-literal before applying.
#   examples/make-secret.sh zelvo > zelvo-secrets.yaml   (then keep the file safe, or apply it)
set -euo pipefail
namespace=${1:-zelvo}
r() { openssl rand -hex 24; }
kubectl create secret generic zelvo-secrets --namespace "$namespace" --dry-run=client -o yaml \
  --from-literal=postgresOwnerPassword="$(r)" --from-literal=postgresAppPassword="$(r)" \
  --from-literal=keycloakDbPassword="$(r)" --from-literal=keycloakAdminPassword="$(r)" \
  --from-literal=keycloakProvisionerSecret="$(r)" --from-literal=webClientSecret="$(r)" \
  --from-literal=tlsToken="$(r)" --from-literal=redisPassword="$(r)" \
  --from-literal=rabbitmqApiPassword="$(r)" --from-literal=rabbitmqWorkerPassword="$(r)" \
  --from-literal=opensearchApiPassword="$(r)" --from-literal=opensearchWorkerPassword="$(r)" \
  --from-literal=smtpPassword="" --from-literal=masterKey="$(openssl rand -base64 32)"
