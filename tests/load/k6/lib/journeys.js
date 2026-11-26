// What an agent does in the agent workspace (task B4), as API calls. Used by both scripts.
import { sleep } from "k6";
import { config, pick, think } from "./common.js";

const searchWords = ["printer", "VPN", "email", "laptop", "password", "طابعة", "الشبكة", "البريد"];

/** Endpoint names, so every one gets its own p95 threshold. */
export const endpoints = [
  "incident-queue",
  "incident-open",
  "incident-comments",
  "incident-history",
  "incident-customer",
  "workspace-views",
  "search",
  "notifications-unread",
  "comment-add",
  "incident-create",
  "incident-update",
];

/**
 * One round of an agent's work: look at the queue, open an incident with its conversation, now
 * and then its history, the customer, a search, an internal note, and sometimes a new incident.
 * `call(method, path, body, name)` is apiCall or webCall. People-only endpoints are skipped for
 * API keys (`people`).
 */
export function agentRound(call, { people, pause = true }) {
  const rest = (min, max) => pause && sleep(think(min, max));

  call("GET", "incidents?open=true&sort=due&pageSize=25", undefined, "incident-queue");
  if (people) {
    call("GET", "workspace/views", undefined, "workspace-views");
    call("GET", "me/notifications/unread-count", undefined, "notifications-unread");
  }
  rest(2, 6);

  const id = pick(config().incidentIds);
  call("GET", `incidents/${id}`, undefined, "incident-open");
  call("GET", `incidents/${id}/comments`, undefined, "incident-comments");
  if (Math.random() < 0.3) {
    call("GET", `incidents/${id}/history`, undefined, "incident-history");
  }
  if (Math.random() < 0.3) {
    call("GET", `incidents/${id}/customer`, undefined, "incident-customer");
  }
  rest(5, 15);

  if (Math.random() < 0.4) {
    call("GET", `search?q=${encodeURIComponent(pick(searchWords))}&pageSize=10`, undefined, "search");
    rest(2, 5);
  }

  if (Math.random() < 0.25) {
    // Internal notes, so the requester is not sent a notification for every load-test write.
    call("POST", `incidents/${id}/comments`, { body: "Load test: checked with the requester; still investigating.", visibility: "Internal", language: "en" }, "comment-add");
    rest(3, 8);
  }

  if (Math.random() < 0.05) {
    const created = call(
      "POST",
      "incidents",
      { title: "Load test: new incident from the workspace", description: "Raised during the load test.", requesterId: config().requesterId, impact: 3, urgency: 3 },
      "incident-create",
    );
    if (created.status === 201) {
      call("PATCH", `incidents/${created.json("id")}`, { urgency: 2 }, "incident-update");
    }
  }

  if (people) {
    // The header asks for the notification count every 30 seconds (task B5).
    call("GET", "me/notifications/unread-count", undefined, "notifications-unread");
  }
  rest(5, 15);
}
