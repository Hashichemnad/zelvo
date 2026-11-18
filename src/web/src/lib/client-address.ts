/**
 * The browser's address, passed to the API as `X-Forwarded-For` so its rate limits and audit log
 * see the person rather than this server. Next.js keeps an incoming `X-Forwarded-For` as is and sets
 * it to the connection's address when there is none. With `trustedProxyHops` reverse proxies in
 * front of the web app (TRUSTED_PROXY_HOPS, for example 1 behind the TLS proxy), the address is the
 * one the outermost trusted proxy saw; entries further left were sent by the client and are ignored.
 */
export function clientAddress(forwardedFor: string | null, trustedProxyHops: number): string | null {
  const entries = (forwardedFor ?? "")
    .split(",")
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0 && entry.length <= 45 && /^[0-9a-fA-F:.]+$/.test(entry));
  if (entries.length === 0) {
    return null;
  }
  const fromRight = Math.max(1, trustedProxyHops);
  return entries[Math.max(0, entries.length - fromRight)] ?? null;
}

/** TRUSTED_PROXY_HOPS as a number of proxies (0 when unset or invalid). */
export function trustedProxyHops(value: string | undefined): number {
  const hops = Number.parseInt(value ?? "", 10);
  return Number.isInteger(hops) && hops >= 0 && hops <= 10 ? hops : 0;
}
