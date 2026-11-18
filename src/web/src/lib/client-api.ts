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
    throw new Error(`PUT ${path} failed with ${response.status}`);
  }
  return (await response.json()) as T;
}

/** GET through the proxy. */
export async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`/bff/api${path}`, { headers: { Accept: "application/json" }, cache: "no-store" });
  if (!response.ok) {
    throw new Error(`GET ${path} failed with ${response.status}`);
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
    throw new Error(`POST ${path} failed with ${response.status}`);
  }
  return (await response.json()) as T;
}
