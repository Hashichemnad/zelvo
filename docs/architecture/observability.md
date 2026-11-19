# Observability (task A5)

Covers the non-functional requirements **Performance** (monitoring the API's 95th percentile against 200 ms), **Availability** and the reference stack's "OpenTelemetry with Grafana". Product owner decisions 2026-11-18: traces in **Grafana Tempo**, logs in **Grafana Loki**, metrics in **Prometheus**, all shown in **Grafana**; dashboards are operator tools and **English only**.

## Flow

```
API, worker (OpenTelemetry .NET)  ──OTLP gRPC──┐
web app server (@vercel/otel)     ──OTLP HTTP──┼──▶ OpenTelemetry Collector ──▶ Prometheus (metrics, OTLP receiver)
Keycloak (built-in tracing)       ──OTLP gRPC──┘                            ├──▶ Tempo (traces)
                                                                            └──▶ Loki (logs)
Prometheus also scrapes: Keycloak :9000/metrics, RabbitMQ :15692, the collector :8888
Grafana ──▶ Prometheus, Tempo, Loki (traces link to their logs, logs to their trace)
```

- **Switch.** Services export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (standard OpenTelemetry settings, such as `OTEL_EXPORTER_OTLP_PROTOCOL`, apply). Without it nothing is collected, which keeps tests and installs without monitoring quiet.
- **.NET** (`src/api/Zelvo.Observability`, `AddZelvoTelemetry`): resource `service.namespace=zelvo`, `service.name` (`zelvo-api`, `zelvo-worker`), version, instance and environment. Traces: ASP.NET Core (probes not traced), HttpClient, Npgsql, RabbitMQ and Zelvo's own sources (`Zelvo`, `Zelvo.*`), with `zelvo.tenant` on request spans. Metrics: ASP.NET Core and Kestrel, HttpClient, Npgsql, the .NET runtime and Zelvo's meters. Logs: the .NET logs as OpenTelemetry records with trace ids. `Telemetry:TraceSampleRatio` (default 1.0) samples traces; outgoing calls without a parent span (background polling such as the search outbox relay every second) are not traced.
- **Web app** (`src/web/src/instrumentation.ts`): traces of pages, sign-in and the API proxy; the trace context is sent only to the API, so one trace covers the browser request, the web server and the API.
- **Keycloak**: `KC_TRACING_ENABLED` (traces of sign-ins and admin calls) and `KC_METRICS_ENABLED`.

## Personal data in telemetry

Telemetry stays on the installation (no calls home; Grafana's update checks, news and plugin downloads are off). It never carries request bodies, tokens, passwords or SQL parameter values. Query strings are removed from span attributes (search terms can be names), and the web app's spans also drop client addresses, referrers and user ids. Tenants appear by slug. Log messages follow the existing rule: no personal data in log text.

## Zelvo's own metrics

| Metric (Prometheus name) | Meaning |
| --- | --- |
| `zelvo_rate_limit_rejected_total{caller}` | Requests refused with 429 (`user` or `visitor`) |
| `zelvo_rate_limit_store_errors_total` | Requests allowed without a check because Valkey was unavailable |
| `zelvo_search_outbox_published_total` | Search outbox entries published to RabbitMQ |
| `zelvo_search_indexed_total` / `zelvo_search_refused_total` | Entries written to / refused by OpenSearch |
| `zelvo_search_retried_total` | Index messages retried (OpenSearch or PostgreSQL unavailable) |
| `zelvo_secrets_data_key_unwraps_total` | Data keys unwrapped by the key-encryption key (cache misses) |

Prometheus names API metrics with `job="zelvo/zelvo-api"` (service namespace and name).

## Dashboards and alerts

Grafana (development: http://localhost:3001, `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` from `.env`; anonymous access and sign-up off) has the data sources and the **Zelvo → Zelvo overview** dashboard provisioned read-only from `deploy/compose/observability/grafana`:

- API: requests per second, p95 latency against the 200 ms target, server error rate, rate-limited requests, p95 by route, requests by status, rate limiting, database query time;
- background jobs: search indexing and RabbitMQ queue depth;
- platform: services up, memory, data key unwraps;
- logs: warnings and errors of Zelvo's services (open a line to see its trace).

Prometheus alert rules (`observability/alerts.yaml`, shown in Grafana): API p95 above 200 ms for 10 minutes, more than 2% server errors, rate-limit cache unavailable, search indexing retrying. Sending alerts (email, Teams) is set up by each installation.

## Retention

Traces 7 days (Tempo), logs 15 days (Loki), metrics 15 days (Prometheus), all on local volumes. Medium and large installs size these in their Helm values (task F6).

## Compose services

`otel-collector` (0.165.0), `prometheus` (3.16.0), `tempo` (3.1.1), `loki` (3.7.10), `grafana` (13.2.6). Only Grafana is published (127.0.0.1). The collector, Tempo and Loki images have no shell, so Compose cannot check their health; Prometheus's `up` and the dashboard show them.
