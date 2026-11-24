/**
 * Custom domains (module 11, task F1): which tenant a request's host serves. The installation's
 * own address (APP_URL) and local addresses serve ZELVO_TENANT as before; any other host is looked
 * up in the API, which knows only hosts a tenant verified by DNS. Answers are kept for a minute,
 * unknown hosts too, so a flood of made-up hosts cannot overload the API.
 */

export interface HostTenant {
  host: string;
  tenant: string;
}

const cache = new Map<string, { value: HostTenant | null; expires: number }>();
const ttlMs = 60_000;
const maxEntries = 1000;
const hostPattern = /^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$/;

/** The host without its port, in lower case, when it is a DNS name another tenant could own; else null. */
export function customHost(hostHeader: string | null | undefined, appUrl: URL): string | null {
  if (!hostHeader) return null;
  const host = hostHeader.trim().toLowerCase().replace(/:\d+$/, "").replace(/\.$/, "");
  if (host === appUrl.hostname.toLowerCase() || host === "localhost" || host === "127.0.0.1" || host.startsWith("[")) return null;
  return hostPattern.test(host) ? host : null;
}

/** The tenant of a verified custom domain, or null. `lookup` asks the API (tests replace it). */
export async function tenantOfHost(host: string, lookup: (host: string) => Promise<string | null> = askApi, now = Date.now()): Promise<HostTenant | null> {
  const known = cache.get(host);
  if (known && known.expires > now) return known.value;
  let value: HostTenant | null = null;
  try {
    const tenant = await lookup(host);
    value = tenant ? { host, tenant } : null;
  } catch {
    // The API is unreachable: serve nothing for this host now, try again after the minute.
  }
  if (cache.size >= maxEntries) cache.clear();
  cache.set(host, { value, expires: now + ttlMs });
  return value;
}

/** Forgets cached answers (tests). */
export function forgetHosts(): void {
  cache.clear();
}

async function askApi(host: string): Promise<string | null> {
  const base = process.env.API_INTERNAL_URL ?? "http://localhost:8080";
  const response = await fetch(`${base}/api/v1/platform/hosts/${encodeURIComponent(host)}`, {
    headers: { Accept: "application/json", "X-Zelvo-Tenant": process.env.ZELVO_TENANT ?? "default" },
    cache: "no-store",
    signal: AbortSignal.timeout(3000),
  });
  if (!response.ok) return null;
  const body = (await response.json()) as { tenant?: string };
  return typeof body.tenant === "string" && /^[a-z0-9-]{1,63}$/.test(body.tenant) ? body.tenant : null;
}
