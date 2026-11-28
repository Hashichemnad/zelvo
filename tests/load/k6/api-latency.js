// Task F7: API response time under 200 ms at the 95th percentile (docs/requirements.md).
//
// A steady arrival rate of agent-workspace calls straight to the API (no web app in front),
// without pauses, so the latency the API adds is measured at a known throughput. Reads use API
// keys; the changes (internal notes, urgency) are for people only and go through web sessions.
// Task L1 adds a second scenario at its own rate for problems, changes and the CMDB (reads with
// the same keys; the people-only what-if simulation through web sessions), included when
// provisioning wrote their sample data. Every endpoint has its own p95 threshold; k6 exits
// non-zero when one is missed.
//
//   k6 run tests/load/k6/api-latency.js
//   k6 run -e RATE=300 -e PHASE2_RATE=60 -e DURATION=15m tests/load/k6/api-latency.js
import { apiCall, thresholdsFor, webCall } from "./lib/common.js";
import { agentRound, endpoints, peopleOnly, writes } from "./lib/journeys.js";
import { phase2Available, phase2Endpoints, phase2Round } from "./lib/phase2.js";

// Rounds per second; one round is about 6 calls, so RATE=50 is about 300 requests a second.
const rate = Number(__ENV.RATE || 50);
// Phase 2 rounds per second (about 6 calls each); PHASE2_RATE=0 leaves them out.
const phase2Rate = Number(__ENV.PHASE2_RATE || 15);
const withPhase2 = phase2Rate > 0 && phase2Available();
const duration = __ENV.DURATION || "10m";

const phase2Names = phase2Endpoints();

export const options = {
  scenarios: {
    latency: {
      executor: "constant-arrival-rate",
      rate,
      timeUnit: "1s",
      duration,
      preAllocatedVUs: Number(__ENV.VUS || 200),
      maxVUs: Number(__ENV.MAX_VUS || 1000),
    },
    ...(withPhase2
      ? {
          phase2: {
            executor: "constant-arrival-rate",
            exec: "phase2",
            rate: phase2Rate,
            timeUnit: "1s",
            duration,
            preAllocatedVUs: Number(__ENV.PHASE2_VUS || 100),
            maxVUs: Number(__ENV.PHASE2_MAX_VUS || 500),
          },
        }
      : {}),
  },
  thresholds: {
    ...thresholdsFor(
      [...endpoints.filter((name) => !peopleOnly.includes(name) && !writes.includes(name)), ...(withPhase2 ? phase2Names.read : [])],
      "api",
    ),
    // Changes and the what-if simulation are for people only, so they go through web sessions and include the web app's time.
    ...thresholdsFor([...writes, ...(withPhase2 ? phase2Names.people : [])], "web"),
  },
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

export default function () {
  agentRound(apiCall, { people: false, pause: false, write: webCall });
}

export function phase2() {
  phase2Round(apiCall, { pause: false, person: webCall });
}
