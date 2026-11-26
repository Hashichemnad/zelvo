// Task F7: API response time under 200 ms at the 95th percentile (docs/requirements.md).
//
// A steady arrival rate of agent-workspace calls straight to the API (no web app in front),
// without pauses, so the latency the API adds is measured at a known throughput. Reads use API
// keys; the changes (internal notes, urgency) are for people only and go through web sessions.
// Every endpoint has its own p95 threshold; k6 exits non-zero when one is missed.
//
//   k6 run tests/load/k6/api-latency.js
//   k6 run -e RATE=300 -e DURATION=15m tests/load/k6/api-latency.js
import { apiCall, thresholdsFor, webCall } from "./lib/common.js";
import { agentRound, endpoints, peopleOnly, writes } from "./lib/journeys.js";

// Rounds per second; one round is about 6 calls, so RATE=50 is about 300 requests a second.
const rate = Number(__ENV.RATE || 50);

export const options = {
  scenarios: {
    latency: {
      executor: "constant-arrival-rate",
      rate,
      timeUnit: "1s",
      duration: __ENV.DURATION || "10m",
      preAllocatedVUs: Number(__ENV.VUS || 200),
      maxVUs: Number(__ENV.MAX_VUS || 1000),
    },
  },
  thresholds: {
    ...thresholdsFor(endpoints.filter((name) => !peopleOnly.includes(name) && !writes.includes(name)), "api"),
    // Changes are for people only, so they go through web sessions and include the web app's time.
    ...thresholdsFor(writes, "web"),
  },
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

export default function () {
  agentRound(apiCall, { people: false, pause: false, write: webCall });
}
