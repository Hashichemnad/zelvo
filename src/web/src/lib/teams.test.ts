import { contentSecurityPolicy } from "./csp";
import { defaultTeamsFrameAncestors, teamsFrameAncestors, teamsViewOf } from "./teams";

describe("Teams tab (task J4)", () => {
  it("lets only Teams' addresses frame the tab, unless the installation names others", () => {
    expect(teamsFrameAncestors({})).toEqual(defaultTeamsFrameAncestors);
    expect(teamsFrameAncestors({ TEAMS_FRAME_ANCESTORS: "https://teams.microsoft.us https://*.gov.teams.microsoft.us" })).toEqual([
      "https://teams.microsoft.us",
      "https://*.gov.teams.microsoft.us",
    ]);
    // Anything that is not an https origin is ignored.
    expect(teamsFrameAncestors({ TEAMS_FRAME_ANCESTORS: "* 'self' http://evil.example" })).toEqual(defaultTeamsFrameAncestors);
    const policy = contentSecurityPolicy({ nonce: "n", frameAncestors: teamsFrameAncestors({}) });
    expect(policy).toContain("frame-ancestors https://teams.microsoft.com https://*.teams.microsoft.com https://teams.cloud.microsoft");
    expect(contentSecurityPolicy({ nonce: "n" })).toContain("frame-ancestors 'none'");
  });

  it("opens only the tab's own pages", () => {
    expect(teamsViewOf("approvals")).toBe("approvals");
    expect(teamsViewOf("workspace")).toBe("workspace");
    expect(teamsViewOf("https://evil.example")).toBe("my-tickets");
    expect(teamsViewOf(undefined)).toBe("my-tickets");
  });
});
