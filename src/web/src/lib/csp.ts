/**
 * Task F7 (ASVS V3.4.3, V3.4.6): the web app's Content-Security-Policy. Every page is rendered per
 * request, so each response gets its own random nonce: Next.js puts it on its scripts (it reads
 * the nonce from the request's policy), and with 'strict-dynamic' only those scripts, and the
 * scripts they load, run. Inline styles stay allowed: React `style` attributes (progress bars,
 * charts, drag and drop) and the tenant's checked branding variables need them, and styles run
 * no code. Fetches, live updates (Server-Sent Events) and images stay on this site; frames only
 * show this site's own sandboxed previews. Only the chat widget page may be framed, by the
 * websites listed for the widget.
 */
export interface CspOptions {
  nonce: string;
  /** Who may frame the page: none, or 'self' and the chat widget's websites. */
  frameAncestors?: string[];
  /** Further sites the page may fetch from (the Teams tab: the Teams SDK's list of Teams' hosts). */
  connectSources?: string[];
  /** `next dev` needs eval for fast refresh. */
  development?: boolean;
  /** Ask browsers to load http:// resources over HTTPS (only when the site itself is HTTPS). */
  upgradeInsecureRequests?: boolean;
}

export function contentSecurityPolicy({ nonce, frameAncestors, connectSources = [], development = false, upgradeInsecureRequests = false }: CspOptions): string {
  const directives: [string, ...string[]][] = [
    ["default-src", "'self'"],
    ["script-src", "'self'", `'nonce-${nonce}'`, "'strict-dynamic'", ...(development ? ["'unsafe-eval'"] : [])],
    ["style-src", "'self'", "'unsafe-inline'"],
    ["img-src", "'self'", "blob:", "data:"],
    ["font-src", "'self'"],
    ["connect-src", "'self'", ...connectSources, ...(development ? ["ws:"] : [])],
    ["media-src", "'self'", "blob:"],
    ["frame-src", "'self'"],
    ["worker-src", "'self'", "blob:"],
    ["manifest-src", "'self'"],
    ["object-src", "'none'"],
    ["base-uri", "'none'"],
    ["frame-ancestors", ...(frameAncestors && frameAncestors.length > 0 ? frameAncestors : ["'none'"])],
  ];
  if (upgradeInsecureRequests) {
    directives.push(["upgrade-insecure-requests"]);
  }
  return directives.map((d) => d.join(" ")).join("; ");
}

/** A fresh nonce: 128 random bits, base64. */
export function newNonce(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return btoa(String.fromCharCode(...bytes));
}

/** Header names Next.js uses to change the request a page sees, from the proxy (middleware). */
const overrideHeader = "x-middleware-override-headers";
const requestHeaderPrefix = "x-middleware-request-";

/**
 * Passes extra request headers to the page through a response another middleware made (here
 * next-intl's), the way `NextResponse.next({ request: { headers } })` does: Next.js then gives the
 * page exactly the headers listed in `x-middleware-override-headers`, so the list keeps the
 * request's own headers and any the other middleware already changed.
 */
export function addRequestHeaders(response: Response, request: Request, extra: Record<string, string>): void {
  const listed = response.headers.get(overrideHeader);
  const names = new Set<string>();
  if (listed) {
    for (const name of listed.split(",")) {
      if (name.trim()) {
        names.add(name.trim().toLowerCase());
      }
    }
  } else {
    request.headers.forEach((value, name) => {
      names.add(name.toLowerCase());
      response.headers.set(`${requestHeaderPrefix}${name}`, value);
    });
  }
  for (const [name, value] of Object.entries(extra)) {
    names.add(name.toLowerCase());
    response.headers.set(`${requestHeaderPrefix}${name.toLowerCase()}`, value);
  }
  response.headers.set(overrideHeader, [...names].join(","));
}
