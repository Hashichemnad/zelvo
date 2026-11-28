import { NextResponse, type NextRequest } from "next/server";
import { authConfigFor } from "@/lib/auth/config";
import { teamsViewOf } from "@/lib/teams";

/**
 * Task J4: the address of the Zelvo tabs in the Teams app manifest. Teams fills in its own language
 * (`{locale}`, for example "ar-ae"); the tab opens in Arabic for Arabic and in English otherwise.
 */
export async function GET(request: NextRequest) {
  const config = await authConfigFor(request);
  const params = request.nextUrl.searchParams;
  const language = params.get("locale")?.toLowerCase().startsWith("ar") ? "ar" : "en";
  const tab = new URL(`/${language}/teams/tab`, config.appUrl);
  tab.search = new URLSearchParams({ view: teamsViewOf(params.get("view")) }).toString();
  return NextResponse.redirect(tab, 303);
}
