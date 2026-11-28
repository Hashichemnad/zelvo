# Load tests (tasks F7 and L1)

Scripts that check the performance targets of `docs/requirements.md` on a **dedicated test server**. The step-by-step runbook (sizing, commands, reading the results, clean-up and report) is `docs/release-readiness/load-test-guide.md`.

| Target | Script | Pass when |
| --- | --- | --- |
| API under 200 ms at the 95th percentile | `k6/api-latency.js` | p95 of every endpoint and of all calls under 200 ms, errors under 1 % |
| 1,000 concurrent agents per tenant | `k6/agents.js` with `live/live-connections.mjs` | the same p95 and error rate with 1,000 agents working at once; 99 % of their live-update connections open, connecting under 1 s at p95 |
| CMDB size (Phase 2 plan, decision 12): 50,000 configuration items per tenant on Small, 500,000 on Medium | `cmdb/provision-cmdb.mjs` fills the CMDB, then both k6 scripts | the same p95 and error rate for the CMDB, problem and change endpoints with that many items in the tenant |

They are written now and run later on a server sized like the published minimum (Small: 8 vCPU, 32 GB RAM, 500 GB SSD, one server with Docker Compose; Medium and Large when the Helm charts of task F6 exist). The results confirm or correct the server sizing in `docs/requirements.md`. They are **not** part of CI (CI only checks that the scripts load); a shared CI runner says nothing about a customer's server.

## What the agents do

`k6/lib/journeys.js` is one round of work in the agent workspace (task B4), with pauses like a person's (about 30–60 seconds a round):

1. the queue: open incidents, soonest due first (`GET /incidents?open=true&sort=due`), saved views and the notification count (people only);
2. one of the sample incidents with its conversation (`GET /incidents/{id}`, `/comments`), now and then its history and the customer;
3. a search in English or Arabic (`GET /search`) in 40 % of rounds;
4. an internal note in 25 % of rounds (`POST /incidents/{id}/comments`; internal, so no emails are sent);
5. a change of urgency in 5 % of rounds (`PATCH /incidents/{id}`; the priority and SLA clocks follow);
6. the notification count again, as the header asks every 30 seconds (people only).

Task L1 adds rounds on **problems, changes and the CMDB** (`k6/lib/phase2.js`), each on one of them (weighted 30 / 30 / 40), all reads:

- problems: the open problems (`GET /problems?open=true`), one problem with its incidents (`/problems/{id}`, `/incidents`), now and then its timeline, a known error search in English or Arabic (`GET /known-errors?q=`), and for one of its incidents the matching known errors and linked problems (`GET /incidents/{id}/known-errors`, `/problems`);
- changes: the open changes (`GET /changes?open=true`), one change with its configuration items (`/changes/{id}`, `/configuration-items`) and its what-if simulation (`GET /changes/{id}/what-if`, people only), now and then this month's change calendar (`GET /changes/calendar`) and the conflict check of its window (`POST /changes/conflicts`, a read with `changes.read`);
- the CMDB: the item list filtered by family or searched by name (`GET /cmdb/items`), now and then the item picker (`GET /cmdb/lookup`) and the search index (`GET /search?types=ci`), one item with its relationships (`/cmdb/items/{id}`, `/relationships`), now and then its tickets, the impact of a server or switch three steps deep (`GET /cmdb/items/{id}/impact?depth=3`) and the map around an application, service or virtual machine (`GET /cmdb/items/{id}/map?depth=2`).

The areas run only when provisioning wrote their sample data (`problems`, `changes`, `cmdb` in `load.json`); an older `load.json` runs the Phase 1 rounds alone.

Writing comments and changing incidents are for people only (integrations get 403 `people-only`), so these changes always go through a web session, also in the scripts that read with API keys. The what-if simulation is for people too, and goes the same way.

Every call is tagged with its endpoint, so the k6 summary has a p95 per endpoint, and each has its own threshold.

- **`agents.js`** ramps up to `AGENTS` (1,000) virtual agents over `RAMP_UP` (10 minutes), holds them for `HOLD` (20 minutes) and ramps down. In `PHASE2_SHARE` (0.3) of their rounds they also do a problem, change or CMDB round (0 leaves them out). By default it goes **through the web app's backend-for-frontend** with real web sessions (`LOAD_VIA=web`), exactly the path of a browser: session cookie, CSRF token, the proxy, the API. `LOAD_VIA=api` reads straight from the API with API keys instead (the changes still go as people).
- **`api-latency.js`** sends rounds without pauses at a constant rate, reading straight from the API with API keys (changes through web sessions, with their own thresholds): `RATE` rounds a second (50, about 300 requests a second) for `DURATION` (10 minutes). This isolates what the API adds; with 1,000 agents at a round a minute, 300 requests a second is well above their average load. A second scenario sends `PHASE2_RATE` (15) problem, change or CMDB rounds a second (about 90 requests a second) at the same time (0 leaves it out).
- **`live/live-connections.mjs`** (Node.js 24, no dependencies) holds `CONNECTIONS` (1,000) live-update connections (SignalR over Server-Sent Events through the web proxy, task B4) for `DURATION_MINUTES` (30), opened over `RAMP_SECONDS` (300). k6 cannot hold event streams, so this runs next to `agents.js`; the notes and incidents the agents write are what the connections hear.

## Credentials and sample data

`tests/e2e/load/provision.spec.ts` prepares the installation (run it once per test server; Node.js 24 and Playwright, as for the end-to-end tests):

- the development administrator creates `LOAD_API_KEYS` (10) API keys acting as the **agent** role, for reading only (`incidents.read`, `knowledge.read`; with task L1 also `problems.read`, `changes.read`, `cmdb.read`), with 100,000 calls a minute each;
- the development agent signs in once with the second factor and `LOAD_SESSIONS` (50) web sessions are opened from Keycloak's single sign-on;
- the agent raises `LOAD_INCIDENTS` (500) open sample incidents in English and Arabic for the development requester. The installation must not require form answers for incidents without a category (otherwise provisioning stops with the form's validation error);
- task L1: the administrator adds Zelvo's standard CMDB classes, makes (or reuses) the discovery API source **"Load test discovery"** and one more key, **"Load test CMDB …"**, with the tenant administrator role narrowed to `cmdb.read`, `cmdb.manage`, `problems.read`, `problems.edit`, `changes.read` and `changes.edit` (pushing records and linking items; making a source needs `cmdb.discovery.manage`, which stays with people);
- the agent raises `LOAD_PROBLEMS` (50) problems from three sample incidents each, with a bilingual known error, and `LOAD_CHANGES` (100) normal changes with plans and evening windows over the next 30 days (changes `i`, `i + 30`, `i + 60` and `i + 90` share a window). Raising problems and changes is for people only. `LOAD_CMDB=0` leaves this step out.

It writes `tests/load/.credentials/load.json` (git-ignored, readable only by you). These are **real credentials** of the tested installation: revoke the keys in Settings → Integrations ("Load test …", also "Load test CMDB …") and sign the agent out after the run.

Then `cmdb/provision-cmdb.mjs` (Node.js 24, no dependencies) fills the CMDB through the discovery API (`POST /cmdb/discovery/sources/{id}/records`, 1,000 records a call) with the "Load test CMDB" key:

- `CMDB_ITEMS` (50,000; 500,000 for Medium) items of a mid-sized organisation: switches, servers, virtual machines, databases, applications and business services (about 10 % together), printers, phones and tablets, and laptops and desktops for the rest;
- relationships, so impact and the map have depth: servers connect to switches, virtual machines run on servers, databases and applications run on virtual machines, applications depend on databases, each business service depends on five applications;
- every record has a fixed id at the source (`lt-<kind>-<number>`, also its name and host name), so running it again updates the same items; `CMDB_FROM` continues a run that stopped at a record, `CMDB_SKIP_PUSH=1` only does the next step;
- then it picks sample items (about 100 of each infrastructure class, 60 of the others), links a server and an application to each sample change (changes sharing a window get the same server, so they conflict) and to each problem, and writes the sample ids into `load.json` (`cmdb.itemIds`, `impactIds`, `mapIds`, the changes' `configurationItemIds`).

Pushing takes time: each record is reconciled (identification rules, values, search document) as it arrives. How long that takes has not been measured yet; the script prints each call's time and the total, which go into the report (500,000 items can take hours, so run it in `tmux` or with `nohup`).

Notes on what is measured:

- All web sessions belong to the one development agent, so `compose.load.yaml` raises the per-person rate limit for the run; every other setting stays as installed. Rate limiting itself stays on (its Valkey round trip is part of every call).
- Calls with API keys are written to the integration log and the audit log (task E1), which people's calls are not. The API-key path is therefore slightly heavier than an agent's: passing it is on the safe side.
- The CMDB push is provisioning, not measured load: its calls are logged in the integration log like any integration's. The k6 scripts only read the CMDB.
- Sign-in itself (Keycloak) is not load-tested: agents sign in once a day; sessions are refreshed by the web app in the background.

## Running a test

On the test server, with the installation started (`make up`, or the hardened install from F6):

```bash
docker compose -f deploy/compose/compose.yaml -f tests/load/compose.load.yaml up -d api
```

```bash
cd tests/e2e && npm ci && E2E_LOAD=1 npx playwright test --project=load-provision
```

```bash
CMDB_ITEMS=50000 node tests/load/cmdb/provision-cmdb.mjs
```

Then, from a separate load generator machine (or the same server, which then also carries k6). The API and web ports are bound to 127.0.0.1 in Compose; `compose.load-network.yaml` also publishes them on the test server's private address (`ZELVO_LOAD_BIND`, ports 18080 and 13000), and the generator sets `API_URL` and `WEB_URL` to them. Changes always send the web app's own address as `Origin` (from `load.json`, or `WEB_ORIGIN`), because the web proxy refuses changes from other origins:

```bash
k6 run --summary-export tests/load/results/api-latency.json tests/load/k6/api-latency.js
```

```bash
node tests/load/live/live-connections.mjs > tests/load/results/live.log &
k6 run --summary-export tests/load/results/agents.json tests/load/k6/agents.js
```

k6 runs as a binary or as its container (`docker run --rm --network host -v "$PWD/tests/load:/load" -w /load grafana/k6 run k6/agents.js`). k6 exits non-zero when a threshold is missed. Settings are environment variables (`-e NAME=value` for k6): `API_URL`, `WEB_URL`, `LOAD_TENANT`, `LOAD_CREDENTIALS`, `WEB_ORIGIN`, `P95_TARGET_MS` (200), `ERROR_RATE_TARGET` (0.01), `PHASE2_RATE` and `PHASE2_SHARE`, and the per-script ones above.

While a test runs, watch Grafana's "Zelvo overview" dashboard (task A5): its API p95 panel shows the target line, and latency by route, database query time, memory and the RabbitMQ and search queues show where time goes (CPU from `docker stats`; see the guide). Record the server size, the installation's version, the number of configuration items, problems and changes, the k6 summaries and screenshots of the dashboard in the release-readiness report, and update the server sizing in `docs/requirements.md` with what was measured.
