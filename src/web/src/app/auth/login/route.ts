import type { NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { localeOf, safeReturnTo } from "@/lib/auth/security";
import { startSignIn } from "@/lib/auth/sign-in";

export type { PendingSignIn } from "@/lib/auth/sign-in";

/**
 * Starts sign-in at the tenant's Keycloak realm (authorization code flow with PKCE). With
 * `mfa=1` the user is asked for a second factor (`acr_values=mfa`). Task J3: `action=passkey`
 * asks Keycloak to add a passkey to the signed-in account (`kc_action=webauthn-register-passwordless`).
 */
export async function GET(request: NextRequest) {
  const config = (await authConfigFor(request));
  const params = request.nextUrl.searchParams;
  const locale = localeOf(params.get("locale"));
  return startSignIn(config, {
    locale,
    returnTo: safeReturnTo(params.get("returnTo"), locale),
    mfa: params.get("mfa") === "1",
    ...(params.get("action") === "passkey" ? { action: "webauthn-register-passwordless" as const } : {}),
  });
}
