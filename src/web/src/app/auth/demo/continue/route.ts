import { NextResponse, type NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { getSessionStore } from "@/lib/auth/store";
import { demoSwitchKey, finishDemoSwitch, type DemoSwitch } from "@/lib/demo-switch";

/**
 * Demo installations only: Keycloak comes back here after ending the previous person's session;
 * the ticket (random, used once, valid five minutes) says who to sign in next.
 */
export async function GET(request: NextRequest) {
  const config = authConfigFor(request);
  const ticket = request.nextUrl.searchParams.get("ticket");
  const raw = ticket && config.demoTenants.length > 0 ? await getSessionStore(config.sessionStoreUrl).take(demoSwitchKey(ticket)) : null;
  if (!raw) {
    return NextResponse.redirect(new URL("/", config.appUrl), 303);
  }
  return finishDemoSwitch(JSON.parse(raw) as DemoSwitch);
}
