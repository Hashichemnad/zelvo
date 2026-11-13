import createMiddleware from "next-intl/middleware";
import { NextResponse, type NextRequest } from "next/server";
import { routing } from "./i18n/routing";
import { getLocalizationSettings } from "./lib/api";
import { getAuthConfig } from "./lib/auth/config";
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
  if (hasLanguagePrefix(pathname)) {
    return intl(request);
  }

  const config = getAuthConfig();
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
  matcher: "/((?!api|auth/|bff/|healthz|_next|_vercel|.*\\..*).*)",
};
