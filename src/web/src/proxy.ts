import createMiddleware from "next-intl/middleware";
import { NextResponse, type NextRequest } from "next/server";
import { routing } from "./i18n/routing";
import { getLocalizationSettings, getWidgetOrigins } from "./lib/api";
import { authConfigFor } from "./lib/auth/config";
import { sessionCookieName, teamsSessionCookieName } from "./lib/auth/session";
import { addRequestHeaders, contentSecurityPolicy, newNonce } from "./lib/csp";
import { hasLanguagePrefix, languageCookieName, resolveLanguage, withLanguage } from "./lib/localization";
import { teamsFrameAncestors } from "./lib/teams";

const intl = createMiddleware(routing);

/**
 * Addresses without a language (such as "/") go to the language from the display language order
 * (see lib/localization.ts); addresses with one are served in it, and next-intl remembers it in
 * the device's language cookie.
 */
export default async function proxy(request: NextRequest) {
  // Task F7: a Content-Security-Policy with a fresh nonce on every page (lib/csp.ts). Next.js reads
  // the nonce from the request's policy and puts it on its scripts; layouts read `x-nonce`.
  const nonce = newNonce();
  const config = await authConfigFor(request);
  const policy = (frameAncestors?: string[]) =>
    contentSecurityPolicy({
      nonce,
      frameAncestors,
      development: process.env.NODE_ENV === "development",
      upgradeInsecureRequests: config.appUrl.protocol === "https:",
    });
  const pageHeaders = (csp: string) => ({ "content-security-policy": csp, "x-nonce": nonce });

  const { pathname, search } = request.nextUrl;
  // The chat widget's frame (task E2): only the websites listed for the widget may frame it.
  const widget = /^\/chat-widget\/(en|ar)\/([0-9a-f-]{36})$/i.exec(pathname);
  if (widget) {
    const origins = await getWidgetOrigins(config.tenant, widget[2] ?? "");
    const csp = policy(["'self'", ...origins]);
    const headers = new Headers(request.headers);
    for (const [name, value] of Object.entries(pageHeaders(csp))) {
      headers.set(name, value);
    }
    const response = NextResponse.next({ request: { headers } });
    response.headers.set("Content-Security-Policy", csp);
    return response;
  }
  if (hasLanguagePrefix(pathname)) {
    // Task J4: Teams may frame the Teams tab's own pages, and any page opened with the Teams tab's
    // partitioned session cookie (which only exists inside Teams); every other page stays unframed.
    const inTeams = /^\/(en|ar)\/teams\/tab(\/|$)/.test(pathname) || request.cookies.has(teamsSessionCookieName(config));
    const csp = policy(inTeams ? teamsFrameAncestors() : undefined);
    const response = intl(request);
    addRequestHeaders(response, request, pageHeaders(csp));
    response.headers.set("Content-Security-Policy", csp);
    return response;
  }

  const deviceLanguage = request.cookies.get(languageCookieName)?.value;
  const signedIn = request.cookies.has(sessionCookieName(config));
  // Only first visits (no language cookie yet) need the tenant default; ask the API directly.
  const tenantDefault =
    deviceLanguage === undefined
      ? (await getLocalizationSettings(config.tenant, { useCache: false }))?.defaultLanguage
      : undefined;
  const language = resolveLanguage({
    deviceLanguage,
    signedIn,
    acceptLanguage: request.headers.get("accept-language"),
    tenantDefault,
  });
  const response = NextResponse.redirect(new URL(`${withLanguage(pathname, language)}${search}`, request.url));
  response.headers.set("Content-Security-Policy", policy());
  return response;
}

export const config = {
  // Everything except API routes, sign-in routes (/auth/...), the API proxy (/bff/...), the health
  // endpoint, Next internals and files with an extension (task J1: also the app icons, the manifest
  // and the service worker).
  matcher: "/((?!api|auth/|bff/|branding/|app-icons/|payments/callback/|healthz|_next|_vercel|.*\\..*).*)",
};
