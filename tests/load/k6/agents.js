// Task F7: 1,000 concurrent agents per tenant (NFR "Performance", docs/requirements.md).
//
// Each virtual user is an agent working in the agent workspace with human pauses: queue, open
// incident, conversation, history, customer, search, internal notes, the notification count.
// By default through the web app's backend-for-frontend with real web sessions (LOAD_VIA=web),
// like browsers; LOAD_VIA=api calls the API directly with API keys instead. Live updates
// (SignalR) are held open separately by tests/load/live/live-connections.mjs.
//
//   k6 run tests/load/k6/agents.js
//   k6 run -e AGENTS=1000 -e HOLD=30m tests/load/k6/agents.js
import { apiCall, thresholdsFor, webCall } from "./lib/common.js";
import { agentRound, endpoints } from "./lib/journeys.js";

const via = __ENV.LOAD_VIA === "api" ? "api" : "web";
const agents = Number(__ENV.AGENTS || 1000);

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
  thresholds: thresholdsFor(
    endpoints.filter((name) => via === "web" || !["workspace-views", "notifications-unread"].includes(name)),
    via,
  ),
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

export default function () {
  agentRound(via === "web" ? webCall : apiCall, { people: via === "web" });
}
