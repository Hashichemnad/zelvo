import { NextResponse, type NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { isSameOriginRequest, randomSecret, secretsMatch, teamsHandoffKey } from "@/lib/auth/security";
import { getSession } from "@/lib/auth/session";
import { getSessionStore } from "@/lib/auth/store";

/** How long the pop-up's code may wait for the tab: long enough for Teams to pass it on. */
const handoffSeconds = 120;

/**
 * Task J4: in the sign-in pop-up, after signing in: a one-time code for this session (POST from
 * this site with the session's CSRF token). Only the code's hash is kept, for two minutes; the
 * tab exchanges it once for its own partitioned cookie.
 */
export async function POST(request: NextRequest) {
  const config = await authConfigFor(request);
  if (!isSameOriginRequest(request.headers.get("origin"), config.appUrl)) {
    return new NextResponse(null, { status: 403 });
  }
  const session = await getSession(config);
  if (!session) {
    return new NextResponse(null, { status: 401 });
  }
  if (!secretsMatch(request.headers.get("x-csrf-token"), session.data.csrfToken)) {
    return new NextResponse(null, { status: 403 });
  }
  const code = randomSecret();
  await getSessionStore(config.sessionStoreUrl).set(teamsHandoffKey(code), session.id, handoffSeconds);
  return NextResponse.json({ code }, { headers: { "Cache-Control": "no-store" } });
}
