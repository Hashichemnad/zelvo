import { NextResponse, type NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { getOidcConfiguration, oidc } from "@/lib/auth/oidc";
import { isSameOriginRequest, localeOf, secretsMatch } from "@/lib/auth/security";
import { deleteSession, getSession, sessionCookieName } from "@/lib/auth/session";

/**
 * Signs out: a POST from this site with the session's CSRF token. Ends the Zelvo session, then
 * the Keycloak session, and comes back to the home page of the language. After a UAE PASS
 * sign-in, Keycloak returns to /auth/signed-out/uae-pass, which also ends the UAE PASS session,
 * as UAE PASS requires of service providers.
 */
export async function POST(request: NextRequest) {
  const config = authConfigFor(request);
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

  await deleteSession(session.id, config);
  let target = home;
  const afterKeycloak =
    session.data.identityProvider === "uaepass" ? new URL(`/auth/signed-out/uae-pass?locale=${locale}`, config.appUrl) : home;
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
  return response;
}
