import "server-only";
import { revalidateTag } from "next/cache";
import { headers as requestHeaders } from "next/headers";
import { cache } from "react";
import { clientAddress, trustedProxyHops } from "./client-address";
import type { ApiCalendar, ApiDigits } from "./format";

/**
 * Server-side client for the Zelvo API. The web app uses the same public API as integrations.
 */

export type HealthStatus = "Healthy" | "Degraded" | "Unhealthy";

export interface HealthReport {
  status: HealthStatus;
  checks: { name: string; status: HealthStatus }[];
}

export interface SystemInfo {
  product: string;
  version: string;
  languages: string[];
  licenseMode: string;
  /** production, test (the test installation every license includes), demo or development. */
  environment?: string;
}

const apiBaseUrl = process.env.API_INTERNAL_URL ?? "http://localhost:8080";
const timeoutMs = 3000;
const proxyHops = trustedProxyHops(process.env.TRUSTED_PROXY_HOPS);

/** `X-Forwarded-For` with the browser's address, for the API's rate limits and audit log; empty outside a request. */
async function forwardedFor(): Promise<Record<string, string>> {
  try {
    const address = clientAddress((await requestHeaders()).get("x-forwarded-for"), proxyHops);
    return address ? { "X-Forwarded-For": address } : {};
  } catch {
    return {};
  }
}

async function getJson<T>(path: string): Promise<T | null> {
  try {
    const response = await fetch(new URL(path, apiBaseUrl), {
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", ...(await forwardedFor()) },
    });
    // Readiness answers 503 with a body when a dependency is down; that body is still useful.
    if (!response.ok && response.status !== 503) {
      return null;
    }
    return (await response.json()) as T;
  } catch {
    return null;
  }
}

export function getHealth(): Promise<HealthReport | null> {
  return getJson<HealthReport>("/health/ready");
}

export function getSystemInfo(): Promise<SystemInfo | null> {
  return getJson<SystemInfo>("/api/v1/system/info");
}

/** Bilingual text as the API returns it. */
export interface LocalizedText {
  en: string | null;
  ar: string | null;
  missing: string[];
}

/** Language, calendar and digits for display. */
export interface DisplaySettings {
  language: "en" | "ar";
  calendar: ApiCalendar;
  digits: ApiDigits;
}

/** A user's own choices (null: the tenant default) and what applies (GET /api/v1/me/preferences). */
export interface Preferences {
  language: "en" | "ar" | null;
  calendar: ApiCalendar | null;
  digits: ApiDigits | null;
  effective: DisplaySettings;
}

/** The tenant's language and region defaults (GET /api/v1/settings/localization). */
export interface LocalizationSettings {
  defaultLanguage: "en" | "ar";
  defaultCalendar: ApiCalendar;
  defaultDigits: ApiDigits;
  updatedAt: string | null;
}

/** The signed-in user (GET /api/v1/me). */
/** Settings → Search: the tenant's search index (GET /api/v1/search/status). */
export interface SearchStatus {
  available: boolean;
  documents: number | null;
  pendingUpdates: number;
  failedUpdates: number;
  lastError: string | null;
  lastErrorAt: string | null;
  lastIndexedAt: string | null;
  schemaVersion: number | null;
  currentSchemaVersion: number;
  rebuildRequestedAt: string | null;
  rebuildCompletedAt: string | null;
  rebuildInProgress: boolean;
}

export interface CurrentUser {
  id: string;
  email: string | null;
  displayName: string | null;
  roles: { key: string; name: LocalizedText; requiresMfa: boolean }[];
  permissions: string[];
  pendingPermissions: string[];
  mfa: { satisfied: boolean; required: boolean };
  preferences: Preferences;
  /** Whether round-robin may give this user tickets (task B2). */
  availableForAssignment?: boolean;
  /** Assignment groups the user belongs to (task B2). */
  groups?: { id: string; name: LocalizedText; isLead: boolean }[];
}

/**
 * Calls the API as the signed-in user, for the session's tenant. An empty access token calls it
 * as a visitor (only endpoints open to visitors answer).
 */
export async function apiFetch(path: string, accessToken: string, tenant: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (accessToken) {
    headers.set("Authorization", `Bearer ${accessToken}`);
  }
  headers.set("X-Zelvo-Tenant", tenant);
  if (!headers.has("Accept")) {
    headers.set("Accept", "application/json");
  }
  for (const [name, value] of Object.entries(await forwardedFor())) {
    headers.set(name, value);
  }
  return fetch(new URL(path, apiBaseUrl), {
    ...init,
    headers,
    cache: "no-store",
    signal: init.signal ?? AbortSignal.timeout(10_000),
  });
}

/** The signed-in user; one API call per request however many components ask. */
export const getCurrentUser = cache(async (accessToken: string, tenant: string): Promise<CurrentUser | null> => {
  try {
    const response = await apiFetch("/api/v1/me", accessToken, tenant);
    return response.ok ? ((await response.json()) as CurrentUser) : null;
  } catch {
    return null;
  }
});

/** Calls the API as the signed-in user and returns the JSON body, or null on any failure. */
export async function getJsonAs<T>(path: string, accessToken: string, tenant: string): Promise<T | null> {
  try {
    const response = await apiFetch(path, accessToken, tenant);
    return response.ok ? ((await response.json()) as T) : null;
  } catch {
    return null;
  }
}

/** Cache tag of a tenant's language and region defaults in Next's data cache. */
export function localizationSettingsTag(tenant: string): string {
  return `zelvo-localization-${tenant}`;
}

/**
 * The tenant's language and region defaults, readable without signing in. Pages keep them in
 * Next's data cache for a minute, shared by all of the app's bundles, so visitors' pages do not
 * call the API each time; saving them in Settings expires the cache at once
 * (`forgetLocalizationSettings`). `useCache: false` always asks the API.
 */
export async function getLocalizationSettings(
  tenant: string,
  { useCache = true }: { useCache?: boolean } = {},
): Promise<LocalizationSettings | null> {
  try {
    const response = await fetch(new URL("/api/v1/settings/localization", apiBaseUrl), {
      ...(useCache ? { next: { revalidate: 60, tags: [localizationSettingsTag(tenant)] } } : { cache: "no-store" as const }),
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", "X-Zelvo-Tenant": tenant },
    });
    return response.ok ? ((await response.json()) as LocalizationSettings) : null;
  } catch {
    return null;
  }
}

/** Expires the cached defaults, after an admin changes them through this web app. */
export function forgetLocalizationSettings(tenant: string): void {
  revalidateTag(localizationSettingsTag(tenant), { expire: 0 });
}

/** Cache tag of a tenant's published branding (task F1). */
export function brandingTag(tenant: string): string {
  return `zelvo-branding-${tenant}`;
}

/** The tenant's published branding, cached for a minute like the language defaults; null when the API cannot be reached. */
export async function getBranding(tenant: string): Promise<import("./branding").PublicBranding | null> {
  try {
    const response = await fetch(new URL("/api/v1/branding", apiBaseUrl), {
      next: { revalidate: 60, tags: [brandingTag(tenant)] },
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", "X-Zelvo-Tenant": tenant },
    });
    return response.ok ? ((await response.json()) as import("./branding").PublicBranding) : null;
  } catch {
    return null;
  }
}

/** Expires the cached branding after an administrator publishes or discards through this web app. */
export function forgetBranding(tenant: string): void {
  revalidateTag(brandingTag(tenant), { expire: 0 });
}

/** Sign-in methods besides password and email code (GET /api/v1/identity/sign-in-methods). */
export interface SignInMethods {
  uaePass: { enabled: boolean; signOutUrl: string | null };
}

/** The tenant's sign-in methods, readable without signing in; null when the API cannot be reached. */
export async function getSignInMethods(tenant: string): Promise<SignInMethods | null> {
  try {
    const response = await fetch(new URL("/api/v1/identity/sign-in-methods", apiBaseUrl), {
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", "X-Zelvo-Tenant": tenant, ...(await forwardedFor()) },
    });
    return response.ok ? ((await response.json()) as SignInMethods) : null;
  } catch {
    return null;
  }
}

/** Why the API refused this sign-in (its problem code, for example "uae-pass-level-too-low"), or null. */
export async function getSignInRefusal(accessToken: string, tenant: string): Promise<string | null> {
  try {
    const response = await apiFetch("/api/v1/me", accessToken, tenant);
    if (response.status !== 403) {
      return null;
    }
    const problem = (await response.json().catch(() => null)) as { code?: unknown } | null;
    return typeof problem?.code === "string" ? problem.code : null;
  } catch {
    return null;
  }
}

/** A sample organisation of a demo installation. */
export interface DemoOrganisation {
  slug: string;
  kind: "Government" | "Company";
  name: LocalizedText;
}

/** A ready-made demo user: one per persona. */
export interface DemoUser {
  persona: string;
  roleKey: string;
  roleName: LocalizedText;
  displayName: string;
  username: string;
}

/** Demo mode for the request's sample organisation (GET /api/v1/demo). */
export interface DemoInfo {
  organisation: string;
  organisations: DemoOrganisation[];
  users: DemoUser[];
  lastResetAt: string | null;
}

/** Demo mode of `tenant`; null when this is not a demo installation (or the API cannot be reached). */
export async function getDemo(tenant: string): Promise<DemoInfo | null> {
  try {
    const response = await fetch(new URL("/api/v1/demo", apiBaseUrl), {
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", "X-Zelvo-Tenant": tenant },
    });
    return response.ok ? ((await response.json()) as DemoInfo) : null;
  } catch {
    return null;
  }
}

/**
 * Websites that may frame a chat widget (task E2), for the widget page's frame-ancestors policy;
 * cached for a minute. None when the widget is unknown or the API cannot be reached.
 */
export async function getWidgetOrigins(tenant: string, widgetId: string): Promise<string[]> {
  try {
    const response = await fetch(new URL(`/api/v1/chat/widgets/${encodeURIComponent(widgetId)}/public`, apiBaseUrl), {
      next: { revalidate: 60 },
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", "X-Zelvo-Tenant": tenant },
    });
    if (!response.ok) return [];
    const widget = (await response.json()) as { allowedOrigins?: unknown };
    return Array.isArray(widget.allowedOrigins)
      ? widget.allowedOrigins.filter((o): o is string => typeof o === "string" && /^https?:\/\/[a-z0-9.:-]+$/i.test(o))
      : [];
  } catch {
    return [];
  }
}
