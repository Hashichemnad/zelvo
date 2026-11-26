// Shared set-up for Zelvo's load tests (task F7). See tests/load/README.md.
import http from "k6/http";
import { check } from "k6";
import { SharedArray } from "k6/data";

/** What provisioning wrote (tests/e2e/load/provision.spec.ts; LOAD_CREDENTIALS relative to this file or absolute), read once and shared by every virtual user. */
const credentialsFile = __ENV.LOAD_CREDENTIALS || "../../.credentials/load.json";
const provisioned = new SharedArray("provisioned", () => [JSON.parse(open(credentialsFile))]);

export const config = () => provisioned[0];

/** Where the API is (direct) and where the web app is (through its backend-for-frontend). */
export const apiUrl = () => __ENV.API_URL || config().apiUrl;
export const webUrl = () => __ENV.WEB_URL || config().webUrl;
export const tenant = () => __ENV.LOAD_TENANT || config().tenant;

/** The NFR target: API under 200 ms at the 95th percentile (docs/requirements.md). */
export const p95TargetMs = Number(__ENV.P95_TARGET_MS || 200);

/** Errors other than the ones a journey expects stay under 1 %. */
export const errorRateTarget = Number(__ENV.ERROR_RATE_TARGET || 0.01);

export const pick = (items) => items[Math.floor(Math.random() * items.length)];

/** Pauses like a person between actions (seconds, uniformly between the two). */
export const think = (min, max) => min + Math.random() * (max - min);

/**
 * Calls the API directly with one of the provisioned API keys (agent role). `name` groups the
 * metric per endpoint, so each endpoint has its own p95 in the summary.
 */
export function apiCall(method, path, body, name, keyIndex) {
  const keys = config().apiKeys;
  const key = keys[(keyIndex ?? __VU) % keys.length].key;
  const params = {
    headers: { "X-Api-Key": key, "X-Zelvo-Tenant": tenant(), Accept: "application/json", "Content-Type": "application/json" },
    tags: { name, via: "api" },
  };
  const response = http.request(method, `${apiUrl()}/api/v1/${path}`, body === undefined ? null : JSON.stringify(body), params);
  check(response, { [`${name} succeeded`]: (r) => r.status >= 200 && r.status < 300 });
  return response;
}

/**
 * Calls the API through the web app's backend-for-frontend with one of the provisioned web
 * sessions, exactly as a signed-in agent's browser does (session cookie, CSRF token on changes).
 */
export function webCall(method, path, body, name, sessionIndex) {
  const sessions = config().sessions;
  const session = sessions[(sessionIndex ?? __VU) % sessions.length];
  const headers = { Cookie: session.cookie, Accept: "application/json" };
  if (method !== "GET") {
    headers["Content-Type"] = "application/json";
    headers["X-CSRF-Token"] = session.csrf;
    headers.Origin = webUrl();
  }
  const response = http.request(method, `${webUrl()}/bff/api/v1/${path}`, body === undefined ? null : JSON.stringify(body), {
    headers,
    tags: { name, via: "web" },
  });
  check(response, { [`${name} succeeded`]: (r) => r.status >= 200 && r.status < 300 });
  return response;
}

/** Thresholds: p95 under the target for every named endpoint and overall, and few errors. */
export function thresholdsFor(names, via) {
  const thresholds = {
    [`http_req_duration{via:${via}}`]: [`p(95)<${p95TargetMs}`],
    [`http_req_failed{via:${via}}`]: [`rate<${errorRateTarget}`],
    checks: [`rate>${1 - errorRateTarget}`],
  };
  for (const name of names) {
    thresholds[`http_req_duration{name:${name}}`] = [`p(95)<${p95TargetMs}`];
  }
  return thresholds;
}
