import "server-only";
import { cache } from "react";
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
}

const apiBaseUrl = process.env.API_INTERNAL_URL ?? "http://localhost:8080";
const timeoutMs = 3000;

async function getJson<T>(path: string): Promise<T | null> {
  try {
    const response = await fetch(new URL(path, apiBaseUrl), {
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json" },
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
export interface CurrentUser {
  id: string;
  email: string | null;
  displayName: string | null;
  roles: { key: string; name: LocalizedText; requiresMfa: boolean }[];
  permissions: string[];
  pendingPermissions: string[];
  mfa: { satisfied: boolean; required: boolean };
  preferences: Preferences;
}

/** Calls the API as the signed-in user, for the session's tenant. */
export async function apiFetch(path: string, accessToken: string, tenant: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${accessToken}`);
  headers.set("X-Zelvo-Tenant", tenant);
  if (!headers.has("Accept")) {
    headers.set("Accept", "application/json");
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

const tenantDefaultsTtlMs = 60_000;
const tenantDefaultsCache = new Map<string, { value: LocalizationSettings; expiresAt: number }>();

/**
 * The tenant's language and region defaults, readable without signing in. Kept for a minute, so
 * pages for visitors do not call the API each time; a change in Settings shows within a minute.
 */
export async function getLocalizationSettings(tenant: string): Promise<LocalizationSettings | null> {
  const cached = tenantDefaultsCache.get(tenant);
  if (cached && cached.expiresAt > Date.now()) {
    return cached.value;
  }
  try {
    const response = await fetch(new URL("/api/v1/settings/localization", apiBaseUrl), {
      cache: "no-store",
      signal: AbortSignal.timeout(timeoutMs),
      headers: { Accept: "application/json", "X-Zelvo-Tenant": tenant },
    });
    if (!response.ok) {
      return cached?.value ?? null;
    }
    const value = (await response.json()) as LocalizationSettings;
    tenantDefaultsCache.set(tenant, { value, expiresAt: Date.now() + tenantDefaultsTtlMs });
    return value;
  } catch {
    return cached?.value ?? null;
  }
}

/** Forgets the cached defaults, after an admin changes them here. */
export function forgetLocalizationSettings(tenant: string): void {
  tenantDefaultsCache.delete(tenant);
}
