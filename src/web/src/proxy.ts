import createMiddleware from "next-intl/middleware";
import { NextResponse, type NextRequest } from "next/server";
import { routing } from "./i18n/routing";
import { getLocalizationSettings, getWidgetOrigins } from "./lib/api";
import { authConfigFor } from "./lib/auth/config";
import { sessionCookieName } from "./lib/auth/session";
import { hasLanguagePrefix, languageCookieName, resolveLanguage, withLanguage } from "./lib/localization";

const intl = createMiddleware(routing);

/**
 * Addresses without a language (such as "/") go to the language from the display language order
 * (see lib/localization.ts); addresses with one are served in it, and next-intl remembers it in
 * the device's language cookie.
 */
export default async function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  // The chat widget's frame (task E2): only the websites listed for the widget may frame it.
  const widget = /^\/chat-widget\/(en|ar)\/([0-9a-f-]{36})$/i.exec(pathname);
  if (widget) {
    const origins = await getWidgetOrigins((await authConfigFor(request)).tenant, widget[2] ?? "");
    const response = NextResponse.next();
    response.headers.set("Content-Security-Policy", `frame-ancestors 'self' ${origins.join(" ")}`.trim());
    return response;
  }
  if (hasLanguagePrefix(pathname)) {
    return intl(request);
  }

  const config = (await authConfigFor(request));
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
  return NextResponse.redirect(new URL(`${withLanguage(pathname, language)}${search}`, request.url));
}

export const config = {
  // Everything except API routes, sign-in routes (/auth/...), the API proxy (/bff/...), the health
  // endpoint, Next internals and files with an extension.
  matcher: "/((?!api|auth/|bff/|branding/|healthz|_next|_vercel|.*\\..*).*)",
};
