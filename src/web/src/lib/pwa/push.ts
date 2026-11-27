"use client";

import { postJson } from "@/lib/client-api";

/** Web push for the signed-in person (module 19, task J2), as the API answers it. */
export interface PushStatus {
  /** Whether this installation offers web push (it needs outgoing internet). */
  available: boolean;
  /** The tenant's public key the browser subscribes with (base64url). */
  publicKey: string | null;
  devices: PushDevice[];
}

export interface PushDevice {
  id: string;
  deviceName: string;
  createdAt: string;
  lastSuccessAt: string | null;
}

/** Whether this browser can receive web push at all (service worker, Push API, notifications). */
export function pushSupported(): boolean {
  return typeof window !== "undefined" && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
}

/** Whether this is an iPhone or iPad browser tab, where web push works only in the app added to the Home Screen. */
export function needsHomeScreen(): boolean {
  if (typeof window === "undefined") return false;
  const ios = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  const standalone = window.matchMedia?.("(display-mode: standalone)").matches === true || (navigator as { standalone?: boolean }).standalone === true;
  return ios && !standalone;
}

function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const base64 = base64url.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((base64url.length + 3) % 4);
  const raw = atob(base64);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

async function registration(): Promise<ServiceWorkerRegistration> {
  return (await navigator.serviceWorker.getRegistration("/")) ?? (await navigator.serviceWorker.register("/sw.js", { scope: "/" }));
}

/** This browser's current push subscription, if any. */
export async function currentSubscription(): Promise<PushSubscription | null> {
  if (!pushSupported()) return null;
  const existing = await navigator.serviceWorker.getRegistration("/");
  return (await existing?.pushManager.getSubscription()) ?? null;
}

/**
 * Asks for permission, subscribes this browser with the tenant's key and adds it as one of the
 * person's devices. Answers "denied" when the person (or the browser) refused notifications.
 */
export async function subscribeThisDevice(publicKey: string, csrfToken: string): Promise<"subscribed" | "denied"> {
  const permission = await Notification.requestPermission();
  if (permission !== "granted") return "denied";
  const reg = await registration();
  await navigator.serviceWorker.ready;
  let subscription = await reg.pushManager.getSubscription();
  // A subscription made with another key (another organisation on this address before) is replaced.
  const wanted = keyBytes(publicKey);
  const current = subscription?.options.applicationServerKey;
  if (subscription && current && !sameBytes(new Uint8Array(current), wanted)) {
    await subscription.unsubscribe();
    subscription = null;
  }
  subscription ??= await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: wanted });
  const json = subscription.toJSON();
  await postJson("/v1/me/push/subscriptions", { endpoint: json.endpoint, p256dh: json.keys?.p256dh, auth: json.keys?.auth }, csrfToken);
  remember(json.endpoint ?? null);
  return "subscribed";
}

/** Where this device's push address is remembered (the browser holds the subscription anyway). */
const endpointKey = "zelvo-push-endpoint";

function remember(endpoint: string | null): void {
  try {
    if (endpoint) localStorage.setItem(endpointKey, endpoint);
    else localStorage.removeItem(endpointKey);
  } catch {
    // Storage blocked: sign-out then relies on the push service reporting the device gone.
  }
}

/**
 * Signing out on this device (task J2), without holding up the sign-out: the push address this
 * device subscribed with, read at once, which the sign-out form sends along so the server removes
 * the device before the session ends. The browser's own subscription is ended in the background.
 */
export function takeRememberedEndpoint(): string | null {
  let endpoint: string | null = null;
  try {
    endpoint = localStorage.getItem(endpointKey);
  } catch {
    endpoint = null;
  }
  remember(null);
  if (endpoint && pushSupported()) {
    void navigator.serviceWorker
      .getRegistration("/")
      .then((reg) => reg?.pushManager.getSubscription())
      .then((subscription) => subscription?.unsubscribe())
      .catch(() => undefined);
  }
  return endpoint;
}

function sameBytes(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && a.every((value, i) => value === b[i]);
}
