/** Which proxy paths visitors who are not signed in may use (the public knowledge base, D1, and public catalog services, C2). */

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
  if (area === "chat") {
    return isChatVisitorPath(method, kind, id, sub, subId);
  }
  if (area === "settings" && (kind === "knowledge" || kind === "customer-voice") && id === undefined) {
    return method === "GET";
  }
  // Task C6: anonymous complaints, inquiries and suggestions, following them with the tracking
  // code, and the portal's happiness meter. The API decides whether the tenant allows anonymous ones.
  if (area === "public" && kind === "customer-voice") {
    if (method !== "POST" || subId !== undefined) {
      return false;
    }
    if (id === undefined) {
      return sub === undefined;
    }
    return id === "track" && (sub === undefined || sub === "comments" || sub === "confirmation" || sub === "happiness");
  }
  // Task M2: the first administrator's one-time setup link (check it, then choose a password).
  if (area === "identity" && kind === "account-setup" && sub === undefined) {
    return method === "POST" && (id === undefined || id === "check");
  }
  // Task F5: the published privacy notice, which visitors read before they chat or send a message.
  if (area === "privacy" && kind === "notice" && id === undefined) {
    return method === "GET";
  }
  // Task D2: the portal's search-first home (visitors find public services and external articles).
  if (area === "portal" && kind === "search" && id === undefined) {
    return method === "GET";
  }
  if (area === "happiness" && kind === "portal" && id === undefined) {
    return method === "POST";
  }
  // Task C2: public catalog services, their cards and their images and document templates.
  if (area === "catalog" && kind === "published") {
    if (method !== "GET") {
      return false;
    }
    if (id === undefined) {
      return true;
    }
    if (!uuid.test(id)) {
      return false;
    }
    return (sub === undefined && subId === undefined) || (sub === "files" && subId !== undefined && uuid.test(subId));
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

/**
 * Live chat (task E2): the portal's widget, a widget's public details, starting a chat, leaving a
 * message and the visitor's own chat (the API checks its token).
 */
function isChatVisitorPath(method: string, kind?: string, id?: string, sub?: string, subId?: string): boolean {
  if (subId !== undefined) {
    return false;
  }
  if (kind === "portal-widget") {
    return method === "GET" && id === undefined;
  }
  if (id === undefined || !uuid.test(id)) {
    return false;
  }
  if (kind === "widgets") {
    return (sub === "public" && method === "GET") || ((sub === "sessions" || sub === "offline-messages") && method === "POST");
  }
  if (kind === "sessions") {
    return (sub === undefined && method === "GET") || ((sub === "messages" || sub === "end") && method === "POST");
  }
  return false;
}
