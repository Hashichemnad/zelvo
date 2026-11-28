#!/usr/bin/env node
// Task L1: fills the CMDB of the tested tenant to the size target (Phase 2 plan, decision 12:
// 50,000 configuration items per tenant on the Small server, 500,000 on Medium) through the
// discovery API (task G2), then links sample items to the load test's problems and changes and
// writes the ids the k6 scripts use into load.json. Run it after tests/e2e/load/provision.spec.ts,
// which made the discovery API source and its key. Node.js 24, no dependencies.
//
//   node tests/load/cmdb/provision-cmdb.mjs
//   CMDB_ITEMS=500000 node tests/load/cmdb/provision-cmdb.mjs
//
// Settings (environment variables):
//   LOAD_CREDENTIALS  load.json written by provisioning (default tests/load/.credentials/load.json)
//   API_URL           the API (default: load.json's apiUrl)
//   LOAD_TENANT       the tenant (default: load.json's tenant)
//   CMDB_ITEMS        configuration items to have (default 50000)
//   CMDB_BATCH        records per call (default and most 1000)
//   CMDB_CONCURRENCY  calls at once for the end-user devices (default 1; new items take their
//                     numbers one at a time, so more gains little; the infrastructure with
//                     relationships always goes one call at a time, in order)
//   CMDB_FROM         the record to start from, to continue a run that stopped (default 0)
//   CMDB_SOFTWARE     1: each computer also reports three installed products (slower)
//   CMDB_SKIP_PUSH    1: only collect sample ids and link them (the items are there already)
//
// Idempotent: every record has a fixed id at the source (lt-<kind>-<number>), so running it again
// updates the same items instead of adding new ones; a larger CMDB_ITEMS adds the difference.
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const credentialsFile = path.resolve(process.env.LOAD_CREDENTIALS ?? path.join(here, "../.credentials/load.json"));
const config = JSON.parse(readFileSync(credentialsFile, "utf8"));
if (!config.cmdb?.sourceId || !config.cmdb?.key?.key) {
  console.error(`${credentialsFile} has no CMDB source or key: run the provisioning spec first (without LOAD_CMDB=0).`);
  process.exit(2);
}

const apiUrl = (process.env.API_URL ?? config.apiUrl).replace(/\/$/, "");
const tenant = process.env.LOAD_TENANT ?? config.tenant;
const total = Number(process.env.CMDB_ITEMS ?? "50000");
const batchSize = Math.min(1000, Math.max(1, Number(process.env.CMDB_BATCH ?? "1000")));
const concurrency = Math.max(1, Number(process.env.CMDB_CONCURRENCY ?? "1"));
const from = Math.max(0, Number(process.env.CMDB_FROM ?? "0"));
const withSoftware = process.env.CMDB_SOFTWARE === "1";
const skipPush = process.env.CMDB_SKIP_PUSH === "1";

/**
 * The mix of a mid-sized organisation's CMDB, in the order they are pushed: what others run on
 * comes first, so each relationship's target is already known when it arrives. Shares of the
 * total; end-user devices take the rest.
 */
const kinds = [
  { kind: "sw", classKey: "switch", share: 0.004, min: 2 },
  { kind: "srv", classKey: "server", share: 0.015, min: 15 },
  { kind: "vm", classKey: "virtual-machine", share: 0.06, min: 30 },
  { kind: "db", classKey: "database", share: 0.005, min: 5 },
  { kind: "app", classKey: "application", share: 0.01, min: 40 },
  { kind: "svc", classKey: "business-service", share: 0.002, min: 5 },
  { kind: "prn", classKey: "printer", share: 0.01, min: 1 },
  { kind: "mob", classKey: "mobile-device", share: 0.05, min: 1 },
  { kind: "lap", classKey: "laptop", share: 0, min: 1 },
  { kind: "desk", classKey: "desktop", share: 0, min: 1 },
];
const infrastructure = new Set(["sw", "srv", "vm", "db", "app", "svc"]);

const counts = new Map();
let fixed = 0;
for (const k of kinds.filter((k) => k.share > 0)) {
  counts.set(k.kind, Math.max(k.min, Math.round(total * k.share)));
  fixed += counts.get(k.kind);
}
const rest = Math.max(2, total - fixed);
counts.set("lap", Math.round(rest * 0.7));
counts.set("desk", rest - counts.get("lap"));

// Where each kind starts in the push order.
const starts = new Map();
let offset = 0;
for (const k of kinds) {
  starts.set(k.kind, offset);
  offset += counts.get(k.kind);
}
const planned = offset;

const pad = (n) => String(n).padStart(6, "0");
const id = (kind, n) => `lt-${kind}-${pad(n)}`;
const mac = (index) =>
  ["02", ...[24, 16, 8, 0].map((shift) => ((index >>> shift) & 0xff).toString(16).padStart(2, "0")), "01"].join(":");
const serviceNames = ["Payroll", "Email", "Customer portal", "Licensing", "HR", "Finance", "Fleet", "Procurement", "رواتب", "بوابة المتعاملين"];
const software = [
  { name: "Microsoft 365 Apps", publisher: "Microsoft", version: "16.0" },
  { name: "Google Chrome", publisher: "Google", version: "131.0" },
  { name: "Adobe Acrobat Reader", publisher: "Adobe", version: "24.4" },
];

/** The record at position `index` of the push order (deterministic, so runs repeat the same records). */
function recordAt(index) {
  const k = [...kinds].reverse().find((k) => index >= starts.get(k.kind));
  const n = index - starts.get(k.kind);
  const c = (kind) => counts.get(kind);
  const record = { id: id(k.kind, n), class: k.classKey, name: id(k.kind, n), attributes: {}, relationships: [] };
  const link = (type, kind, m) => record.relationships.push({ type, target: id(kind, m % c(kind)) });
  switch (k.kind) {
    case "sw":
      record.attributes = { hostName: record.name, serialNumber: `LTSW${pad(n)}`, macAddress: mac(index) };
      break;
    case "srv":
      record.attributes = { hostName: record.name, serialNumber: `LTSRV${pad(n)}`, macAddress: mac(index), cpuCores: 32, memoryGb: 256, environment: "production" };
      link("connects-to", "sw", n);
      break;
    case "vm":
      record.attributes = { hostName: record.name, cpuCores: 4, memoryGb: 16, environment: n % 5 === 0 ? "test" : "production" };
      link("runs-on", "srv", n);
      break;
    case "db":
      record.attributes = { engine: "postgresql", environment: "production" };
      link("runs-on", "vm", n * 7);
      break;
    case "app":
      record.attributes = { vendor: "Load test", version: "1.0", environment: "production" };
      link("runs-on", "vm", n * 13 + 1);
      link("depends-on", "db", n);
      break;
    case "svc":
      record.name = `lt-svc-${pad(n)} ${serviceNames[n % serviceNames.length]} / خدمة ${n + 1}`;
      record.attributes = { criticality: n % 3 === 0 ? "high" : "medium" };
      for (let a = 0; a < 5; a++) {
        link("depends-on", "app", n * 5 + a);
      }
      break;
    case "prn":
      record.attributes = { serialNumber: `LTPRN${pad(n)}` };
      break;
    case "mob":
      record.attributes = { imei: String(350000000000000 + n) };
      break;
    default:
      record.attributes = { hostName: record.name, serialNumber: `LT${k.kind.toUpperCase()}${pad(n)}`, macAddress: mac(index), operatingSystem: "Windows 11" };
      if (withSoftware) {
        record.software = software;
      }
  }
  if (record.relationships.length === 0) {
    delete record.relationships;
  }
  return record;
}

const headers = (body) => ({
  "X-Api-Key": config.cmdb.key.key,
  "X-Zelvo-Tenant": tenant,
  Accept: "application/json",
  ...(body === undefined ? {} : { "Content-Type": "application/json" }),
});

/** Calls the API with the CMDB key; retries busy answers (429, 5xx) and lost connections three times. */
async function call(method, apiPath, body) {
  for (let attempt = 1; ; attempt++) {
    let response;
    try {
      response = await fetch(`${apiUrl}/api/v1/${apiPath}`, { method, headers: headers(body), body: body === undefined ? undefined : JSON.stringify(body) });
    } catch (error) {
      if (attempt >= 4) throw error;
      await new Promise((r) => setTimeout(r, attempt * 5000));
      continue;
    }
    if (response.ok) {
      return response.status === 204 ? undefined : response.json();
    }
    const text = await response.text();
    if ((response.status === 429 || response.status >= 500) && attempt < 4) {
      await new Promise((r) => setTimeout(r, attempt * 5000));
      continue;
    }
    throw new Error(`${method} ${apiPath}: ${response.status} ${text.slice(0, 500)}`);
  }
}

const totals = {};

async function push(first, end) {
  const last = Math.min(end, first + batchSize);
  const records = [];
  for (let i = first; i < last; i++) records.push(recordAt(i));
  const started = Date.now();
  const stats = await call("POST", `cmdb/discovery/sources/${config.cmdb.sourceId}/records`, { records });
  for (const [key, value] of Object.entries(stats ?? {})) {
    if (typeof value === "number") totals[key] = (totals[key] ?? 0) + value;
  }
  console.log(`records ${first}–${last - 1} of ${planned}: ${((Date.now() - started) / 1000).toFixed(1)} s ${JSON.stringify(stats)}`);
}

async function pushAll() {
  const infraEnd = kinds.filter((k) => infrastructure.has(k.kind)).reduce((sum, k) => sum + counts.get(k.kind), 0);
  console.log(`Pushing ${planned} configuration items from record ${from} (${[...counts].map(([k, v]) => `${k} ${v}`).join(", ")}) to ${apiUrl}`);
  const began = Date.now();
  // Infrastructure in order, one call at a time: relationships need their targets first.
  for (let first = from; first < infraEnd; first += batchSize) {
    await push(first, infraEnd);
  }
  // End-user devices have no relationships, so several calls can run at once.
  const batches = [];
  for (let first = Math.max(from, infraEnd); first < planned; first += batchSize) batches.push(first);
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(concurrency, batches.length) }, async () => {
      while (next < batches.length) {
        await push(batches[next++], planned);
      }
    }),
  );
  console.log(`Pushed in ${Math.round((Date.now() - began) / 1000)} s: ${JSON.stringify(totals)}`);
}

/** Up to `limit` items of a class (random pages of the load test's items). */
async function sample(classId, limit) {
  const first = await call("GET", `cmdb/items?classId=${classId}&q=lt-&pageSize=100&page=1`);
  const ids = new Set(first.items.map((i) => i.id));
  const pages = Math.min(1000, Math.ceil(first.totalCount / 100));
  for (let tries = 0; ids.size < Math.min(limit, first.totalCount) && tries < 5 && pages > 1; tries++) {
    const page = 2 + Math.floor(Math.random() * (pages - 1));
    for (const item of (await call("GET", `cmdb/items?classId=${classId}&q=lt-&pageSize=100&page=${page}`)).items) ids.add(item.id);
  }
  return [...ids].slice(0, limit);
}

async function collectAndLink() {
  const classes = await call("GET", "cmdb/classes");
  const classId = (key) => {
    const found = classes.find((c) => c.key === key);
    if (!found) throw new Error(`The CMDB has no class '${key}': run the provisioning spec first.`);
    return found.id;
  };
  const byKind = {};
  for (const k of kinds) {
    byKind[k.kind] = await sample(classId(k.classKey), infrastructure.has(k.kind) ? 100 : 60);
  }
  // Items agents open: a spread over every kind. Impact from servers and switches (most depend on
  // them); the map around applications, services and virtual machines.
  const itemIds = Object.values(byKind).flat();
  const impactIds = [...byKind.srv, ...byKind.sw];
  const mapIds = [...byKind.app, ...byKind.svc, ...byKind.vm.slice(0, 40)];

  // Change i gets server i % 15 and an application, so changes sharing a window (i, i + 30, …)
  // conflict on the server; problems get an application and a server.
  const servers = byKind.srv.slice(0, 15);
  const apps = byKind.app;
  for (const [i, change] of (config.changes ?? []).entries()) {
    const items = [servers[i % servers.length], apps[i % apps.length]];
    for (const item of items) await call("PUT", `changes/${change.id}/configuration-items/${item}`);
    change.configurationItemIds = items;
  }
  for (const [i, problem] of (config.problems ?? []).entries()) {
    for (const item of [apps[(i * 3) % apps.length], byKind.srv[i % byKind.srv.length]]) {
      await call("PUT", `problems/${problem.id}/configuration-items/${item}`);
    }
  }

  const count = classes.filter((c) => kinds.some((k) => k.classKey === c.key)).reduce((sum, c) => sum + (c.itemCount ?? 0), 0);
  Object.assign(config.cmdb, { items: count, itemIds, impactIds, mapIds });
  writeFileSync(credentialsFile, JSON.stringify(config, null, 2), { mode: 0o600 });
  console.log(
    `Wrote ${credentialsFile}: ${count} items in the load test's classes, ${itemIds.length} sample items, ${impactIds.length} for impact, ` +
      `${mapIds.length} for the map; ${config.changes?.length ?? 0} changes and ${config.problems?.length ?? 0} problems linked.`,
  );
}

if (!skipPush) {
  await pushAll();
}
await collectAndLink();
