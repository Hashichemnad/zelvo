import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { getSession } from "@/lib/auth/session";
import { isLocale, languageCookieName } from "@/lib/localization";

/**
 * Microsoft or Google sends an administrator back here after they signed in to a mailbox
 * (Settings → Mailboxes, task E2). The API checks the state, exchanges the code and keeps only the
 * refresh token; the administrator goes back to the mailbox's page.
 */
export async function GET(request: NextRequest) {
  const config = (await authConfigFor(request));
  const params = request.nextUrl.searchParams;
  const state = params.get("state") ?? "";
  const code = params.get("code");
  const cookieLocale = request.cookies.get(languageCookieName)?.value;
  const locale = isLocale(cookieLocale) ? cookieLocale : "en";
  const mailboxId = /^([0-9a-f]{32})\./.exec(state)?.[1];
  const mailboxPath = mailboxId
    ? `/${locale}/settings/mailboxes/${mailboxId.replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, "$1-$2-$3-$4-$5")}`
    : `/${locale}/settings/mailboxes`;
  const back = (result: "ok" | "failed") => NextResponse.redirect(new URL(`${mailboxPath}?signIn=${result}`, config.appUrl), 303);

  const session = await getSession(config).catch(() => null);
  if (!session) {
    return NextResponse.redirect(new URL(`/auth/login?returnTo=${encodeURIComponent(mailboxPath)}`, config.appUrl), 303);
  }
  if (!code || !mailboxId || params.get("error")) {
    return back("failed");
  }
  try {
    const response = await apiFetch("/api/v1/mailboxes/sign-in/complete", session.data.accessToken, config.tenant, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ state, code }),
    });
    return back(response.ok ? "ok" : "failed");
  } catch {
    return back("failed");
  }
}
