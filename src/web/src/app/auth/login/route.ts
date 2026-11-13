import { NextResponse, type NextRequest } from "next/server";
import { getAuthConfig } from "@/lib/auth/config";
import { getOidcConfiguration, oidc } from "@/lib/auth/oidc";
import { localeOf, pendingSignInKey, safeReturnTo } from "@/lib/auth/security";
import { cookieOptions, signInCookieName } from "@/lib/auth/session";
import { getSessionStore } from "@/lib/auth/store";

/** A sign-in in progress, kept on the server for ten minutes and used once. */
export interface PendingSignIn {
  codeVerifier: string;
  nonce: string;
  returnTo: string;
  locale: string;
  mfa: boolean;
}

const pendingSeconds = 600;

/**
 * Starts sign-in at the tenant's Keycloak realm (authorization code flow with PKCE). With
 * `mfa=1` the user is asked for a second factor (`acr_values=mfa`).
 */
export async function GET(request: NextRequest) {
  const config = getAuthConfig();
  const params = request.nextUrl.searchParams;
  const locale = localeOf(params.get("locale"));
  const mfa = params.get("mfa") === "1";

  let configuration;
  try {
    configuration = await getOidcConfiguration(config);
  } catch {
    return NextResponse.redirect(new URL(`/${locale}/sign-in-problem`, config.appUrl), 303);
  }

  const codeVerifier = oidc.randomPKCECodeVerifier();
  const state = oidc.randomState();
  const nonce = oidc.randomNonce();
  const pending: PendingSignIn = {
    codeVerifier,
    nonce,
    returnTo: safeReturnTo(params.get("returnTo"), locale),
    locale,
    mfa,
  };
  await getSessionStore(config.sessionStoreUrl).set(pendingSignInKey(state), JSON.stringify(pending), pendingSeconds);

  const authorizationUrl = oidc.buildAuthorizationUrl(configuration, {
    redirect_uri: new URL("/auth/callback", config.appUrl).href,
    scope: "openid profile email",
    code_challenge: await oidc.calculatePKCECodeChallenge(codeVerifier),
    code_challenge_method: "S256",
    state,
    nonce,
    ui_locales: locale,
    ...(mfa ? { acr_values: "mfa" } : {}),
  });

  const response = NextResponse.redirect(authorizationUrl, 303);
  response.cookies.set(signInCookieName(config), state, cookieOptions(config, pendingSeconds));
  return response;
}
