import "server-only";
import { cookies, headers } from "next/headers";
import type { NextRequest } from "next/server";
import { customHost, tenantOfHost, type HostTenant } from "../host-tenant";
import { pickTenant, tenantCookieName } from "../tenant";

/**
 * Sign-in settings from the environment. Each tenant has its own Keycloak realm
 * (`zelvo-<tenant>`); this web app serves ZELVO_TENANT on its own address and, from task F1, each
 * tenant's verified custom domains on those hosts (the host decides; cookies cannot change it).
 * A demo installation also lists its sample organisations in ZELVO_DEMO_TENANTS; visitors switch
 * between them with the demo panel, which remembers the choice in the `zelvo-tenant` cookie.
 */
export interface AuthConfig {
  appUrl: URL;
  tenant: string;
  /** Demo installations: the sample organisations' tenants (empty otherwise). */
  demoTenants: string[];
  issuer: URL;
  keycloakPublicUrl: string;
  keycloakInternalUrl: string;
  clientId: string;
  clientSecret: string;
  sessionStoreUrl: string | undefined;
  secureCookies: boolean;
  allowHttpIdentityProvider: boolean;
  idleTimeoutSeconds: number;
  absoluteTimeoutSeconds: number;
}

function isLocalhost(url: URL): boolean {
  return url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
}

/**
 * The settings for `requestedTenant` (from the tenant cookie): one of the demo tenants in a demo
 * installation, otherwise always ZELVO_TENANT.
 */
export function getAuthConfig(env: NodeJS.ProcessEnv = process.env, requestedTenant?: string | null, hostTenant?: HostTenant | null): AuthConfig {
  const appUrl = hostTenant ? new URL(`https://${hostTenant.host}`) : new URL(env.APP_URL ?? "http://localhost:3000");
  const defaultTenant = env.ZELVO_TENANT ?? "default";
  const demoTenants = (env.ZELVO_DEMO_TENANTS ?? "")
    .split(",")
    .map((slug) => slug.trim())
    .filter((slug) => slug.length > 0);
  const tenant = hostTenant ? hostTenant.tenant : pickTenant(requestedTenant, defaultTenant, demoTenants);
  const keycloakPublicUrl = (env.KEYCLOAK_PUBLIC_URL ?? "http://localhost:8180").replace(/\/+$/, "");
  const keycloakInternalUrl = (env.KEYCLOAK_INTERNAL_URL ?? keycloakPublicUrl).replace(/\/+$/, "");
  const issuer = new URL(`${keycloakPublicUrl}/realms/zelvo-${tenant}`);

  return {
    appUrl,
    tenant,
    demoTenants,
    issuer,
    keycloakPublicUrl,
    keycloakInternalUrl,
    clientId: "zelvo-web",
    clientSecret: env.ZELVO_WEB_CLIENT_SECRET ?? "",
    sessionStoreUrl: env.SESSION_STORE_URL || undefined,
    // Browsers treat localhost as secure, so Secure cookies work there over http too.
    secureCookies: appUrl.protocol === "https:" || isLocalhost(appUrl),
    // Plain http to Keycloak is only acceptable on this computer or inside Docker's network.
    allowHttpIdentityProvider: issuer.protocol === "https:" || isLocalhost(issuer) || env.ZELVO_ALLOW_HTTP_IDENTITY === "true",
    idleTimeoutSeconds: Number(env.SESSION_IDLE_TIMEOUT_SECONDS ?? 1800),
    absoluteTimeoutSeconds: Number(env.SESSION_ABSOLUTE_TIMEOUT_SECONDS ?? 43200),
  };
}

/** The settings for this request's tenant, in server components and server actions. */
export async function getRequestAuthConfig(): Promise<AuthConfig> {
  return getAuthConfig(process.env, (await cookies()).get(tenantCookieName)?.value, await hostTenantOf((await headers()).get("host")));
}

/** The settings for this request's tenant, in route handlers and the proxy. */
export async function authConfigFor(request: NextRequest): Promise<AuthConfig> {
  return getAuthConfig(process.env, request.cookies.get(tenantCookieName)?.value, await hostTenantOf(request.headers.get("host")));
}

async function hostTenantOf(hostHeader: string | null): Promise<HostTenant | null> {
  const host = customHost(hostHeader, new URL(process.env.APP_URL ?? "http://localhost:3000"));
  return host ? tenantOfHost(host) : null;
}
