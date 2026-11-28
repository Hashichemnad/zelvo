/** A refused API call: HTTP status and the problem code (for example "rate-limited"), when the API sent one. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string | null,
    /** The problem details the API sent, for extra fields such as `errors` or `urlProblem`. */
    readonly details: Record<string, unknown> = {},
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
  let details: Record<string, unknown> = {};
  try {
    const body = (await response.json()) as Record<string, unknown>;
    details = body;
    code = typeof body.code === "string" ? body.code : null;
  } catch {
    // No problem details: the status is enough.
  }
  return new ApiError(`${what} failed with ${response.status}`, response.status, code, details);
}

/**
 * Browser calls to the API go through the web app's proxy (/bff/api/...), which adds the
 * session's access token. Changes carry the session's CSRF token.
 */
export async function putJson<T>(path: string, body: unknown, csrfToken: string, extraHeaders: Record<string, string> = {}): Promise<T> {
  const response = await fetch(`/bff/api${path}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken, ...extraHeaders },
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
export async function postJson<T>(path: string, body: unknown, csrfToken: string, extraHeaders: Record<string, string> = {}): Promise<T> {
  const response = await fetch(`/bff/api${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken, ...extraHeaders },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw await refused(response, `POST ${path}`);
  }
  // 204 No Content (for example removing a push device) has no body.
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

/** PATCH through the proxy, with the CSRF token. */
export async function patchJson<T>(path: string, body: unknown, csrfToken: string, extraHeaders: Record<string, string> = {}): Promise<T> {
  const response = await fetch(`/bff/api${path}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrfToken, ...extraHeaders },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw await refused(response, `PATCH ${path}`);
  }
  return (await response.json()) as T;
}

/** DELETE through the proxy, with the CSRF token. */
export async function deleteRequest(path: string, csrfToken: string): Promise<void> {
  const response = await fetch(`/bff/api${path}`, {
    method: "DELETE",
    headers: { Accept: "application/json", "X-CSRF-Token": csrfToken },
  });
  if (!response.ok) {
    throw await refused(response, `DELETE ${path}`);
  }
}

/** Uploads one file as multipart form data (field "file") through the proxy, with the CSRF token. */
export async function postFile<T>(path: string, file: File, csrfToken: string): Promise<T> {
  const form = new FormData();
  form.append("file", file, file.name);
  const response = await fetch(`/bff/api${path}`, {
    method: "POST",
    headers: { Accept: "application/json", "X-CSRF-Token": csrfToken },
    body: form,
  });
  if (!response.ok) {
    throw await refused(response, `POST ${path}`);
  }
  return (await response.json()) as T;
}

/**
 * POST a form with a file through the proxy, with the CSRF token. Answers with problem details
 * other than `acceptStatuses` are refusals; listed statuses (for example 422 with a result body)
 * are returned like successes.
 */
export async function postForm<T>(path: string, form: FormData, csrfToken: string, acceptStatuses: number[] = []): Promise<{ status: number; body: T }> {
  const response = await fetch(`/bff/api${path}`, {
    method: "POST",
    headers: { Accept: "application/json", "X-CSRF-Token": csrfToken },
    body: form,
  });
  if (!response.ok && !acceptStatuses.includes(response.status)) {
    throw await refused(response, `POST ${path}`);
  }
  return { status: response.status, body: (await response.json()) as T };
}
