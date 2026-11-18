/** A refused API call: HTTP status and the problem code (for example "rate-limited"), when the API sent one. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string | null,
  ) {
    super(message);
    this.name = "ApiError";
  }

  /** The API's rate limit was reached; try again after a short wait. */
  get rateLimited(): boolean {
    return this.status === 429;
  }
}

async function refused(response: Response, what: string): Promise<ApiError> {
  let code: string | null = null;
  try {
    const body = (await response.json()) as { code?: unknown };
    code = typeof body.code === "string" ? body.code : null;
  } catch {
    // No problem details: the status is enough.
  }
  return new ApiError(`${what} failed with ${response.status}`, response.status, code);
}

/**
 * Browser calls to the API go through the web app's proxy (/bff/api/...), which adds the
 * session's access token. Changes carry the session's CSRF token.
 */
export async function putJson<T>(path: string, body: unknown, csrfToken: string): Promise<T> {
  const response = await fetch(`/bff/api${path}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw await refused(response, `PUT ${path}`);
  }
  return (await response.json()) as T;
}

/** GET through the proxy. */
export async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`/bff/api${path}`, { headers: { Accept: "application/json" }, cache: "no-store" });
  if (!response.ok) {
    throw await refused(response, `GET ${path}`);
  }
  return (await response.json()) as T;
}

/** POST through the proxy, with the CSRF token. */
export async function postJson<T>(path: string, body: unknown, csrfToken: string): Promise<T> {
  const response = await fetch(`/bff/api${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw await refused(response, `POST ${path}`);
  }
  return (await response.json()) as T;
}
