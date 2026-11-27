import type { SessionData } from "./session";
import type { SessionStore } from "./store";

/** New tokens from Keycloak's refresh token grant. */
export interface RefreshedTokens {
  accessToken: string;
  expiresInSeconds: number | undefined;
  refreshToken: string | undefined;
  idToken: string | undefined;
}

/** Refreshes in flight in this server process, by session key: one refresh per session at a time. */
const inFlight = new Map<string, Promise<SessionData | null>>();

/** How often and how long to look for a refresh another web server instance made. */
const rereadAttempts = 4;
const rereadDelayMs = 250;

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Refreshes a session's access token once, however many requests of the same browser ask at the
 * same moment. Keycloak rotates refresh tokens and refuses a used one (`revokeRefreshToken`,
 * `refreshTokenMaxReuse` 0, task F7), so two requests refreshing with the same token would see
 * the second refused, and the session was then ended: people were signed out about every five
 * minutes when a page's requests overlapped the token's expiry (CI's demo WCAG audit of
 * 2026-11-27). Requests in this process share one refresh; a refusal is first checked against the
 * store, where another instance may already have saved the new tokens. Only when nobody did is
 * the session over (Keycloak ended it: time-out, sign-out elsewhere, disabled person).
 */
export function refreshSession(
  store: SessionStore,
  key: string,
  data: SessionData,
  grant: (refreshToken: string) => Promise<RefreshedTokens>,
  ttlSeconds: (data: SessionData) => number,
): Promise<SessionData | null> {
  const running = inFlight.get(key);
  if (running) {
    return running;
  }
  const work = (async () => {
    const usedToken = data.refreshToken;
    if (!usedToken) {
      await store.delete(key);
      return null;
    }
    try {
      const tokens = await grant(usedToken);
      const now = Date.now();
      const refreshed: SessionData = {
        ...data,
        accessToken: tokens.accessToken,
        accessTokenExpiresAt: now + (tokens.expiresInSeconds ?? 60) * 1000,
        refreshToken: tokens.refreshToken ?? usedToken,
        idToken: tokens.idToken ?? data.idToken,
        lastSeenAt: now,
      };
      await store.set(key, JSON.stringify(refreshed), ttlSeconds(refreshed));
      return refreshed;
    } catch {
      for (let attempt = 0; attempt < rereadAttempts; attempt++) {
        const raw = await store.get(key);
        if (!raw) {
          return null;
        }
        const stored = JSON.parse(raw) as SessionData;
        if (stored.refreshToken !== usedToken && stored.accessTokenExpiresAt > Date.now()) {
          return stored;
        }
        await sleep(rereadDelayMs);
      }
      await store.delete(key);
      return null;
    }
  })().finally(() => inFlight.delete(key));
  inFlight.set(key, work);
  return work;
}
