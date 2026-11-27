import { refreshSession, type RefreshedTokens } from "./refresh";
import type { SessionData } from "./session";
import type { SessionStore } from "./store";

class FakeStore implements SessionStore {
  readonly items = new Map<string, string>();
  async get(key: string) { return this.items.get(key) ?? null; }
  async set(key: string, value: string) { this.items.set(key, value); }
  async take(key: string) { const v = this.items.get(key) ?? null; this.items.delete(key); return v; }
  async delete(key: string) { this.items.delete(key); }
}

function session(refreshToken: string): SessionData {
  const now = Date.now();
  return {
    subject: "s", tenant: "t", name: null, email: null, acr: "mfa", accessToken: crypto.randomUUID(), accessTokenExpiresAt: now + 10_000,
    refreshToken, idToken: null, csrfToken: crypto.randomUUID(), createdAt: now, lastSeenAt: now,
  };
}

const tokens = (refreshToken: string): RefreshedTokens => ({ accessToken: crypto.randomUUID(), expiresInSeconds: 300, refreshToken, idToken: undefined });

describe("session refresh", () => {
  it("refreshes once for requests that ask at the same moment", async () => {
    const store = new FakeStore();
    const key = `k-${crypto.randomUUID()}`;
    const first = crypto.randomUUID();
    const next = crypto.randomUUID();
    const data = session(first);
    store.items.set(key, JSON.stringify(data));
    const grant = vi.fn(async (used: string) => {
      if (used !== first) throw new Error("refused");
      return tokens(next);
    });
    const results = await Promise.all([1, 2, 3].map(() => refreshSession(store, key, data, grant, () => 60)));
    expect(grant).toHaveBeenCalledTimes(1);
    expect(results.every((r) => r?.refreshToken === next)).toBe(true);
    expect(JSON.parse(store.items.get(key)!).refreshToken).toBe(next);
  });

  it("keeps the session another server instance already refreshed", async () => {
    const store = new FakeStore();
    const key = `k-${crypto.randomUUID()}`;
    const used = crypto.randomUUID();
    const elsewhere = { ...session(crypto.randomUUID()), accessTokenExpiresAt: Date.now() + 300_000 };
    store.items.set(key, JSON.stringify(elsewhere));
    const result = await refreshSession(store, key, session(used), async () => { throw new Error("refused"); }, () => 60);
    expect(result?.refreshToken).toBe(elsewhere.refreshToken);
    expect(store.items.has(key)).toBe(true);
  });

  it("ends the session when Keycloak refuses and nobody refreshed it", async () => {
    const store = new FakeStore();
    const key = `k-${crypto.randomUUID()}`;
    const data = session(crypto.randomUUID());
    store.items.set(key, JSON.stringify(data));
    const result = await refreshSession(store, key, data, async () => { throw new Error("refused"); }, () => 60);
    expect(result).toBeNull();
    expect(store.items.has(key)).toBe(false);
  });
});
