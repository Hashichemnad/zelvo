import { NextResponse, type NextRequest } from "next/server";
import { getSignInMethods } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { localeOf } from "@/lib/auth/security";

/**
 * Last step of signing out after a UAE PASS sign-in (GOV-1): Keycloak has ended its session and
 * sends the browser here; this ends the UAE PASS session and comes back to the home page. The
 * UAE PASS address comes from the API (the tenant's chosen environment), never from the request,
 * so this cannot redirect anywhere else.
 */
export async function GET(request: NextRequest) {
  const config = authConfigFor(request);
  const locale = localeOf(request.nextUrl.searchParams.get("locale"));
  const home = new URL(`/${locale}`, config.appUrl);
  const methods = await getSignInMethods(config.tenant);
  const signOutUrl = methods?.uaePass.enabled ? methods.uaePass.signOutUrl : null;
  if (!signOutUrl) {
    return NextResponse.redirect(home, 303);
  }
  const target = new URL(signOutUrl);
  target.searchParams.set("redirect_uri", home.href);
  return NextResponse.redirect(target, 303);
}
