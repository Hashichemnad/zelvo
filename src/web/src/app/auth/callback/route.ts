import { NextResponse, type NextRequest } from "next/server";
import { getCurrentUser, getSignInRefusal } from "@/lib/api";
import { authConfigFor, getAuthConfig } from "@/lib/auth/config";
import { getOidcConfiguration, oidc } from "@/lib/auth/oidc";
import { pendingSignInKey, secretsMatch } from "@/lib/auth/security";
import { cookieOptions, createSession, sessionCookieName, signInCookieName } from "@/lib/auth/session";
import { getSessionStore } from "@/lib/auth/store";
import { hasLanguagePrefix, languageCookieMaxAgeSeconds, languageCookieName, withLanguage } from "@/lib/localization";
import type { PendingSignIn } from "@/lib/auth/sign-in";

/**
 * Keycloak sends the browser back here. Checks state (bound to this browser by a cookie), PKCE
 * and nonce, exchanges the code, and starts a server-side session. Staff whose roles need a
 * second factor and who signed in without one are sent straight back to sign in with it.
 * Signed-in users then see their profile language, or else the tenant default (LANG-2).
 */
export async function GET(request: NextRequest) {
  const requestConfig = (await authConfigFor(request));
  const params = request.nextUrl.searchParams;
  const state = params.get("state");
  const cookieState = request.cookies.get(signInCookieName(requestConfig))?.value;
  const pendingRaw = state ? await getSessionStore(requestConfig.sessionStoreUrl).take(pendingSignInKey(state)) : null;
  const pending = pendingRaw ? (JSON.parse(pendingRaw) as PendingSignIn) : null;
  // Finish at the realm the sign-in started at (a demo installation serves several).
  const config = pending?.tenant && pending.tenant !== requestConfig.tenant ? getAuthConfig(process.env, pending.tenant) : requestConfig;
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
    // A UAE PASS account below the level this account needs (GOV-1) gets its own explanation.
    const refusal = await getSignInRefusal(tokens.access_token, config.tenant);
    return problem(refusal === "uae-pass-level-too-low" ? "uaePassLevel" : "refused");
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
  // Task J3: the outcome of "add a passkey" (Keycloak's kc_action_status), for Account → Security.
  const actionStatus = params.get("kc_action_status");
  if (actionStatus === "success" || actionStatus === "cancelled" || actionStatus === "error") {
    returnTo.searchParams.set("passkey", actionStatus === "success" ? "added" : actionStatus);
  }
  const response = NextResponse.redirect(returnTo, 303);
  response.cookies.delete(signInCookieName(config));
  response.cookies.set(sessionCookieName(config), session.id, cookieOptions(config, config.absoluteTimeoutSeconds));
  response.cookies.set(languageCookieName, language, { path: "/", sameSite: "lax", maxAge: languageCookieMaxAgeSeconds });
  return response;
}
