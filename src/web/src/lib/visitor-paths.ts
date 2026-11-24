/** Which proxy paths visitors who are not signed in may use (the public knowledge base, D1). */

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Paths visitors who are not signed in may use through the proxy: reading published articles,
 * their images and the knowledge settings, counting views and giving "Was this helpful?". The API
 * still decides whether visitors may read the knowledge base at all (Settings → Knowledge).
 */
export function isVisitorPath(method: string, path: string[]): boolean {
  const [version, area, kind, id, sub, subId, ...rest] = path;
  if (version !== "v1" || rest.length > 0) {
    return false;
  }
  if (area === "settings" && kind === "knowledge" && id === undefined) {
    return method === "GET";
  }
  if (area !== "knowledge") {
    return false;
  }
  if (kind === "published") {
    if (id === undefined) {
      return method === "GET";
    }
    if (!uuid.test(id) || subId !== undefined) {
      return false;
    }
    return (
      (sub === undefined && method === "GET") ||
      (sub === "views" && method === "POST") ||
      (sub === "feedback" && (method === "GET" || method === "PUT"))
    );
  }
  return kind === "articles" && method === "GET" && id !== undefined && uuid.test(id) && sub === "images" && subId !== undefined && uuid.test(subId);
}
