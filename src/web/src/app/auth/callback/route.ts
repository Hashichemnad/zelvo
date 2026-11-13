import { NextResponse, type NextRequest } from "next/server";
import { getCurrentUser } from "@/lib/api";
import { getAuthConfig } from "@/lib/auth/config";
import { getOidcConfiguration, oidc } from "@/lib/auth/oidc";
import { pendingSignInKey, secretsMatch } from "@/lib/auth/security";
import { cookieOptions, createSession, sessionCookieName, signInCookieName } from "@/lib/auth/session";
import { getSessionStore } from "@/lib/auth/store";
import { hasLanguagePrefix, languageCookieMaxAgeSeconds, languageCookieName, withLanguage } from "@/lib/localization";
import type { PendingSignIn } from "../login/route";

/**
 * Keycloak sends the browser back here. Checks state (bound to this browser by a cookie), PKCE
 * and nonce, exchanges the code, and starts a server-side session. Staff whose roles need a
 * second factor and who signed in without one are sent straight back to sign in with it.
 * Signed-in users then see their profile language, or else the tenant default (LANG-2).
 */
export async function GET(request: NextRequest) {
  const config = getAuthConfig();
  const params = request.nextUrl.searchParams;
  const state = params.get("state");
  const cookieState = request.cookies.get(signInCookieName(config))?.value;
  const pendingRaw = state ? await getSessionStore(config.sessionStoreUrl).take(pendingSignInKey(state)) : null;
  const pending = pendingRaw ? (JSON.parse(pendingRaw) as PendingSignIn) : null;
  const locale = pending?.locale ?? "en";

  const problem = (reason: string) => {
    const response = NextResponse.redirect(new URL(`/${locale}/sign-in-problem?reason=${reason}`, config.appUrl), 303);
    response.cookies.delete(signInCookieName(config));
    return response;
  };

  if (!pending || !state || !secretsMatch(state, cookieState)) {
    return problem("expired");
  }
  if (params.get("error")) {
    // The user cancelled, or Keycloak could not complete the sign-in.
    return problem(params.get("error") === "access_denied" ? "cancelled" : "failed");
  }

  let tokens;
  try {
    // The address Keycloak redirected to, as the browser saw it (not this server's own host).
    const callbackUrl = new URL(`/auth/callback${request.nextUrl.search}`, config.appUrl);
    tokens = await oidc.authorizationCodeGrant(await getOidcConfiguration(config), callbackUrl, {
      pkceCodeVerifier: pending.codeVerifier,
      expectedState: state,
      expectedNonce: pending.nonce,
      idTokenExpected: true,
    });
  } catch {
    return problem("failed");
  }

  const user = await getCurrentUser(tokens.access_token, config.tenant);
  if (!user) {
    return problem("refused");
  }
  if (user.mfa.required) {
    if (pending.mfa) {
      // Asked for a second factor and still did not get one.
      return problem("mfa");
    }
    const stepUp = new URL("/auth/login", config.appUrl);
    stepUp.search = new URLSearchParams({ mfa: "1", locale, returnTo: pending.returnTo }).toString();
    const response = NextResponse.redirect(stepUp, 303);
    response.cookies.delete(signInCookieName(config));
    return response;
  }

  const session = await createSession(tokens, config);
  const language = user.preferences.effective.language;
  const returnTo = new URL(pending.returnTo, config.appUrl);
  if (hasLanguagePrefix(returnTo.pathname)) {
    returnTo.pathname = withLanguage(returnTo.pathname, language);
  }
  const response = NextResponse.redirect(returnTo, 303);
  response.cookies.delete(signInCookieName(config));
  response.cookies.set(sessionCookieName(config), session.id, cookieOptions(config, config.absoluteTimeoutSeconds));
  response.cookies.set(languageCookieName, language, { path: "/", sameSite: "lax", maxAge: languageCookieMaxAgeSeconds });
  return response;
}
