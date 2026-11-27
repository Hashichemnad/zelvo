/**
 * What the installable web app keeps on the device (module 19, task J1, decision 13):
 *
 * - the requester's own **last-viewed tickets**, read-only, at most {@link maxTickets}, written by
 *   the requester's ticket pages (never by staff pages, so agents' queues are never kept);
 * - **unsent work**: new incidents and replies that could not be sent for want of a connection,
 *   sent when it is back.
 *
 * Everything belongs to one person: a different person signing in, signing out or the tenant
 * turning offline data off clears it all. Kept in IndexedDB, which only this site can read.
 */

export const maxTickets = 20;
const databaseName = "zelvo-offline";
const databaseVersion = 1;

/** A ticket as the requester last saw it. */
export interface OfflineTicket {
  id: string;
  number: string;
  title: string;
  /** The state's label in the language the requester saw it in. */
  state: string;
  /** The team that has it (never an agent's name). */
  team: string | null;
  description: string | null;
  /** Where the ticket opens when the connection is back. */
  href: string;
  /** Public replies, oldest first: author's display name, text and when, as the person saw it (their calendar and digits). */
  messages: { author: string; text: string; at: string }[];
  /** When it last changed, as the person saw it. */
  updated: string;
  /** When the person last viewed it (ISO 8601), for the order. */
  viewedAt: string;
}

/** Work the device sends when the connection is back. */
export interface OutboxItem {
  id?: number;
  kind: "incident" | "reply";
  /** The API path under /bff/api, for example /v1/incidents. */
  path: string;
  body: Record<string, unknown>;
  /** A short description for the person: the title or the start of the reply. */
  summary: string;
  /** For replies: the ticket's number. */
  ticketNumber?: string;
  createdAt: string;
  /** The last reason it could not be sent (an API refusal; sending stops until the person decides). */
  failed?: string | null;
}

interface Meta {
  key: "owner";
  value: string;
}

let opening: Promise<IDBDatabase> | null = null;

/** Whether this browser can keep offline data at all. */
export function offlineStoreAvailable(): boolean {
  return typeof indexedDB !== "undefined";
}

function open(): Promise<IDBDatabase> {
  if (opening) return opening;
  opening = new Promise((resolve, reject) => {
    const request = indexedDB.open(databaseName, databaseVersion);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains("tickets")) db.createObjectStore("tickets", { keyPath: "id" });
      if (!db.objectStoreNames.contains("outbox")) db.createObjectStore("outbox", { keyPath: "id", autoIncrement: true });
      if (!db.objectStoreNames.contains("meta")) db.createObjectStore("meta", { keyPath: "key" });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => {
      opening = null;
      reject(request.error);
    };
  });
  return opening;
}

function done<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function store(name: "tickets" | "outbox" | "meta", mode: IDBTransactionMode): Promise<IDBObjectStore> {
  return (await open()).transaction(name, mode).objectStore(name);
}

/**
 * Makes the store belong to `owner` (the signed-in person's id), clearing what another person
 * left. `null` (nobody signed in) or `allowed = false` (the tenant turned offline data off)
 * clears everything.
 */
export async function claimStore(owner: string | null, allowed: boolean): Promise<void> {
  if (!offlineStoreAvailable()) return;
  const meta = (await done((await store("meta", "readonly")).get("owner"))) as Meta | undefined;
  if (!owner || !allowed) {
    await clearOfflineData();
    return;
  }
  if (meta?.value !== owner) {
    await clearOfflineData();
    await done((await store("meta", "readwrite")).put({ key: "owner", value: owner } satisfies Meta));
  }
}

/** Removes every kept ticket and unsent item (signing out, another person, offline data turned off). */
export async function clearOfflineData(): Promise<void> {
  if (!offlineStoreAvailable()) return;
  for (const name of ["tickets", "outbox", "meta"] as const) {
    await done((await store(name, "readwrite")).clear());
  }
}

/** Keeps a ticket the requester viewed; only the newest {@link maxTickets} stay. */
export async function rememberTicket(ticket: Omit<OfflineTicket, "viewedAt">, now = new Date()): Promise<void> {
  if (!offlineStoreAvailable()) return;
  await done((await store("tickets", "readwrite")).put({ ...ticket, viewedAt: now.toISOString() } satisfies OfflineTicket));
  const all = await listTickets();
  for (const old of all.slice(maxTickets)) {
    await done((await store("tickets", "readwrite")).delete(old.id));
  }
}

/** Kept tickets, last viewed first. */
export async function listTickets(): Promise<OfflineTicket[]> {
  if (!offlineStoreAvailable()) return [];
  const all = (await done((await store("tickets", "readonly")).getAll())) as OfflineTicket[];
  return all.sort((a, b) => b.viewedAt.localeCompare(a.viewedAt));
}

/** Keeps unsent work. */
export async function queueWork(item: Omit<OutboxItem, "id" | "createdAt">, now = new Date()): Promise<void> {
  await done((await store("outbox", "readwrite")).add({ ...item, createdAt: now.toISOString() }));
}

/** Unsent work, oldest first. */
export async function listOutbox(): Promise<OutboxItem[]> {
  if (!offlineStoreAvailable()) return [];
  const all = (await done((await store("outbox", "readonly")).getAll())) as OutboxItem[];
  return all.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
}

/** Removes an item (sent, or thrown away by the person). */
export async function removeWork(id: number): Promise<void> {
  await done((await store("outbox", "readwrite")).delete(id));
}

/** Records why an item could not be sent. */
export async function markFailed(item: OutboxItem, reason: string): Promise<void> {
  await done((await store("outbox", "readwrite")).put({ ...item, failed: reason }));
}

/** Whether a failed call failed for want of a connection (not a refusal by the API). */
export function isConnectionFailure(error: unknown): boolean {
  if (typeof navigator !== "undefined" && navigator.onLine === false) return true;
  return error instanceof TypeError;
}

/**
 * Sends unsent work in order. Items the API refuses are kept with the reason (the person sees
 * them on the offline page); a connection failure stops the run until the next one. Returns how
 * many were sent.
 */
export async function sendQueuedWork(send: (item: OutboxItem) => Promise<void>): Promise<{ sent: number; failed: number }> {
  let sent = 0;
  let failed = 0;
  for (const item of await listOutbox()) {
    if (item.failed) {
      failed++;
      continue;
    }
    try {
      await send(item);
      await removeWork(item.id!);
      sent++;
    } catch (error) {
      if (isConnectionFailure(error)) break;
      await markFailed(item, error instanceof Error ? error.message : "refused");
      failed++;
    }
  }
  return { sent, failed };
}

/** Forgets the open connection (tests). */
export function resetOfflineStoreForTests(): void {
  opening = null;
}
