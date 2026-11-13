import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@/lib/api";
import { getAuthConfig } from "@/lib/auth/config";
import { isSameOriginRequest, secretsMatch } from "@/lib/auth/security";
import { getSession } from "@/lib/auth/session";

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

  const result = new NextResponse(response.body, { status: response.status });
  for (const name of ["content-type", "www-authenticate"]) {
    const value = response.headers.get(name);
    if (value) {
      result.headers.set(name, value);
    }
  }
  return result;
}

function problem(status: number, code: string) {
  return NextResponse.json({ status, code, type: `urn:zelvo:problem:${code}` }, {
    status,
    headers: { "content-type": "application/problem+json" },
  });
}

export { forward as GET, forward as POST, forward as PUT, forward as PATCH, forward as DELETE };
