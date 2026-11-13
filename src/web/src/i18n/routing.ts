import { defineRouting } from "next-intl/routing";

export const locales = ["en", "ar"] as const;
export type Locale = (typeof locales)[number];

/**
 * Display language order (LANG-1, LANG-2): the user's setting, then the tenant default, then the
 * browser language, then English. User and tenant settings arrive with the identity and settings
 * modules; until then the saved language cookie and the browser language decide, falling back to
 * English.
 */
export const routing = defineRouting({
  locales,
  defaultLocale: "en",
  localePrefix: "always",
});

const rtlLocales: ReadonlySet<Locale> = new Set(["ar"]);

export function getDirection(locale: Locale): "rtl" | "ltr" {
  return rtlLocales.has(locale) ? "rtl" : "ltr";
}

export function otherLocale(locale: Locale): Locale {
  return locale === "ar" ? "en" : "ar";
}
