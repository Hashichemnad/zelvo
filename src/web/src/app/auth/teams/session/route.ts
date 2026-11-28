import { NextResponse, type NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { isSameOriginRequest, sessionKey, teamsHandoffKey } from "@/lib/auth/security";
import { teamsCookieOptions, teamsSessionCookieName } from "@/lib/auth/session";
import { getSessionStore } from "@/lib/auth/store";

/**
 * Task J4: the Teams tab (a frame on Teams' site) exchanges the pop-up's one-time code for its own
 * session cookie: partitioned and SameSite=None, so it exists only inside Teams. The code works
 * once; the cookie names the same server-side session, so signing out anywhere ends both.
 */
export async function POST(request: NextRequest) {
  const config = await authConfigFor(request);
  if (!isSameOriginRequest(request.headers.get("origin"), config.appUrl)) {
    return new NextResponse(null, { status: 403 });
  }
  let code: unknown;
  try {
    code = ((await request.json()) as { code?: unknown }).code;
  } catch {
    code = null;
  }
  if (typeof code !== "string" || code.length < 16 || code.length > 200) {
    return new NextResponse(null, { status: 400 });
  }
  const store = getSessionStore(config.sessionStoreUrl);
  const sessionId = await store.take(teamsHandoffKey(code));
  if (!sessionId || !(await store.get(sessionKey(sessionId)))) {
    return new NextResponse(null, { status: 401 });
  }
  const response = new NextResponse(null, { status: 204 });
  response.cookies.set(teamsSessionCookieName(config), sessionId, teamsCookieOptions(config.absoluteTimeoutSeconds));
  return response;
}
