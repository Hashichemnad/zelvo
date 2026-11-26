import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, forgetBranding, forgetLocalizationSettings, forgetPrivacyNotice } from "@/lib/api";
import { authConfigFor } from "@/lib/auth/config";
import { isSameOriginRequest, secretsMatch } from "@/lib/auth/security";
import { getSession } from "@/lib/auth/session";
import { isLocale, languageCookieMaxAgeSeconds, languageCookieName } from "@/lib/localization";
import { isVisitorPath } from "@/lib/visitor-paths";

/**
 * The browser's way to the public API: forwards /bff/api/v1/... to the API with the session's
 * access token, so tokens never reach the browser. Changes need a same-origin request and the
 * session's CSRF token in the `X-CSRF-Token` header.
 */
async function forward(request: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const config = (await authConfigFor(request));
  const { path } = await params;
  if (path[0] !== "v1" || path.some((part) => part === ".." || part === ".")) {
    return problem(404, "not-found");
  }

  const session = await getSession(config);
  const changes = !["GET", "HEAD"].includes(request.method);
  if (!session) {
    // Visitors reach only the public knowledge base (D1), and change only views and feedback,
    // from this site's own pages.
    if (!isVisitorPath(request.method, path)) {
      return problem(401, "unauthenticated");
    }
    if (changes && !isSameOriginRequest(request.headers.get("origin"), config.appUrl)) {
      return problem(403, "csrf-failed");
    }
  } else if (
    changes &&
    (!isSameOriginRequest(request.headers.get("origin"), config.appUrl) ||
      !secretsMatch(request.headers.get("x-csrf-token"), session.data.csrfToken))
  ) {
    return problem(403, "csrf-failed");
  }

  const target = `/api/${path.map(encodeURIComponent).join("/")}${request.nextUrl.search}`;
  // Live updates (task B4): the SignalR connection's requests are long (Server-Sent Events, long
  // polling), so they end when the browser goes away rather than after the usual timeout.
  const live = path[1] === "live";
  // "Reset demo" empties and reseeds every sample organisation, which takes longer than the usual timeout.
  const demoReset = request.method === "POST" && path.join("/") === "v1/demo/reset";
  // Task F4: update packages are large, so their upload is streamed to the API, not buffered.
  // Task M1: so are malware definition files (main.cvd is about 90 MB).
  const packageUpload = request.method === "POST" && ["v1/updates/packages", "v1/malware-definitions/files"].includes(path.join("/"));
  const headers = new Headers();
  // The chat token proves a visitor's chat is theirs (task E2).
  for (const name of ["content-type", "accept", "accept-language", "if-match", "x-chat-token", ...(live ? ["x-requested-with", "x-signalr-user-agent"] : [])]) {
    const value = request.headers.get(name);
    if (value) {
      headers.set(name, value);
    }
  }
  const response = await apiFetch(target, session?.data.accessToken ?? "", config.tenant, {
    method: request.method,
    headers,
    body: changes ? (packageUpload ? request.body : await request.arrayBuffer()) : undefined,
    ...(packageUpload ? { duplex: "half", signal: AbortSignal.timeout(3_600_000) } as RequestInit : {}),
    ...(live ? { signal: request.signal } : demoReset ? { signal: AbortSignal.timeout(300_000) } : {}),
  });

  // A saved profile language applies on this device right away (LANG-2).
  if (request.method === "PUT" && path.join("/") === "v1/me/preferences" && response.ok) {
    const text = await response.text();
    const result = new NextResponse(text, { status: response.status });
    copyHeaders(response, result);
    try {
      const language = (JSON.parse(text) as { effective?: { language?: unknown } }).effective?.language;
      if (isLocale(language)) {
        result.cookies.set(languageCookieName, language, { path: "/", sameSite: "lax", maxAge: languageCookieMaxAgeSeconds });
      }
    } catch {
      // Not JSON: nothing to remember.
    }
    return result;
  }

  if (request.method === "PUT" && path.join("/") === "v1/settings/localization" && response.ok) {
    // Visitors' pages read the tenant defaults through a short cache.
    forgetLocalizationSettings(config.tenant);
  }

  if (request.method === "POST" && /^v1\/settings\/branding\/(publish|discard)$/.test(path.join("/")) && response.ok) {
    // Task F1: every page shows the published branding at once.
    forgetBranding(config.tenant);
  }

  if (request.method === "POST" && path.join("/") === "v1/privacy/notices/draft/publish" && response.ok) {
    // Task F5: the footer link and visitors' forms follow the new notice at once.
    forgetPrivacyNotice(config.tenant);
  }

  const result = new NextResponse(response.status === 204 ? null : response.body, { status: response.status });
  copyHeaders(response, result);
  if (live) {
    // Stream events as they come; proxies in front of the web app must not buffer them.
    result.headers.set("x-accel-buffering", "no");
    if (!result.headers.has("cache-control")) {
      result.headers.set("cache-control", "no-cache");
    }
  }
  return result;
}

function copyHeaders(from: Response, to: NextResponse) {
  // Downloads (attachments) keep the API's disposition and sandboxing headers.
  for (const name of [
    "content-type",
    "content-disposition",
    "content-security-policy",
    "x-content-type-options",
    "cache-control",
    "etag",
    "www-authenticate",
    "retry-after",
    "ratelimit-limit",
    "ratelimit-remaining",
    "ratelimit-reset",
    "zelvo-environment",
  ]) {
    const value = from.headers.get(name);
    if (value) {
      to.headers.set(name, value);
    }
  }
}

function problem(status: number, code: string) {
  return NextResponse.json({ status, code, type: `urn:zelvo:problem:${code}` }, {
    status,
    headers: { "content-type": "application/problem+json" },
  });
}

export { forward as GET, forward as POST, forward as PUT, forward as PATCH, forward as DELETE };
