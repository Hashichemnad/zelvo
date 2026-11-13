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
