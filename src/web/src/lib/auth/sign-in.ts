import "server-only";
import { NextResponse } from "next/server";
import type { Locale } from "@/i18n/routing";
import type { AuthConfig } from "./config";
import { getOidcConfiguration, oidc } from "./oidc";
import { pendingSignInKey } from "./security";
import { cookieOptions, signInCookieName } from "./session";
import { getSessionStore } from "./store";

/** A sign-in in progress, kept on the server for ten minutes and used once. */
export interface PendingSignIn {
  codeVerifier: string;
  nonce: string;
  returnTo: string;
  locale: string;
  mfa: boolean;
  /** The tenant (realm) the sign-in started at; the callback finishes it there. */
  tenant?: string;
}

const pendingSeconds = 600;

export interface SignInOptions {
  locale: Locale;
  /** A path on this site, already checked with safeReturnTo. */
  returnTo: string;
  /** Ask for a second factor (`acr_values=mfa`). */
  mfa: boolean;
  /** Demo installations: `zelvo-demo:<username>` signs that demo user in with one click. */
  loginHint?: string;
  /**
   * Task J3: a Keycloak application-initiated action (`kc_action`), only "add a passkey"
   * (`webauthn-register-passwordless`), started from Account → Security.
   */
  action?: "webauthn-register-passwordless";
}

/**
 * Starts sign-in at the tenant's Keycloak realm (authorization code flow with PKCE, state and
 * nonce). Returns the redirect to Keycloak, or to the sign-in problem page when Keycloak cannot
 * be reached.
 */
export async function startSignIn(config: AuthConfig, options: SignInOptions): Promise<NextResponse> {
  let configuration;
  try {
    configuration = await getOidcConfiguration(config);
  } catch {
    return NextResponse.redirect(new URL(`/${options.locale}/sign-in-problem`, config.appUrl), 303);
  }

  const codeVerifier = oidc.randomPKCECodeVerifier();
  const state = oidc.randomState();
  const nonce = oidc.randomNonce();
  const pending: PendingSignIn = {
    codeVerifier,
    nonce,
    returnTo: options.returnTo,
    locale: options.locale,
    mfa: options.mfa,
    tenant: config.tenant,
  };
  await getSessionStore(config.sessionStoreUrl).set(pendingSignInKey(state), JSON.stringify(pending), pendingSeconds);

  const authorizationUrl = oidc.buildAuthorizationUrl(configuration, {
    redirect_uri: new URL("/auth/callback", config.appUrl).href,
    scope: "openid profile email",
    code_challenge: await oidc.calculatePKCECodeChallenge(codeVerifier),
    code_challenge_method: "S256",
    state,
    nonce,
    ui_locales: options.locale,
    ...(options.mfa ? { acr_values: "mfa" } : {}),
    ...(options.loginHint ? { login_hint: options.loginHint } : {}),
    ...(options.action ? { kc_action: options.action } : {}),
  });

  const response = NextResponse.redirect(authorizationUrl, 303);
  response.cookies.set(signInCookieName(config), state, cookieOptions(config, pendingSeconds));
  return response;
}
