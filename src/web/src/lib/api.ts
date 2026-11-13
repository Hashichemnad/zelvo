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
