import { serviceWorkerSource } from "@/lib/pwa/service-worker";

/**
 * Task J1: the installable web app's service worker for the whole site (scope "/"). Never cached
 * by the browser's HTTP cache, so a new build's worker is found at the next visit.
 */
export function GET() {
  return new Response(serviceWorkerSource(process.env.ZELVO_APP_BUILD ?? "dev"), {
    headers: {
      "Content-Type": "text/javascript; charset=utf-8",
      "Cache-Control": "no-cache, no-store, must-revalidate",
      "Service-Worker-Allowed": "/",
      "X-Content-Type-Options": "nosniff",
    },
  });
}
