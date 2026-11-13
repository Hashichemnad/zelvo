import "server-only";
import { createClient } from "redis";

/** A small key-value store with expiry for sessions and pending sign-ins. */
export interface SessionStore {
  get(key: string): Promise<string | null>;
  set(key: string, value: string, ttlSeconds: number): Promise<void>;
  /** Reads and deletes in one step (a pending sign-in is used once). */
  take(key: string): Promise<string | null>;
  delete(key: string): Promise<void>;
}

/** Valkey (Redis protocol): shared by every web server instance. */
class RedisSessionStore implements SessionStore {
  private client: ReturnType<typeof createClient> | undefined;
  private connecting: Promise<unknown> | undefined;

  constructor(private readonly url: string) {}

  private async connected() {
    if (!this.client) {
      this.client = createClient({ url: this.url });
      this.client.on("error", () => {
        // Reconnects on its own; failed commands surface to the caller.
      });
      this.connecting = this.client.connect();
    }
    await this.connecting;
    return this.client;
  }

  async get(key: string) {
    return (await this.connected()).get(key);
  }

  async set(key: string, value: string, ttlSeconds: number) {
    await (await this.connected()).set(key, value, { EX: Math.max(1, Math.floor(ttlSeconds)) });
  }

  async take(key: string) {
    return (await this.connected()).getDel(key);
  }

  async delete(key: string) {
    await (await this.connected()).del(key);
  }
}

/** In-process store for `npm run dev` without Valkey and for tests. Not for production. */
export class MemorySessionStore implements SessionStore {
  private readonly items = new Map<string, { value: string; expires: number }>();

  async get(key: string) {
    const item = this.items.get(key);
    if (!item || item.expires < Date.now()) {
      this.items.delete(key);
      return null;
    }
    return item.value;
  }

  async set(key: string, value: string, ttlSeconds: number) {
    this.items.set(key, { value, expires: Date.now() + ttlSeconds * 1000 });
  }

  async take(key: string) {
    const value = await this.get(key);
    this.items.delete(key);
    return value;
  }

  async delete(key: string) {
    this.items.delete(key);
  }
}

const globalStore = globalThis as unknown as { zelvoSessionStore?: SessionStore };

export function getSessionStore(url: string | undefined): SessionStore {
  if (!globalStore.zelvoSessionStore) {
    if (!url && process.env.NODE_ENV === "production") {
      throw new Error("SESSION_STORE_URL is required in production.");
    }
    globalStore.zelvoSessionStore = url ? new RedisSessionStore(url) : new MemorySessionStore();
  }
  return globalStore.zelvoSessionStore;
}
