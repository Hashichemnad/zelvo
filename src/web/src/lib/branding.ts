/** White-label branding (module 11, task F1): the shapes the API returns and the safe CSS of a tenant's look. */

import type { Bilingual } from "./incidents";

export type BrandingSlot = "LogoLight" | "LogoDark" | "Favicon" | "LoginImage" | "AppIcon";
export const brandingSlots: readonly BrandingSlot[] = ["LogoLight", "LogoDark", "Favicon", "LoginImage", "AppIcon"];

/** Fonts the branding may choose (bundled, Arabic and Latin): key → CSS family. Matches Zelvo.Data.Branding.BrandingFonts. */
export const brandingFonts: Readonly<Record<string, string>> = {
  "ibm-plex-sans-arabic": "IBM Plex Sans Arabic",
  cairo: "Cairo",
  tajawal: "Tajawal",
  almarai: "Almarai",
};

export const defaultLightColour = "#0f5c8a";
export const defaultDarkColour = "#7cc4f0";
export const lightSurface = "#ffffff";
export const darkSurface = "#141a20";
export const minimumContrast = 4.5;

export interface PublicBranding {
  productName: Bilingual;
  lightColour: string;
  darkColour: string;
  font: string;
  fontFamily: string;
  /** Image addresses on the API (camel-case slots), or null. */
  images: Record<string, string | null>;
  publishedAt: string | null;
}

export interface BrandingDraft {
  productName: Bilingual;
  lightColour: string;
  darkColour: string;
  font: string;
}

export interface ContrastCheck {
  key: "light.text" | "light.button" | "dark.text" | "dark.button";
  foreground: string;
  background: string;
  ratio: number;
  passed: boolean;
}

export interface BrandingImage {
  id: string;
  fileName: string;
  contentType: string;
  size: number;
  scanStatus: "Pending" | "Clean" | "Infected" | "NotScanned";
  url: string;
}

export interface BrandingSettings {
  draft: BrandingDraft;
  published: BrandingDraft | null;
  publishedAt: string | null;
  updatedAt: string;
  unpublished: boolean;
  checks: ContrastCheck[];
  problems: string[];
  images: Record<string, BrandingImage | null>;
  fonts: { key: string; family: string }[];
}

export type DnsCheck = "Unknown" | "Passed" | "Missing" | "Wrong";

export interface DnsRecord {
  type: "TXT" | "CNAME";
  name: string;
  value: string;
  status: DnsCheck;
}

export interface CustomDomain {
  id: string;
  host: string;
  status: "Pending" | "Verified";
  verifiedAt: string | null;
  lastCheckedAt: string | null;
  lastError: string | null;
  certificateMode: "Automatic" | "Uploaded";
  certificateExpiresAt: string | null;
  records: DnsRecord[];
}

export interface SenderDomain {
  domain: string;
  localPart: string;
  address: string;
  verified: boolean;
  lastCheckedAt: string | null;
  records: DnsRecord[];
}

const colourPattern = /^#[0-9a-f]{6}$/;

export function isColour(value: string | null | undefined): value is string {
  return typeof value === "string" && colourPattern.test(value);
}

function luminance(colour: string): number {
  if (!isColour(colour)) return 0;
  const channel = (index: number) => {
    const value = parseInt(colour.slice(1 + index * 2, 3 + index * 2), 16) / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2);
}

/** WCAG contrast ratio of two #rrggbb colours, rounded to two decimals (same as the API). */
export function contrast(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  return Math.round(((Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05)) * 100) / 100;
}

/** The four automatic checks of the API, computed as the administrator types (live preview). */
export function contrastChecks(light: string, dark: string): ContrastCheck[] {
  const check = (key: ContrastCheck["key"], foreground: string, background: string): ContrastCheck => {
    const ratio = contrast(foreground, background);
    return { key, foreground, background, ratio, passed: ratio >= minimumContrast };
  };
  return [
    check("light.text", light, lightSurface),
    check("light.button", lightSurface, light),
    check("dark.text", dark, darkSurface),
    check("dark.button", darkSurface, dark),
  ];
}

/**
 * The CSS that applies a look: brand colours for light and dark mode and the font. Only values
 * that pass the format checks get in (colours as #rrggbb, fonts from the bundled list), so nothing
 * else can be written into the style sheet.
 */
export function brandingCss(look: { lightColour: string; darkColour: string; font: string } | null, selector = ":root"): string {
  if (!look) return "";
  const light = isColour(look.lightColour) ? look.lightColour : defaultLightColour;
  const dark = isColour(look.darkColour) ? look.darkColour : defaultDarkColour;
  const family = brandingFonts[look.font] ?? brandingFonts["ibm-plex-sans-arabic"];
  return (
    `${selector}{--color-brand:${light};--font-sans:"${family}", ui-sans-serif, system-ui, sans-serif;}` +
    `@media (prefers-color-scheme: dark){${selector}{--color-brand:${dark};}}`
  );
}

/** The web app's address of a branding image (it proxies the API's), for pages and the sign-in pages. */
export function brandingImagePath(tenant: string, slot: BrandingSlot, apiUrl: string | null | undefined): string | null {
  if (!apiUrl) return null;
  const version = /[?&]v=([0-9a-f]+)/.exec(apiUrl)?.[1] ?? "0";
  const draft = /[?&]draft=true/.test(apiUrl) ? "/draft" : "";
  return `/branding/${encodeURIComponent(tenant)}/${slot}${draft}?v=${version}`;
}
