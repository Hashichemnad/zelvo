/**
 * Display language order (LANG-1, LANG-2, LANG-3; product owner decision 2026-11-13).
 *
 * - Signed in: the profile language, then the tenant default, then English. Applied at sign-in
 *   and when the user saves a new profile language, by setting the device's language cookie.
 * - Not signed in: the browser language when it is English or Arabic, then the tenant default,
 *   then English.
 * - The language switch applies to the current visit and is remembered on the device (the same
 *   cookie). It never changes the profile.
 *
 * Pages carry the language in the URL (/en, /ar); this order decides where an address without
 * one leads, such as "/".
 */

import { locales, type Locale } from "@/i18n/routing";

/** The device's language: written by the language switch, sign-in and saved preferences. */
export const languageCookieName = "zelvo-language";

/** Remembered for a year on the device. */
export const languageCookieMaxAgeSeconds = 365 * 24 * 60 * 60;

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (locales as readonly string[]).includes(value);
}

/**
 * The first English or Arabic language in an Accept-Language header, by preference (q) and then
 * order, or null when the browser asks for neither. "ar-AE" counts as Arabic.
 */
export function browserLanguage(acceptLanguage: string | null | undefined): Locale | null {
  if (!acceptLanguage) {
    return null;
  }
  const ranked = acceptLanguage
    .split(",")
    .map((part, index) => {
      const [tag, ...params] = part.trim().split(";");
      const q = params.map((p) => p.trim()).find((p) => p.startsWith("q="));
      const quality = q ? Number(q.slice(2)) : 1;
      return { language: (tag ?? "").trim().toLowerCase().split("-")[0], quality: Number.isFinite(quality) ? quality : 0, index };
    })
    .filter((entry) => entry.quality > 0)
    .sort((a, b) => b.quality - a.quality || a.index - b.index);
  return ranked.map((entry) => entry.language).find(isLocale) ?? null;
}

export interface LanguageInputs {
  /** The language cookie of this device (the visit's choice), if any. */
  deviceLanguage: string | null | undefined;
  /** Whether a session cookie is present. */
  signedIn: boolean;
  /** The browser's Accept-Language header. */
  acceptLanguage: string | null | undefined;
  /** The tenant default, or null when it could not be read. */
  tenantDefault: string | null | undefined;
}

/** Language for an address without one. */
export function resolveLanguage(inputs: LanguageInputs): Locale {
  if (isLocale(inputs.deviceLanguage)) {
    return inputs.deviceLanguage;
  }
  if (!inputs.signedIn) {
    const browser = browserLanguage(inputs.acceptLanguage);
    if (browser) {
      return browser;
    }
  }
  return isLocale(inputs.tenantDefault) ? inputs.tenantDefault : "en";
}

/** Whether the path starts with a language, such as /ar or /en/account. */
export function hasLanguagePrefix(pathname: string): boolean {
  return locales.some((locale) => pathname === `/${locale}` || pathname.startsWith(`/${locale}/`));
}

/** The same path in another language: /en/account → /ar/account. Paths without one get it added. */
export function withLanguage(pathname: string, locale: Locale): string {
  const match = locales.find((l) => pathname === `/${l}` || pathname.startsWith(`/${l}/`));
  const rest = match ? pathname.slice(match.length + 1) : pathname;
  return `/${locale}${rest === "/" ? "" : rest}`;
}
