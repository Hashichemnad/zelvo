// What staff do with problems (task H1), changes (H3, H5) and the CMDB (G1) as API calls (task L1).
// Used by both scripts next to the agent-workspace round of journeys.js. Every call is a read:
// raising problems and changes, notes and transitions are for people only and are not part of the
// load (provisioning raises the sample problems and changes once).
import { sleep } from "k6";
import { config, pick, think } from "./common.js";

const knownErrorWords = ["printer", "VPN", "email", "laptop", "password", "طابعة", "الشبكة", "البريد"];
// Names the CMDB provisioning gives items (tests/load/cmdb/provision-cmdb.mjs): lt-<kind>-<number>.
const ciWords = ["lt-srv-00", "lt-vm-01", "lt-lap-02", "lt-db-0", "lt-app-0", "lt-sw-0", "lt-desk-1", "خدمة"];
const families = ["Hardware", "Application", "BusinessService", "Network", "CloudResource"];

/** Endpoint names per area, so every one gets its own p95 threshold. */
export const problemEndpoints = [
  "problem-list",
  "problem-open",
  "problem-incidents",
  "problem-timeline",
  "known-error-search",
  "incident-known-errors",
  "incident-problems",
];

export const changeEndpoints = ["change-list", "change-open", "change-items", "change-calendar", "change-conflicts"];

/** The what-if simulation is for people only (integrations get 403 people-only): read with web sessions. */
export const changePeopleOnly = ["change-what-if"];

export const cmdbEndpoints = [
  "ci-list",
  "ci-search",
  "ci-lookup",
  "ci-search-index",
  "ci-open",
  "ci-relationships",
  "ci-tickets",
  "ci-impact",
  "ci-map",
];

const problems = () => config().problems || [];
const changes = () => config().changes || [];
const cmdb = () => config().cmdb || {};

/** Which areas have provisioned data (older load.json files have none, and the areas are left out). */
export const areas = () => ({
  problems: problems().length > 0,
  changes: changes().length > 0,
  cmdb: (cmdb().itemIds || []).length > 0 && (cmdb().impactIds || []).length > 0,
});

export const phase2Available = () => Object.values(areas()).some(Boolean);

/** Endpoint names of the areas with data: `read` ones (API keys or web sessions) and people-only ones. */
export function phase2Endpoints() {
  const a = areas();
  return {
    read: [...(a.problems ? problemEndpoints : []), ...(a.changes ? changeEndpoints : []), ...(a.cmdb ? cmdbEndpoints : [])],
    people: a.changes ? changePeopleOnly : [],
  };
}

/** A problem manager or agent looking at a problem, its incidents and the known errors. */
function problemRound(read, rest) {
  read("GET", "problems?open=true&pageSize=25", undefined, "problem-list");
  const problem = pick(problems());
  read("GET", `problems/${problem.id}`, undefined, "problem-open");
  read("GET", `problems/${problem.id}/incidents`, undefined, "problem-incidents");
  if (Math.random() < 0.3) {
    read("GET", `problems/${problem.id}/timeline`, undefined, "problem-timeline");
  }
  rest(3, 8);

  if (Math.random() < 0.5) {
    read("GET", `known-errors?q=${encodeURIComponent(pick(knownErrorWords))}&pageSize=10`, undefined, "known-error-search");
  }
  // The incident page asks for matching known errors and linked problems (task H1).
  const incident = pick(problem.incidentIds && problem.incidentIds.length ? problem.incidentIds : config().incidentIds);
  read("GET", `incidents/${incident}/known-errors`, undefined, "incident-known-errors");
  read("GET", `incidents/${incident}/problems`, undefined, "incident-problems");
}

/** A change manager or board member looking at a change, its items, the calendar and its simulation. */
function changeRound(read, person, rest) {
  read("GET", "changes?open=true&pageSize=25", undefined, "change-list");
  const change = pick(changes());
  read("GET", `changes/${change.id}`, undefined, "change-open");
  read("GET", `changes/${change.id}/configuration-items`, undefined, "change-items");
  // The change page shows the what-if panel (task H5): blast radius, similar changes, predicted risk.
  person("GET", `changes/${change.id}/what-if`, undefined, "change-what-if");
  rest(3, 8);

  if (Math.random() < 0.4) {
    const now = new Date();
    const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
    const to = new Date(from.getTime() + 31 * 24 * 3600 * 1000);
    read("GET", `changes/calendar?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`, undefined, "change-calendar");
  }
  if (Math.random() < 0.4 && change.plannedStart && change.plannedEnd) {
    // What the change form asks before saving a window (POST, but a read: changes.read).
    read(
      "POST",
      "changes/conflicts",
      { changeId: change.id, plannedStart: change.plannedStart, plannedEnd: change.plannedEnd, configurationItemIds: change.configurationItemIds || [] },
      "change-conflicts",
    );
  }
}

/** An agent or CMDB manager finding an item, its relationships, what a fault affects and the map. */
function cmdbRound(read, rest) {
  const word = encodeURIComponent(pick(ciWords));
  if (Math.random() < 0.5) {
    read("GET", `cmdb/items?q=${word}&pageSize=25`, undefined, "ci-search");
  } else {
    read("GET", `cmdb/items?family=${pick(families)}&pageSize=25&page=${1 + Math.floor(Math.random() * 5)}`, undefined, "ci-list");
  }
  if (Math.random() < 0.3) {
    // The configuration item picker on tickets and forms.
    read("GET", `cmdb/lookup?q=${word}`, undefined, "ci-lookup");
  }
  if (Math.random() < 0.3) {
    read("GET", `search?q=${word}&types=ci&pageSize=10`, undefined, "ci-search-index");
  }
  rest(2, 6);

  const id = pick(cmdb().itemIds);
  read("GET", `cmdb/items/${id}`, undefined, "ci-open");
  read("GET", `cmdb/items/${id}/relationships`, undefined, "ci-relationships");
  if (Math.random() < 0.3) {
    read("GET", `cmdb/items/${id}/tickets`, undefined, "ci-tickets");
  }
  rest(3, 8);

  // Impact of a fault of a server or switch (depth 3, the default) and the map around an application or service.
  read("GET", `cmdb/items/${pick(cmdb().impactIds)}/impact?depth=3`, undefined, "ci-impact");
  if (Math.random() < 0.5) {
    const mapIds = cmdb().mapIds && cmdb().mapIds.length ? cmdb().mapIds : cmdb().impactIds;
    read("GET", `cmdb/items/${pick(mapIds)}/map?depth=2`, undefined, "ci-map");
  }
}

/**
 * One round on problems, changes or the CMDB (one area, weighted 30 / 30 / 40 among the areas
 * with data). `read` is apiCall or webCall; `person` is always webCall, for the people-only
 * what-if simulation.
 */
export function phase2Round(read, { pause = true, person = read }) {
  const rest = (min, max) => pause && sleep(think(min, max));
  const a = areas();
  const weighted = [
    ...(a.problems ? [["problems", 3]] : []),
    ...(a.changes ? [["changes", 3]] : []),
    ...(a.cmdb ? [["cmdb", 4]] : []),
  ];
  if (weighted.length === 0) {
    return;
  }
  let roll = Math.random() * weighted.reduce((sum, [, w]) => sum + w, 0);
  const chosen = weighted.find(([, w]) => (roll -= w) < 0) || weighted[weighted.length - 1];
  const area = chosen[0];
  if (area === "problems") {
    problemRound(read, rest);
  } else if (area === "changes") {
    changeRound(read, person, rest);
  } else {
    cmdbRound(read, rest);
  }
  rest(3, 8);
}
