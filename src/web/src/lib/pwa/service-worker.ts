/**
 * The installable web app's service worker (module 19, task J1), served at /sw.js for the whole
 * site. As decided (decision 13, 2026-11-26):
 *
 * - **pages and API answers always come from the network** when online; nothing the API answers
 *   is cached by the service worker, so agents' queues and other people's data never are;
 * - the **app shell** (Next.js's hashed static files, fonts, Zelvo's icons) is cached as it is used,
 *   and the **offline page** in both languages when the worker installs, fetched without the
 *   session's cookies so it holds no one's name;
 * - a page that cannot load without a connection opens the offline page, which shows the
 *   requester's last-viewed tickets and unsent work kept by the page itself (lib/pwa/offline-store),
 *   only while the tenant allows offline data.
 *
 * The version changes with every build, so a new release replaces the shell and the offline page.
 *
 * Task J2: **web push**. The message (RFC 8291, decrypted by the browser) holds the event's title
 * in the person's language, the reference number and the page to open, never names or ticket
 * text. It is shown with the app's icon in the right language and direction; a tap opens or focuses
 * the page, only ever a page of this site.
 */
export function serviceWorkerSource(version: string): string {
  const config = JSON.stringify({
    version,
    shell: `zelvo-shell-${version}`,
    offline: { en: "/en/offline", ar: "/ar/offline" },
    precache: ["/zelvo-icons/any-192.png", "/zelvo-icons/any-512.png"],
  });
  return `/* Zelvo service worker (task J1). Generated; do not edit. */
"use strict";
const CONFIG = ${config};

self.addEventListener("install", (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(CONFIG.shell);
    // The offline pages without cookies: the visitor's version, nobody's name or data in it.
    for (const url of [CONFIG.offline.en, CONFIG.offline.ar, ...CONFIG.precache]) {
      try {
        const response = await fetch(new Request(url, { credentials: "omit", cache: "no-store" }));
        if (!response.ok) continue;
        if ((response.headers.get("content-type") || "").includes("text/html")) {
          // The scripts and styles the offline page needs, so it also works on its first offline visit.
          const html = await response.clone().text();
          const files = new Set(html.split(/["']/).filter((part) => part.startsWith("/_next/static/")).map((part) => (part.endsWith("\\\\") ? part.slice(0, -1) : part)));
          await Promise.all([...files].map((file) => cache.add(file).catch(() => undefined)));
        }
        await cache.put(url, response);
      } catch (error) {
        // Offline while installing: the next visit tries again.
      }
    }
    await self.skipWaiting();
  })());
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith("zelvo-") && name !== CONFIG.shell) await caches.delete(name);
    }
    await self.clients.claim();
  })());
});

self.addEventListener("message", (event) => {
  // Signing out empties the shell too (it holds no personal data, but a fresh start is cheap).
  if (event.data && event.data.type === "zelvo:clear") {
    event.waitUntil(caches.keys().then((names) => Promise.all(names.filter((n) => n.startsWith("zelvo-")).map((n) => caches.delete(n)))));
  }
});

// Task J2: web push.
self.addEventListener("push", (event) => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch (error) {
    data = {};
  }
  const lang = data.lang === "ar" ? "ar" : "en";
  const title = typeof data.title === "string" && data.title ? data.title : lang === "ar" ? "إشعار جديد" : "New notification";
  event.waitUntil(self.registration.showNotification(title, {
    body: typeof data.body === "string" ? data.body : "",
    lang,
    dir: data.dir === "rtl" ? "rtl" : "ltr",
    tag: typeof data.tag === "string" ? data.tag : undefined,
    icon: "/zelvo-icons/any-192.png",
    badge: "/zelvo-icons/any-192.png",
    data: { url: sameSitePath(data.url, lang) },
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const target = new URL(sameSitePath(event.notification.data && event.notification.data.url, "en"), self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of windows) {
      if (client.url === target && "focus" in client) return client.focus();
    }
    return self.clients.openWindow(target);
  })());
});

// Only a path of this site (never another site's address), else the notifications page.
function sameSitePath(value, lang) {
  return typeof value === "string" && value.startsWith("/") && !value.startsWith("//") && !value.includes("\\\\") ? value : "/" + lang + "/notifications";
}

function isShellFile(url) {
  return url.pathname.startsWith("/_next/static/") || url.pathname.startsWith("/zelvo-icons/");
}

function offlinePageFor(url) {
  return url.pathname === "/ar" || url.pathname.startsWith("/ar/") ? CONFIG.offline.ar : CONFIG.offline.en;
}

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (isShellFile(url)) {
    // Hashed file names never change: cache first.
    event.respondWith((async () => {
      const cache = await caches.open(CONFIG.shell);
      const cached = await cache.match(request);
      if (cached) return cached;
      const response = await fetch(request);
      if (response.ok) cache.put(request, response.clone());
      return response;
    })());
    return;
  }

  if (request.mode === "navigate") {
    // Pages: always the network; without one, the offline page of the page's language.
    event.respondWith((async () => {
      try {
        return await fetch(request);
      } catch (error) {
        const page = offlinePageFor(url);
        // Elsewhere, go to the offline page's own address (the page must be shown at the address it
        // was made for); there, the kept copy.
        if (url.pathname !== page) return Response.redirect(page, 302);
        const cache = await caches.open(CONFIG.shell);
        const cached = await cache.match(page);
        if (cached) return cached;
        return new Response("Offline", { status: 503, headers: { "Content-Type": "text/plain; charset=utf-8" } });
      }
    })());
  }
  // Everything else (API calls through /bff, images, the manifest) goes to the network as usual.
});
`;
}
