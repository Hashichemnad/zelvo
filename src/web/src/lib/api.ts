import "server-only";

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

/** The signed-in user (GET /api/v1/me). */
export interface CurrentUser {
  id: string;
  email: string | null;
  displayName: string | null;
  roles: { key: string; name: LocalizedText; requiresMfa: boolean }[];
  permissions: string[];
  pendingPermissions: string[];
  mfa: { satisfied: boolean; required: boolean };
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

export async function getCurrentUser(accessToken: string, tenant: string): Promise<CurrentUser | null> {
  try {
    const response = await apiFetch("/api/v1/me", accessToken, tenant);
    return response.ok ? ((await response.json()) as CurrentUser) : null;
  } catch {
    return null;
  }
}
