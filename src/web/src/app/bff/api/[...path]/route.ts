import { NextResponse, type NextRequest } from "next/server";
import { apiFetch, forgetLocalizationSettings } from "@/lib/api";
import { getAuthConfig } from "@/lib/auth/config";
import { isSameOriginRequest, secretsMatch } from "@/lib/auth/security";
import { getSession } from "@/lib/auth/session";
import { isLocale, languageCookieMaxAgeSeconds, languageCookieName } from "@/lib/localization";

/**
 * The browser's way to the public API: forwards /bff/api/v1/... to the API with the session's
 * access token, so tokens never reach the browser. Changes need a same-origin request and the
 * session's CSRF token in the `X-CSRF-Token` header.
 */
async function forward(request: NextRequest, { params }: { params: Promise<{ path: string[] }> }) {
  const config = getAuthConfig();
  const { path } = await params;
  if (path[0] !== "v1" || path.some((part) => part === ".." || part === ".")) {
    return problem(404, "not-found");
  }

  const session = await getSession(config);
  if (!session) {
    return problem(401, "unauthenticated");
  }

  const changes = !["GET", "HEAD"].includes(request.method);
  if (
    changes &&
    (!isSameOriginRequest(request.headers.get("origin"), config.appUrl) ||
      !secretsMatch(request.headers.get("x-csrf-token"), session.data.csrfToken))
  ) {
    return problem(403, "csrf-failed");
  }

  const target = `/api/${path.map(encodeURIComponent).join("/")}${request.nextUrl.search}`;
  const headers = new Headers();
  for (const name of ["content-type", "accept", "accept-language"]) {
    const value = request.headers.get(name);
    if (value) {
      headers.set(name, value);
    }
  }
  const response = await apiFetch(target, session.data.accessToken, config.tenant, {
    method: request.method,
    headers,
    body: changes ? await request.arrayBuffer() : undefined,
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

  const result = new NextResponse(response.body, { status: response.status });
  copyHeaders(response, result);
  return result;
}

function copyHeaders(from: Response, to: NextResponse) {
  for (const name of ["content-type", "www-authenticate"]) {
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
