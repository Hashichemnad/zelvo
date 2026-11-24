import { NextResponse, type NextRequest } from "next/server";
import { getDemo } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { getOidcConfiguration, oidc } from "@/lib/auth/oidc";
import { isSameOriginRequest, localeOf, randomSecret, secretsMatch } from "@/lib/auth/security";
import { deleteSession, getSession, sessionCookieName } from "@/lib/auth/session";
import { getSessionStore } from "@/lib/auth/store";
import { demoSwitchKey, demoSwitchSeconds, finishDemoSwitch, type DemoSwitch } from "@/lib/demo-switch";
import { tenantCookieMaxAgeSeconds, tenantCookieName } from "@/lib/tenant";

/**
 * Demo installations only: one-click switching (task D). A POST from the demo panel with the
 * sample organisation (`tenant`) and optionally a `persona`. Ends the current session (Zelvo's,
 * then Keycloak's, so the next sign-in is a different person), remembers the organisation on the
 * device and signs the persona's demo user in through the demo sign-in step. Without a persona it
 * opens the organisation as a visitor. Signed-in users send the session's CSRF token.
 */
export async function POST(request: NextRequest) {
  const config = (await authConfigFor(request));
  if (config.demoTenants.length === 0) {
    return new NextResponse(null, { status: 404 });
  }
  if (!isSameOriginRequest(request.headers.get("origin"), config.appUrl)) {
    return new NextResponse(null, { status: 403 });
  }

  const form = await request.formData();
  const locale = localeOf(String(form.get("locale") ?? ""));
  const tenant = String(form.get("tenant") ?? "");
  const persona = String(form.get("persona") ?? "");
  if (!config.demoTenants.includes(tenant)) {
    return new NextResponse(null, { status: 400 });
  }

  let username: string | null = null;
  if (persona) {
    const demo = await getDemo(tenant);
    username = demo?.users.find((user) => user.persona === persona)?.username ?? null;
    if (!username) {
      return NextResponse.redirect(new URL(`/${locale}/sign-in-problem?reason=failed`, config.appUrl), 303);
    }
  }

  const next: DemoSwitch = { tenant, username, locale };
  const session = await getSession(config);
  if (!session) {
    return finishDemoSwitch(next);
  }
  if (!secretsMatch(String(form.get("csrf") ?? ""), session.data.csrfToken)) {
    return new NextResponse(null, { status: 403 });
  }

  // End this person's sessions first: Keycloak would otherwise keep signing in the same person.
  await deleteSession(session.id, config);
  const ticket = randomSecret();
  await getSessionStore(config.sessionStoreUrl).set(demoSwitchKey(ticket), JSON.stringify(next), demoSwitchSeconds);
  const continueUrl = new URL(`/auth/demo/continue?ticket=${encodeURIComponent(ticket)}`, config.appUrl);
  let target = continueUrl;
  try {
    target = oidc.buildEndSessionUrl(await getOidcConfiguration(config), {
      post_logout_redirect_uri: continueUrl.href,
      ...(session.data.idToken ? { id_token_hint: session.data.idToken } : { client_id: config.clientId }),
    });
  } catch {
    // Keycloak is unreachable: continue; signing in will say so.
  }

  const response = NextResponse.redirect(target, 303);
  response.cookies.delete(sessionCookieName(config));
  response.cookies.set(tenantCookieName, tenant, {
    httpOnly: true,
    secure: config.secureCookies,
    sameSite: "lax",
    path: "/",
    maxAge: tenantCookieMaxAgeSeconds,
  });
  return response;
}
