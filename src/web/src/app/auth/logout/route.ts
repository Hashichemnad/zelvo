import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { getOidcConfiguration, oidc } from "@/lib/auth/oidc";
import { isSameOriginRequest, localeOf, secretsMatch } from "@/lib/auth/security";
import { deleteSession, getSession, sessionCookieName, teamsCookieOptions, teamsSessionCookieName } from "@/lib/auth/session";

/**
 * Signs out: a POST from this site with the session's CSRF token. Ends the Zelvo session, then
 * the Keycloak session, and comes back to the home page of the language. After a UAE PASS
 * sign-in, Keycloak returns to /auth/signed-out/uae-pass, which also ends the UAE PASS session,
 * as UAE PASS requires of service providers.
 */
export async function POST(request: NextRequest) {
  const config = (await authConfigFor(request));
  const form = await request.formData();
  const locale = localeOf(String(form.get("locale") ?? ""));
  const home = new URL(`/${locale}`, config.appUrl);

  if (!isSameOriginRequest(request.headers.get("origin"), config.appUrl)) {
    return new NextResponse(null, { status: 403 });
  }

  const session = await getSession(config);
  if (!session) {
    return NextResponse.redirect(home, 303);
  }
  if (!secretsMatch(String(form.get("csrf") ?? ""), session.data.csrfToken)) {
    return new NextResponse(null, { status: 403 });
  }

  // Task J2: this device stops receiving the person's web push (the address the page sent along).
  const pushEndpoint = String(form.get("pushEndpoint") ?? "");
  if (pushEndpoint.startsWith("https://") && pushEndpoint.length <= 2048) {
    try {
      await apiFetch("/api/v1/me/push/subscriptions/remove", session.data.accessToken, config.tenant, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ endpoint: pushEndpoint }),
        signal: AbortSignal.timeout(3_000),
      });
    } catch {
      // The API is unreachable: the device goes when the push service reports it gone.
    }
  }

  await deleteSession(session.id, config);
  // Task J4: inside the Teams tab, come back to the tab (the only page Teams may frame without a session).
  const inTeams = request.cookies.has(teamsSessionCookieName(config)) && !request.cookies.has(sessionCookieName(config));
  const signedOutPage = inTeams ? new URL(`/${locale}/teams/tab`, config.appUrl) : home;
  let target = signedOutPage;
  const afterKeycloak =
    session.data.identityProvider === "uaepass" && !inTeams ? new URL(`/auth/signed-out/uae-pass?locale=${locale}`, config.appUrl) : signedOutPage;
  try {
    target = oidc.buildEndSessionUrl(await getOidcConfiguration(config), {
      post_logout_redirect_uri: afterKeycloak.href,
      ...(session.data.idToken ? { id_token_hint: session.data.idToken } : { client_id: config.clientId }),
    });
  } catch {
    // Keycloak is unreachable: the Zelvo session is gone anyway.
  }

  const response = NextResponse.redirect(target, 303);
  response.cookies.delete(sessionCookieName(config));
  // Task J4: signing out in the Teams tab also ends its partitioned cookie.
  if (request.cookies.has(teamsSessionCookieName(config))) {
    response.cookies.set(teamsSessionCookieName(config), "", teamsCookieOptions(0));
  }
  return response;
}
