# Load tests (task F7)

Scripts that check the performance targets of `docs/requirements.md` on a **dedicated test server**:

| Target | Script | Pass when |
| --- | --- | --- |
| API under 200 ms at the 95th percentile | `k6/api-latency.js` | p95 of every endpoint and of all calls under 200 ms, errors under 1 % |
| 1,000 concurrent agents per tenant | `k6/agents.js` with `live/live-connections.mjs` | the same p95 and error rate with 1,000 agents working at once; 99 % of their live-update connections open, connecting under 1 s at p95 |

They are written now and run later on a server sized like the published minimum (Small: 8 vCPU, 32 GB RAM, 500 GB SSD, one server with Docker Compose; Medium and Large when the Helm charts of task F6 exist). The results confirm or correct the server sizing in `docs/requirements.md`. They are **not** part of CI (CI only checks that the scripts load); a shared CI runner says nothing about a customer's server.

## What the agents do

`k6/lib/journeys.js` is one round of work in the agent workspace (task B4), with pauses like a person's (about 30–60 seconds a round):

1. the queue: open incidents, soonest due first (`GET /incidents?open=true&sort=due`), saved views and the notification count (people only);
2. one of the sample incidents with its conversation (`GET /incidents/{id}`, `/comments`), now and then its history and the customer;
3. a search in English or Arabic (`GET /search`) in 40 % of rounds;
4. an internal note in 25 % of rounds (`POST /incidents/{id}/comments`; internal, so no emails are sent);
5. a change of urgency in 5 % of rounds (`PATCH /incidents/{id}`; the priority and SLA clocks follow);
6. the notification count again, as the header asks every 30 seconds (people only).

Writing comments and changing incidents are for people only (integrations get 403 `people-only`), so these changes always go through a web session, also in the scripts that read with API keys.

Every call is tagged with its endpoint, so the k6 summary has a p95 per endpoint, and each has its own threshold.

- **`agents.js`** ramps up to `AGENTS` (1,000) virtual agents over `RAMP_UP` (10 minutes), holds them for `HOLD` (20 minutes) and ramps down. By default it goes **through the web app's backend-for-frontend** with real web sessions (`LOAD_VIA=web`), exactly the path of a browser: session cookie, CSRF token, the proxy, the API. `LOAD_VIA=api` reads straight from the API with API keys instead (the changes still go as people).
- **`api-latency.js`** sends rounds without pauses at a constant rate, reading straight from the API with API keys (changes through web sessions, with their own thresholds): `RATE` rounds a second (50, about 300 requests a second) for `DURATION` (10 minutes). This isolates what the API adds; with 1,000 agents at a round a minute, 300 requests a second is well above their average load.
- **`live/live-connections.mjs`** (Node.js 24, no dependencies) holds `CONNECTIONS` (1,000) live-update connections (SignalR over Server-Sent Events through the web proxy, task B4) for `DURATION_MINUTES` (30), opened over `RAMP_SECONDS` (300). k6 cannot hold event streams, so this runs next to `agents.js`; the notes and incidents the agents write are what the connections hear.

## Credentials and sample data

`tests/e2e/load/provision.spec.ts` prepares the installation (run it once per test server; Node.js 24 and Playwright, as for the end-to-end tests):

- the development administrator creates `LOAD_API_KEYS` (10) API keys acting as the **agent** role, for reading only (`incidents.read`, `knowledge.read`), with 100,000 calls a minute each;
- the development agent signs in once with the second factor and `LOAD_SESSIONS` (50) web sessions are opened from Keycloak's single sign-on;
- the agent raises `LOAD_INCIDENTS` (500) open sample incidents in English and Arabic for the development requester. The installation must not require form answers for incidents without a category (otherwise provisioning stops with the form's validation error).

It writes `tests/load/.credentials/load.json` (git-ignored, readable only by you). These are **real credentials** of the tested installation: revoke the keys in Settings → Integrations ("Load test …") and sign the agent out after the run.

Notes on what is measured:

- All web sessions belong to the one development agent, so `compose.load.yaml` raises the per-person rate limit for the run; every other setting stays as installed. Rate limiting itself stays on (its Valkey round trip is part of every call).
- Calls with API keys are written to the integration log and the audit log (task E1), which people's calls are not. The API-key path is therefore slightly heavier than an agent's: passing it is on the safe side.
- Sign-in itself (Keycloak) is not load-tested: agents sign in once a day; sessions are refreshed by the web app in the background.

## Running a test

On the test server, with the installation started (`make up`, or the hardened install from F6):

```bash
docker compose -f deploy/compose/compose.yaml -f tests/load/compose.load.yaml up -d api
```

```bash
cd tests/e2e && npm ci && E2E_LOAD=1 npx playwright test --project=load-provision
```

Then, from a separate load generator machine (or the same server, which then also carries k6; the API and web ports are bound to 127.0.0.1 in Compose, so a separate machine needs an SSH tunnel or the hardened install's proxy, with `API_URL` and `WEB_URL` set):

```bash
k6 run --summary-export tests/load/results/api-latency.json tests/load/k6/api-latency.js
```

```bash
node tests/load/live/live-connections.mjs > tests/load/results/live.log &
k6 run --summary-export tests/load/results/agents.json tests/load/k6/agents.js
```

k6 runs as a binary or as its container (`docker run --rm --network host -v "$PWD/tests/load:/load" -w /load grafana/k6 run k6/agents.js`). k6 exits non-zero when a threshold is missed. Settings are environment variables (`-e NAME=value` for k6): `API_URL`, `WEB_URL`, `LOAD_TENANT`, `LOAD_CREDENTIALS`, `P95_TARGET_MS` (200), `ERROR_RATE_TARGET` (0.01), and the per-script ones above.

While a test runs, watch Grafana's "Zelvo overview" dashboard (task A5): its API p95 panel shows the target line, and CPU, memory, PostgreSQL connections and the RabbitMQ and search queues show where time goes. Record the server size, the installation's version, the k6 summaries and screenshots of the dashboard in the release-readiness report, and update the server sizing in `docs/requirements.md` with what was measured.
