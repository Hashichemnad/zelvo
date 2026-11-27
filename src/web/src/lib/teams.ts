/** The Zelvo tab inside Microsoft Teams (module 19, task J4). */

/** The pages the tab opens: the person's tickets, the agent's list and approvals. */
export const teamsViews = { "my-tickets": "/my-tickets", workspace: "/workspace", approvals: "/approvals" } as const;
export type TeamsView = keyof typeof teamsViews;

export function teamsViewOf(value: unknown): TeamsView {
  return typeof value === "string" && value in teamsViews ? (value as TeamsView) : "my-tickets";
}

/**
 * Teams' own addresses, the only sites that may frame Zelvo's pages in a Teams context
 * (`frame-ancestors`): the classic and the new Teams web clients. The installation can replace the
 * list with `TEAMS_FRAME_ANCESTORS` (space-separated, for sovereign clouds).
 */
export const defaultTeamsFrameAncestors = [
  "https://teams.microsoft.com",
  "https://*.teams.microsoft.com",
  "https://teams.cloud.microsoft",
  "https://*.teams.cloud.microsoft",
];

export function teamsFrameAncestors(env: Record<string, string | undefined> = process.env): string[] {
  const configured = env.TEAMS_FRAME_ANCESTORS?.split(/\s+/).filter((s) => /^https:\/\/(\*\.)?[a-z0-9.-]+$/i.test(s));
  return configured && configured.length > 0 ? configured : defaultTeamsFrameAncestors;
}

/**
 * Where the Teams JavaScript SDK fetches the current list of Teams' own hosts when the tab starts
 * (it falls back to the list it carries after 1.5 seconds). Allowed in `connect-src` on Teams pages only.
 */
export const teamsConnectSources = ["https://res.cdn.office.net"];
