// Task F7: 1,000 concurrent agents per tenant (NFR "Performance", docs/requirements.md).
//
// Each virtual user is an agent working in the agent workspace with human pauses: queue, open
// incident, conversation, history, customer, search, internal notes, the notification count.
// Task L1: in PHASE2_SHARE of the rounds (0.3) the agent also looks at a problem, a change or
// the CMDB (when provisioning wrote their sample data; PHASE2_SHARE=0 leaves them out).
// By default through the web app's backend-for-frontend with real web sessions (LOAD_VIA=web),
// like browsers; LOAD_VIA=api reads from the API directly with API keys instead (changes and the
// what-if simulation still go as people, through the web app, since integrations may not use
// them). Live updates (SignalR) are held open separately by tests/load/live/live-connections.mjs.
//
//   k6 run tests/load/k6/agents.js
//   k6 run -e AGENTS=1000 -e HOLD=30m tests/load/k6/agents.js
import { apiCall, thresholdsFor, webCall } from "./lib/common.js";
import { agentRound, endpoints, peopleOnly } from "./lib/journeys.js";
import { phase2Available, phase2Endpoints, phase2Round } from "./lib/phase2.js";

const via = __ENV.LOAD_VIA === "api" ? "api" : "web";
const agents = Number(__ENV.AGENTS || 1000);
const phase2Share = Number(__ENV.PHASE2_SHARE === undefined ? 0.3 : __ENV.PHASE2_SHARE);
const withPhase2 = phase2Share > 0 && phase2Available();
const phase2Names = phase2Endpoints();

export const options = {
  scenarios: {
    agents: {
      executor: "ramping-vus",
      startVUs: 0,
      stages: [
        { duration: __ENV.RAMP_UP || "10m", target: agents },
        { duration: __ENV.HOLD || "20m", target: agents },
        { duration: __ENV.RAMP_DOWN || "2m", target: 0 },
      ],
      gracefulRampDown: "30s",
    },
  },
  thresholds: {
    ...thresholdsFor(
      [...endpoints.filter((name) => via === "web" || !peopleOnly.includes(name)), ...(withPhase2 ? phase2Names.read : []), ...(withPhase2 && via === "web" ? phase2Names.people : [])],
      via,
    ),
    // With LOAD_VIA=api the changes and the what-if simulation still go through web sessions (people only).
    ...(via === "api" ? thresholdsFor(withPhase2 ? phase2Names.people : [], "web") : {}),
  },
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

export default function () {
  const read = via === "web" ? webCall : apiCall;
  agentRound(read, { people: via === "web", write: webCall });
  if (withPhase2 && Math.random() < phase2Share) {
    phase2Round(read, { person: webCall });
  }
}
