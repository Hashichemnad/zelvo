// @vitest-environment node
import { NextRequest } from "next/server";
import { sessionKey, teamsHandoffKey } from "@/lib/auth/security";
import { getSessionStore } from "@/lib/auth/store";
import { POST as exchange } from "./session/route";
import { GET as tab } from "./tab/route";

vi.mock("server-only", () => ({}));

const origin = "http://localhost:3000";

function post(body: unknown, headers: Record<string, string> = { origin }) {
  return new NextRequest(`${origin}/auth/teams/session`, { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });
}

describe("Teams sign-in hand-off (task J4)", () => {
  it("exchanges a pop-up's code once for a partitioned SameSite=None cookie naming the same session", async () => {
    const store = getSessionStore(undefined);
    const sessionId = crypto.randomUUID();
    const code = crypto.randomUUID();
    await store.set(sessionKey(sessionId), JSON.stringify({ tenant: "default" }), 60);
    await store.set(teamsHandoffKey(code), sessionId, 60);

    // Another site cannot make the exchange.
    expect((await exchange(post({ code }, { origin: "https://evil.example" }))).status).toBe(403);

    const response = await exchange(post({ code }));
    expect(response.status).toBe(204);
    const cookie = response.headers.get("set-cookie") ?? "";
    expect(cookie).toContain(`zelvo-teams=${sessionId}`);
    expect(cookie.toLowerCase()).toContain("samesite=none");
    expect(cookie.toLowerCase()).toContain("partitioned");
    expect(cookie.toLowerCase()).toContain("secure");
    expect(cookie.toLowerCase()).toContain("httponly");

    // The code works once.
    expect((await exchange(post({ code }))).status).toBe(401);
    expect((await exchange(post({ code: "short" }))).status).toBe(400);
  });

  it("opens the tab in Teams' language", async () => {
    const arabic = await tab(new NextRequest(`${origin}/auth/teams/tab?view=approvals&locale=ar-ae`));
    expect(arabic.headers.get("location")).toBe(`${origin}/ar/teams/tab?view=approvals`);
    const other = await tab(new NextRequest(`${origin}/auth/teams/tab?view=elsewhere&locale=fr-fr`));
    expect(other.headers.get("location")).toBe(`${origin}/en/teams/tab?view=my-tickets`);
  });
});
