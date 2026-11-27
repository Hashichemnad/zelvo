import { NextResponse, type NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { localeOf } from "@/lib/auth/security";

/**
 * Task J4: the first page of the Teams sign-in pop-up (Teams opens it with `authentication.authenticate`).
 * The pop-up is a window of its own, so this is the usual first-party sign-in; it ends on the
 * hand-off page, which passes a one-time code back to the tab.
 */
export async function GET(request: NextRequest) {
  const config = await authConfigFor(request);
  const locale = localeOf(request.nextUrl.searchParams.get("locale"));
  const login = new URL("/auth/login", config.appUrl);
  login.search = new URLSearchParams({ locale, returnTo: `/${locale}/teams/auth-end` }).toString();
  return NextResponse.redirect(login, 303);
}
