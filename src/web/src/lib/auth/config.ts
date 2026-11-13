import "server-only";

/**
 * Sign-in settings from the environment. Each tenant has its own Keycloak realm
 * (`zelvo-<tenant>`); this web app serves one tenant (ZELVO_TENANT) until custom domains arrive.
 */
export interface AuthConfig {
  appUrl: URL;
  tenant: string;
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

export function getAuthConfig(env: NodeJS.ProcessEnv = process.env): AuthConfig {
  const appUrl = new URL(env.APP_URL ?? "http://localhost:3000");
  const tenant = env.ZELVO_TENANT ?? "default";
  const keycloakPublicUrl = (env.KEYCLOAK_PUBLIC_URL ?? "http://localhost:8180").replace(/\/+$/, "");
  const keycloakInternalUrl = (env.KEYCLOAK_INTERNAL_URL ?? keycloakPublicUrl).replace(/\/+$/, "");
  const issuer = new URL(`${keycloakPublicUrl}/realms/zelvo-${tenant}`);

  return {
    appUrl,
    tenant,
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
