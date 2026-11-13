import "server-only";
import { cookies } from "next/headers";
import { getAuthConfig, type AuthConfig } from "./config";
import { getOidcConfiguration, oidc } from "./oidc";
import { randomSecret, sessionKey } from "./security";
import { getSessionStore } from "./store";

/** What the server keeps about a signed-in browser. The browser only holds a random id. */
export interface SessionData {
  subject: string;
  tenant: string;
  name: string | null;
  email: string | null;
  acr: string | null;
  accessToken: string;
  accessTokenExpiresAt: number;
  refreshToken: string | null;
  idToken: string | null;
  csrfToken: string;
  createdAt: number;
  lastSeenAt: number;
}

export interface Session {
  id: string;
  data: SessionData;
}

/** Cookie names: `__Host-` cookies are Secure, host-only and path=/ (ASVS V3.4). */
export function sessionCookieName(config: AuthConfig): string {
  return config.secureCookies ? "__Host-zelvo-session" : "zelvo-session";
}

export function signInCookieName(config: AuthConfig): string {
  return config.secureCookies ? "__Host-zelvo-signin" : "zelvo-signin";
}

export function cookieOptions(config: AuthConfig, maxAgeSeconds: number) {
  return {
    httpOnly: true,
    secure: config.secureCookies,
    sameSite: "lax" as const,
    path: "/",
    maxAge: maxAgeSeconds,
  };
}

function store(config: AuthConfig) {
  return getSessionStore(config.sessionStoreUrl);
}

/** Saves a new session and returns its id (to put in the cookie). */
export async function createSession(
  tokens: oidc.TokenEndpointResponse & oidc.TokenEndpointResponseHelpers,
  config: AuthConfig = getAuthConfig(),
): Promise<Session> {
  const claims = tokens.claims();
  const now = Date.now();
  const data: SessionData = {
    subject: String(claims?.sub ?? ""),
    tenant: config.tenant,
    name: typeof claims?.name === "string" ? claims.name : null,
    email: typeof claims?.email === "string" ? claims.email : null,
    acr: typeof claims?.acr === "string" ? claims.acr : null,
    accessToken: tokens.access_token,
    accessTokenExpiresAt: now + (tokens.expiresIn() ?? 60) * 1000,
    refreshToken: tokens.refresh_token ?? null,
    idToken: tokens.id_token ?? null,
    csrfToken: randomSecret(),
    createdAt: now,
    lastSeenAt: now,
  };
  const id = randomSecret();
  await store(config).set(sessionKey(id), JSON.stringify(data), config.idleTimeoutSeconds);
  return { id, data };
}

/**
 * The current session, or null. Ends sessions past the idle or absolute time-out, and refreshes
 * the access token when it is about to expire (the refresh token never leaves the server).
 */
export async function getSession(config: AuthConfig = getAuthConfig()): Promise<Session | null> {
  const id = (await cookies()).get(sessionCookieName(config))?.value;
  if (!id) {
    return null;
  }
  const key = sessionKey(id);
  const raw = await store(config).get(key);
  if (!raw) {
    return null;
  }
  let data = JSON.parse(raw) as SessionData;
  const now = Date.now();
  if (data.tenant !== config.tenant || now - data.createdAt > config.absoluteTimeoutSeconds * 1000) {
    await store(config).delete(key);
    return null;
  }

  if (data.accessTokenExpiresAt - now < 30_000) {
    if (!data.refreshToken) {
      await store(config).delete(key);
      return null;
    }
    try {
      const refreshed = await oidc.refreshTokenGrant(await getOidcConfiguration(config), data.refreshToken);
      data = {
        ...data,
        accessToken: refreshed.access_token,
        accessTokenExpiresAt: now + (refreshed.expiresIn() ?? 60) * 1000,
        refreshToken: refreshed.refresh_token ?? data.refreshToken,
        idToken: refreshed.id_token ?? data.idToken,
      };
    } catch {
      // Keycloak ended the session (time-out, sign-out elsewhere, disabled user).
      await store(config).delete(key);
      return null;
    }
  }

  // Sliding idle time-out, written at most once a minute.
  if (now - data.lastSeenAt > 60_000 || data.accessTokenExpiresAt !== JSON.parse(raw).accessTokenExpiresAt) {
    data = { ...data, lastSeenAt: now };
    const remaining = Math.min(config.idleTimeoutSeconds, config.absoluteTimeoutSeconds - (now - data.createdAt) / 1000);
    await store(config).set(key, JSON.stringify(data), remaining);
  }
  return { id, data };
}

export async function deleteSession(id: string, config: AuthConfig = getAuthConfig()): Promise<void> {
  await store(config).delete(sessionKey(id));
}
