import { defineRouting } from "next-intl/routing";

export const locales = ["en", "ar"] as const;
export type Locale = (typeof locales)[number];

/**
 * Pages carry their language in the URL (/en, /ar). Visiting one remembers it on the device in
 * the language cookie; the display language order for addresses without one is in
 * lib/localization.ts and src/proxy.ts.
 */
export const routing = defineRouting({
  locales,
  defaultLocale: "en",
  localePrefix: "always",
  localeCookie: {
    // Same values as languageCookieName and languageCookieMaxAgeSeconds in lib/localization.ts.
    name: "zelvo-language",
    maxAge: 365 * 24 * 60 * 60,
  },
});

const rtlLocales: ReadonlySet<Locale> = new Set(["ar"]);

export function getDirection(locale: Locale): "rtl" | "ltr" {
  return rtlLocales.has(locale) ? "rtl" : "ltr";
}

export function otherLocale(locale: Locale): Locale {
  return locale === "ar" ? "en" : "ar";
}
