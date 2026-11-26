# Load-test guide (task F7)

Step-by-step instructions for running Zelvo's load tests on a dedicated test server and judging the results against the performance targets. The scripts and what they do are described in `tests/load/README.md`; this guide is the runbook around them.

**Targets** (`docs/requirements.md`, non-functional requirements):

| Target | Measured by | Passes when |
| --- | --- | --- |
| API under 200 ms at the 95th percentile | `tests/load/k6/api-latency.js` | p95 of every endpoint and of all calls under 200 ms; failed requests under 1 % |
| 1,000 concurrent agents per tenant | `tests/load/k6/agents.js` with `tests/load/live/live-connections.mjs` | the same p95 and error rate with 1,000 agents working at once; at least 990 of 1,000 live-update connections open while holding, connecting in under 1 s at p95 |
| Server sizing is right | All runs, with Grafana | the published sizes (table below) carry their number of agents within the targets |

## 1. What to run, on which hardware

The published sizes (`docs/requirements.md`, "Server sizing") rate the **Small** server for **up to 50 agents**. The 1,000-agent target applies to a tenant on a larger installation. So the runs are:

| Run | Server | Load | Proves |
| --- | --- | --- | --- |
| A. API latency | Small | `api-latency.js`, `RATE=50` (about 300 requests a second) for 10 minutes | the API itself stays under 200 ms at p95 |
| B. Rated agents | Small | `agents.js`, `AGENTS=50`, with 50 live connections | the Small server carries its rated 50 agents |
| C. Small ceiling | Small | `agents.js` at 100, 200, 400 and 800 agents, one run each | how far the Small server goes before p95 crosses 200 ms; confirms or corrects the sizing table |
| D. 1,000 agents | Large test server (below) | `agents.js`, `AGENTS=1000`, with 1,000 live connections | the 1,000-agent target |

**The Small test server** (the published minimum):

- 8 vCPU, 32 GB RAM, 500 GB SSD (local NVMe or provisioned-IOPS block storage, not burstable disks).
- Ubuntu Server 24.04 LTS, Docker Engine 27 or newer with the Compose plugin.
- Dedicated: no other workloads, no CPU credits or burst limits (avoid "burstable" cloud instance types).
- A UAE cloud region or the customer's data centre, as a customer would host it.

**The large test server for run D.** Medium and Large installations run on Kubernetes (task F6), and the Helm chart has not yet run on a real cluster. Until it has, run D on **one server of 32 vCPU and 128 GB RAM** with the same Compose installation and record that it is a single-server result. Repeat D on a Medium cluster (3 application nodes of 8 vCPU and 16 GB, a database server of 8 vCPU, 64 GB and 1 TB SSD) once the chart has been tried, and publish those figures.

**The load generator** is a separate machine on the same private network (under 1 ms round trip, 1 Gbit/s or more):

- Runs A to C: 4 vCPU, 8 GB RAM.
- Run D: 8 vCPU, 16 GB RAM (k6 needs a few MB per virtual agent; the live-connection script about 1 GB for 1,000 connections).
- k6 2.4.0 (the version CI uses; binary or the `grafana/k6:2.4.0` image), Node.js 24 LTS, Git.

Running the generator on the test server itself makes the results worse than reality (it takes CPU from Zelvo), so do it only for a smoke run.

## 2. Which installation is measured

The load tests run on the **development Compose installation** (`make up`), with the provisioning script's development users. The hardened install (F6) has no development users and no way yet to create the first tenant administrator from the command line (see `pentest-pack.md`, "Before the test"), so provisioning cannot run there today.

What differs from a customer's hardened install, and what that means for the results:

| Difference | Effect on the results |
| --- | --- |
| Images built from source (Release build, not obfuscated) | None expected: CI checks that the obfuscated API behaves the same (`scripts/check-obfuscation.sh`). |
| No CPU and memory limits per service | Slightly optimistic: services may use the whole server. Run C shows the margin. |
| No TLS between the services and PostgreSQL and Valkey (hardened installs use TLS 1.3 for both) | Slightly optimistic: TLS adds a little CPU per new connection; connections are pooled. |
| `ASPNETCORE_ENVIRONMENT=Development` (detailed errors, OpenAPI on) | None on timings measured so far. |

When the first administrator can be created on hardened installs, repeat run B there and compare. A difference over 10 % at p95 goes into the release-readiness report.

## 3. Prepare the test server

All commands run on the test server as a user in the `docker` group, unless marked as on the load generator.

1. Check out the commit being released (the tag, or the commit of the release candidate), so the result belongs to a known version:

   ```bash
   git clone https://github.com/Hashichemnad/zelvo.git && cd zelvo && git checkout <release tag or commit>
   ```

2. Start the installation. The first start builds the images and takes 15 to 30 minutes:

   ```bash
   make up
   ```

3. Apply the load-test settings (the raised per-person rate limit; see `tests/load/compose.load.yaml`) and publish the API and web app on the private network address only (`tests/load/compose.load-network.yaml`; replace `10.0.0.5` with the test server's private address):

   ```bash
   ZELVO_LOAD_BIND=10.0.0.5 docker compose -f deploy/compose/compose.yaml -f tests/load/compose.load.yaml -f tests/load/compose.load-network.yaml up -d api web
   ```

   Docker-published ports are not filtered by `ufw`. Use an address that only the private network reaches, or a cloud security group that admits only the load generator to ports 18080 and 13000.

4. Check that everything is healthy and that Grafana is up (`http://127.0.0.1:3001` on the server; reach it with an SSH tunnel: `ssh -L 3001:127.0.0.1:3001 <test server>`):

   ```bash
   docker compose -f deploy/compose/compose.yaml ps --format '{{.Service}} {{.Health}}'
   ```

5. Provision the load-test credentials and sample incidents (Node.js 24 and Playwright's Chromium on the test server, because the development users sign in through `localhost`). Raise `LOAD_INCIDENTS` to 5000 for runs C and D, so lists and searches work on a realistic amount of data (it takes longer):

   ```bash
   cd tests/e2e && npm ci && npx playwright install chromium && E2E_LOAD=1 LOAD_INCIDENTS=500 npx playwright test --project=load-provision
   ```

   This writes `tests/load/.credentials/load.json` (git-ignored, mode 0600). Copy it to the load generator over SSH and to nowhere else; it holds real API keys and session cookies:

   ```bash
   scp tests/load/.credentials/load.json <load generator>:zelvo/tests/load/.credentials/load.json
   ```

6. Note the starting point for the report: the commit, `docker compose -f deploy/compose/compose.yaml images`, `nproc`, `free -g`, `df -h /var/lib/docker`, and the number of incidents (`LOAD_INCIDENTS`).

## 4. Run the tests (on the load generator)

Check out the same commit on the load generator. Set the addresses once (the test server's private address and the ports from step 3):

```bash
export API_URL=http://10.0.0.5:18080 WEB_URL=http://10.0.0.5:13000
```

The scripts send the web app's own address (`http://localhost:3000`, from `load.json`) as the `Origin` of changes, because the web proxy refuses changes from any other origin. If the web app was given another `ZELVO_APP_URL`, set `WEB_ORIGIN` to it.

**Smoke run first** (two minutes, five agents), to catch set-up mistakes before a long run:

```bash
k6 run -e AGENTS=5 -e RAMP_UP=30s -e HOLD=1m -e RAMP_DOWN=10s tests/load/k6/agents.js
```

It must end with every threshold ticked and `checks` at 100 %. A 403 `csrf-failed` means `WEB_ORIGIN` is wrong; a 429 means step 3 was skipped; a 401 means the sessions expired (provision again, step 5).

**Run A, API latency** (about 12 minutes):

```bash
mkdir -p tests/load/results && k6 run --summary-export tests/load/results/A-api-latency.json tests/load/k6/api-latency.js 2>&1 | tee tests/load/results/A-api-latency.txt
```

**Run B, rated agents** (about 35 minutes; the live connections run next to k6):

```bash
CONNECTIONS=50 RAMP_SECONDS=600 DURATION_MINUTES=22 node tests/load/live/live-connections.mjs > tests/load/results/B-live.log 2>&1 &
```

```bash
k6 run -e AGENTS=50 --summary-export tests/load/results/B-agents.json tests/load/k6/agents.js 2>&1 | tee tests/load/results/B-agents.txt
```

**Run C, the Small ceiling.** One run per step, with 10 minutes' rest between them. Stop at the first step whose p95 crosses 200 ms or whose errors pass 1 %; that step's predecessor is the measured capacity:

```bash
for agents in 100 200 400 800; do k6 run -e AGENTS=$agents -e RAMP_UP=5m -e HOLD=15m --summary-export tests/load/results/C-agents-$agents.json tests/load/k6/agents.js 2>&1 | tee tests/load/results/C-agents-$agents.txt || break; sleep 600; done
```

**Run D, 1,000 agents** (on the large test server, about 35 minutes):

```bash
node tests/load/live/live-connections.mjs > tests/load/results/D-live.log 2>&1 &
```

```bash
k6 run --summary-export tests/load/results/D-agents.json tests/load/k6/agents.js 2>&1 | tee tests/load/results/D-agents.txt
```

Wait for the live-connection script to finish too (`wait`), then check its exit code (`echo $?` straight after `wait`; 0 is a pass).

While each run holds its load, take screenshots of Grafana's **Zelvo → Zelvo overview** dashboard with the time range of the run (section 6).

## 5. Read the results

**k6's exit code.** 0: every threshold held. **99**: at least one threshold failed (the run is a fail, whatever the averages say). Anything else: the run did not finish (fix and repeat).

**k6's end-of-test summary** (the `.txt` files) lists each threshold with ✓ or ✗. In the JSON summaries, a threshold's value is `true` when it **failed**. The thresholds are:

| Threshold | Meaning | Target |
| --- | --- | --- |
| `http_req_duration{name:<endpoint>}` `p(95)<200` | p95 of one endpoint, for each of `incident-queue`, `incident-open`, `incident-comments`, `incident-history`, `incident-customer`, `workspace-views`, `search`, `notifications-unread`, `comment-add`, `incident-update` | under 200 ms |
| `http_req_duration{via:api}` / `{via:web}` | p95 of all calls on that path (straight to the API, or through the web app) | under 200 ms |
| `http_req_failed{via:…}` | share of failed HTTP requests (status 400 and up, or no answer) | under 1 % |
| `checks` | share of calls that got a 2xx answer | over 99 % |

To list the p95 of every endpoint from a summary file (needs `jq`):

```bash
jq -r '.metrics | to_entries[] | select(.key | startswith("http_req_duration{")) | "\(.key)\tp95=\(.value["p(95)"] | floor) ms\tp99=\(.value["p(99)"] | floor) ms"' tests/load/results/D-agents.json
```

And the error rates:

```bash
jq -r '.metrics | to_entries[] | select(.key | startswith("http_req_failed{") or . == "checks") | "\(.key)\t\(.value.value)"' tests/load/results/D-agents.json
```

**The live connections** (`*-live.log`). Every 30 seconds a line with `open=<n>/<total>`, connects, failures, messages and the connect p95; at the end a `Done:` line. A pass needs the average open while holding at 99 % of the total or more, and a connect p95 under 1,000 ms (the script's exit code says the same). `messages` should grow during the run, because the agents' notes and changes are what the connections hear. If it stays at 0, live updates are not reaching the web proxy, even when connections stay open.

**How to judge the target.** The requirement is about the **API**, so run A (straight to the API) decides "API under 200 ms". Runs B to D go through the web app's backend-for-frontend, as browsers do, so their times include the web app's proxy; passing there is stricter than the requirement. If run A passes and a web run fails, the web app's proxy is the bottleneck, not the API.

**Client and server views.** Grafana's "API p95" panel measures inside the API (`http_server_request_duration_seconds`); k6 measures from the generator, including the network and, on the web path, the proxy. k6's figure should be the higher one by a few milliseconds on a private network. A large gap means the network or the web app adds the time.

## 6. Find the bottleneck when a run fails

Look at the Zelvo overview dashboard for the run's time range, and at the server itself (the dashboard has no CPU panel):

```bash
docker stats --no-stream > tests/load/results/<run>-docker-stats.txt
```

```bash
docker compose -f deploy/compose/compose.yaml exec postgres sh -c 'psql -U "$POSTGRES_USER" -tAc "select state, count(*) from pg_stat_activity group by state"'
```

| What you see | Likely cause | What to try |
| --- | --- | --- |
| "Latency by route (p95)": one route stands out | That endpoint's query | Its trace in Tempo (Explore → Tempo, filter by `http.route`) shows the slow database call; add an index through a migration |
| `docker stats`: `api` (or `web`) CPU near the server's limit | Not enough CPU for this load | The measured capacity is the step before; correct the sizing table |
| "Database query time (p95)" rises with load; `postgres` CPU high | PostgreSQL is the bottleneck | For Medium and Large, a separate database server is already planned |
| Many `idle in transaction` or the count stuck at the pool's maximum | Connection pool too small, or a slow transaction | Note it; the pool size (`Maximum Pool Size` in the connection string) is an installation setting |
| "Rate-limited requests" above 0 | `compose.load.yaml` not applied, or the API keys' limits | Repeat step 3 |
| "Queue depth (RabbitMQ)" or "Search indexing" falling behind | Search indexing cannot keep up | Normal during a burst; a run fails only if the queue keeps growing after the load ends |
| "Memory (working set)" of a .NET service rising without levelling off | A leak | Report it as a defect, with the dashboard screenshot |
| "Server errors" above 0 | A defect | "Warnings and errors" (or Explore → Loki, `{service_namespace="zelvo"} \| detected_level="error"`) has the exceptions |

Keep the `docker stats` output from the hold phase of each run in the results folder.

## 7. Clean up

Every time, also after a failed run:

1. Stop the live-connection script if it still runs (`kill %1`).
2. In Zelvo, sign in as the development administrator and revoke the "Load test …" API keys (Settings → Integrations).
3. Delete the credentials on both machines:

   ```bash
   rm -f tests/load/.credentials/load.json
   ```

4. Stop the installation and remove its data, so no load-test data or sessions remain:

   ```bash
   make reset
   ```

## 8. Report

Write a short release-readiness report with, for each run: the run letter, date, commit, server size (vCPU, RAM, disk type), number of agents and incidents, the k6 summary's p95 per endpoint and error rate, the live-connection `Done:` line, pass or fail, and the dashboard screenshots. Then:

- Update the **Server sizing** table in `docs/requirements.md` with what was measured (run C for Small, run D for 1,000 agents), with the product owner's approval.
- Record defects found as issues, with the trace or log that shows them.
- Mark the load-test step of F7 done in `CLAUDE.md`.

The k6 summaries and logs in `tests/load/results/` are git-ignored; attach them to the report instead of committing them.
