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
  "incident-update",
];

/** Endpoints only people may use (integrations get 403 people-only): read with web sessions. */
export const peopleOnly = ["workspace-views", "notifications-unread"];

/** Changes: integrations may not write comments or change incidents, so these always go as people. */
export const writes = ["comment-add", "incident-update"];

/**
 * One round of an agent's work: look at the queue, open an incident with its conversation, now
 * and then its history, the customer, a search, an internal note or a change of urgency.
 * `read(method, path, body, name)` is apiCall or webCall; `write` makes the changes and is always
 * webCall, because writing comments and changing incidents are for people only (403 people-only
 * for integrations). People-only reads are skipped when reading with API keys (`people` false).
 */
export function agentRound(read, { people, pause = true, write = read }) {
  const rest = (min, max) => pause && sleep(think(min, max));

  read("GET", "incidents?open=true&sort=due&pageSize=25", undefined, "incident-queue");
  if (people) {
    read("GET", "workspace/views", undefined, "workspace-views");
    read("GET", "me/notifications/unread-count", undefined, "notifications-unread");
  }
  rest(2, 6);

  const id = pick(config().incidentIds);
  read("GET", `incidents/${id}`, undefined, "incident-open");
  read("GET", `incidents/${id}/comments`, undefined, "incident-comments");
  if (Math.random() < 0.3) {
    read("GET", `incidents/${id}/history`, undefined, "incident-history");
  }
  if (Math.random() < 0.3) {
    read("GET", `incidents/${id}/customer`, undefined, "incident-customer");
  }
  rest(5, 15);

  if (Math.random() < 0.4) {
    read("GET", `search?q=${encodeURIComponent(pick(searchWords))}&pageSize=10`, undefined, "search");
    rest(2, 5);
  }

  if (Math.random() < 0.25) {
    // Internal notes, so the requester is not sent a notification for every load-test write.
    write("POST", `incidents/${id}/comments`, { body: "Load test: checked with the requester; still investigating.", visibility: "Internal", language: "en" }, "comment-add");
    rest(3, 8);
  }

  if (Math.random() < 0.05) {
    write("PATCH", `incidents/${id}`, { urgency: 1 + Math.floor(Math.random() * 3) }, "incident-update");
  }

  if (people) {
    // The header asks for the notification count every 30 seconds (task B5).
    read("GET", "me/notifications/unread-count", undefined, "notifications-unread");
  }
  rest(5, 15);
}
