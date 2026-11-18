/**
 * Which tenant a request is for. Kept free of server-only imports so it can be tested.
 */

/** Cookie that remembers the sample organisation chosen in the demo panel (demo installations only). */
export const tenantCookieName = "zelvo-tenant";

/** A year: the choice is remembered on the device like the language. */
export const tenantCookieMaxAgeSeconds = 60 * 60 * 24 * 365;

/**
 * The requested tenant when it is one of the demo installation's sample organisations; otherwise
 * the installation's tenant. A cookie can never select a tenant the installation does not list.
 */
export function pickTenant(requested: string | null | undefined, defaultTenant: string, demoTenants: readonly string[]): string {
  if (demoTenants.length === 0) {
    return defaultTenant;
  }
  if (requested && demoTenants.includes(requested)) {
    return requested;
  }
  return demoTenants.includes(defaultTenant) ? defaultTenant : (demoTenants[0] ?? defaultTenant);
}
