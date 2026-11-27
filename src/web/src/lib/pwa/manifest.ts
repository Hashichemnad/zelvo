/**
 * The installable web app (module 19, task J1): the web app manifest each host serves, so every
 * customer's app installs with its own name, colours and icon on its own address.
 */

import { defaultLightColour, isColour, lightSurface, type PublicBranding } from "../branding";

/** What the API says about the installable app (GET /api/v1/app). */
export interface AppInfo {
  /** Whether the app may keep the requester's last-viewed tickets and unsent work on the device (decision 13). */
  offlineData: boolean;
  /** The tenant's app icon sizes by name, or null for Zelvo's own icon. */
  icons: Record<string, string> | null;
}

/** The icon sizes Zelvo makes (Zelvo.Tickets.Branding.AppIcons). */
export const appIconNames = ["any-192", "any-512", "maskable-192", "maskable-512", "apple-180"] as const;
export type AppIconName = (typeof appIconNames)[number];

export interface ManifestIcon {
  src: string;
  sizes: string;
  type: "image/png";
  purpose: "any" | "maskable";
}

export interface WebAppManifest {
  id: string;
  name: string;
  short_name: string;
  description: string;
  lang: string;
  dir: "ltr" | "rtl";
  start_url: string;
  scope: string;
  display: "standalone";
  orientation: "any";
  background_color: string;
  theme_color: string;
  icons: ManifestIcon[];
}

/** The address of an app icon: the tenant's (proxied from the API, with its version) or Zelvo's own. */
export function appIconPath(tenant: string, name: AppIconName, app: AppInfo | null): string {
  const api = app?.icons?.[name];
  if (api) {
    const version = /[?&]v=([0-9a-f]+)/.exec(api)?.[1] ?? "0";
    return `/app-icons/${encodeURIComponent(tenant)}/${name}?v=${version}`;
  }
  return `/zelvo-icons/${name}.png`;
}

/** The product name in the language, else the other, else Zelvo's. */
export function productNameIn(branding: PublicBranding | null, locale: string): string {
  const name = locale === "ar" ? branding?.productName.ar ?? branding?.productName.en : branding?.productName.en ?? branding?.productName.ar;
  return name?.trim() || (locale === "ar" ? "زيلفو" : "Zelvo");
}

/** A short name for the home screen (phones show about 12 characters). */
export function shortName(name: string): string {
  const chars = [...name];
  if (chars.length <= 12) return name;
  const firstWord = name.split(/\s+/)[0] ?? name;
  return [...firstWord].length <= 12 ? firstWord : chars.slice(0, 12).join("");
}

/**
 * The manifest in one language. The same `id` in both languages, so switching language never
 * installs a second app; `start_url` opens the home page in the language it was installed in.
 */
export function buildManifest(options: { locale: "en" | "ar"; tenant: string; branding: PublicBranding | null; app: AppInfo | null; description: string }): WebAppManifest {
  const { locale, tenant, branding, app, description } = options;
  const name = productNameIn(branding, locale);
  const theme = branding?.publishedAt && isColour(branding.lightColour) ? branding.lightColour : defaultLightColour;
  const icon = (name: AppIconName, sizes: string, purpose: ManifestIcon["purpose"]): ManifestIcon => ({
    src: appIconPath(tenant, name, app),
    sizes,
    type: "image/png",
    purpose,
  });
  return {
    id: "/",
    name,
    short_name: shortName(name),
    description,
    lang: locale,
    dir: locale === "ar" ? "rtl" : "ltr",
    start_url: `/${locale}?source=app`,
    scope: "/",
    display: "standalone",
    orientation: "any",
    background_color: lightSurface,
    theme_color: theme,
    icons: [
      icon("any-192", "192x192", "any"),
      icon("any-512", "512x512", "any"),
      icon("maskable-192", "192x192", "maskable"),
      icon("maskable-512", "512x512", "maskable"),
    ],
  };
}
