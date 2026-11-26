# Helm chart for medium and large installs

`zelvo/` installs Zelvo on Kubernetes (task F6; product owner decisions 2026-11-26): Zelvo's own
services (api, worker, web, keycloak, clamav, the caddy proxy and the migrate job), with
PostgreSQL, OpenSearch, RabbitMQ and Valkey given by address. Run those with operators
(CloudNativePG, the OpenSearch operator, the RabbitMQ cluster operator) or use managed services.
Small installs use Docker Compose instead (`deploy/compose/compose.production.yaml`).

## Before installing

1. **Images.** Pull access to the private registry, or load the bundle's `images/*.tar.gz` and
   push them to the customer's registry (`image.registry`, `image.prefix`).
2. **Data services** reachable from the namespace:
   - PostgreSQL 17 with pgvector, a superuser for migrations (`postgres.owner`), and the roles and
     Keycloak database from `examples/database-roles.sql`. `examples/cloudnative-pg.yaml` is a
     CloudNativePG cluster with continuous backup to object storage (RPO under 15 minutes, 35 days).
   - OpenSearch 3 with the ICU plugin (`zelvo-opensearch` image or the operator with the plugin),
     users `zelvo-api` and `zelvo-worker` as in `deploy/compose/security/opensearch`.
   - RabbitMQ 4 with TLS, virtual host `zelvo`, users `zelvo-api` and `zelvo-worker` (see
     `deploy/compose/security/rabbitmq`), and Valkey or Redis with a password.
   - TLS on all four, and the CA of their certificates in a Secret (`caSecret`, key `ca.crt`):
     Zelvo checks every server's certificate (PostgreSQL `VerifyFull` by default).
3. **The Secret** `existingSecret` (default `zelvo-secrets`) with these keys:

   | Key | What |
   | --- | --- |
   | `postgresOwnerPassword`, `postgresAppPassword`, `keycloakDbPassword` | Database passwords |
   | `keycloakAdminPassword`, `keycloakProvisionerSecret`, `webClientSecret` | Keycloak |
   | `tlsToken` | The proxy's questions to the API (custom domains) |
   | `redisPassword`, `rabbitmqApiPassword`, `rabbitmqWorkerPassword` | Cache and queue |
   | `opensearchApiPassword`, `opensearchWorkerPassword` | Search |
   | `smtpPassword` | Mail server (empty if none) |
   | `masterKey` | 32 random bytes in base64 (`openssl rand -base64 32`); keep a copy outside the cluster |
   | `s3AccessKey`, `s3SecretKey` | Only with `attachments.storage: S3` |

   `examples/make-secret.sh` makes it with random values.
4. **Pod Security.** Every pod meets the `restricted` standard except ClamAV's (its entrypoint
   starts as root): label the namespace `pod-security.kubernetes.io/enforce=baseline` and
   `pod-security.kubernetes.io/warn=restricted`.

## Install and upgrade

```bash
helm install zelvo helm/zelvo-1.4.0.tgz --namespace zelvo -f my-values.yaml \
  --set-file release.manifest=release/release-manifest.json \
  --set-file release.signature=release/release-manifest.json.sig
```

`helm upgrade` with the new release's chart and manifest runs the migrations first (a
`pre-upgrade` hook) and replaces pods one at a time; `helm rollback` returns to the previous
release (restore the database from its backup when the new release's migrations must be undone).
The license's cluster id is the `kube-system` namespace's uid (or `licensing.clusterId`).

With Kyverno in the cluster, `imagePolicy.enabled=true --set-file imagePolicy.publicKey=cosign.pub`
admits only Zelvo images signed with Zelvo's key.

CI lints and renders the chart (`ci/test-values.yaml`) and checks the security rules
(`ci/check-rendered.sh`).
