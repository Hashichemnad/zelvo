import { describe, expect, it } from "vitest";
import { addRequestHeaders, contentSecurityPolicy, newNonce } from "./csp";

const directive = (policy: string, name: string) => policy.split("; ").find((d) => d.startsWith(`${name} `) || d === name);

describe("content security policy (task F7)", () => {
  it("lets only scripts with the response's nonce run and blocks plugins, base tags and framing", () => {
    const policy = contentSecurityPolicy({ nonce: "abc123" });
    expect(directive(policy, "script-src")).toBe("script-src 'self' 'nonce-abc123' 'strict-dynamic'");
    expect(directive(policy, "script-src")).not.toContain("unsafe-inline");
    expect(directive(policy, "script-src")).not.toContain("unsafe-eval");
    expect(directive(policy, "object-src")).toBe("object-src 'none'");
    expect(directive(policy, "base-uri")).toBe("base-uri 'none'");
    expect(directive(policy, "frame-ancestors")).toBe("frame-ancestors 'none'");
    expect(directive(policy, "connect-src")).toBe("connect-src 'self'");
    expect(directive(policy, "upgrade-insecure-requests")).toBeUndefined();
  });

  it("lets the chat widget be framed only by its websites, and adds development and HTTPS extras when asked", () => {
    const policy = contentSecurityPolicy({ nonce: "n", frameAncestors: ["'self'", "https://www.example.ae"], development: true, upgradeInsecureRequests: true });
    expect(directive(policy, "frame-ancestors")).toBe("frame-ancestors 'self' https://www.example.ae");
    expect(directive(policy, "script-src")).toContain("'unsafe-eval'");
    expect(directive(policy, "connect-src")).toBe("connect-src 'self' ws:");
    expect(directive(policy, "upgrade-insecure-requests")).toBe("upgrade-insecure-requests");
  });

  it("makes a different 128-bit nonce each time", () => {
    const first = newNonce();
    expect(atob(first)).toHaveLength(16);
    expect(newNonce()).not.toBe(first);
  });

  it("passes request headers through another middleware's response, keeping the ones it changed", () => {
    const request = new Request("http://localhost/en", { headers: { accept: "text/html", cookie: "a=1" } });
    const fresh = new Response(null);
    addRequestHeaders(fresh, request, { "x-nonce": "n1" });
    expect(fresh.headers.get("x-middleware-override-headers")?.split(",").sort()).toEqual(["accept", "cookie", "x-nonce"]);
    expect(fresh.headers.get("x-middleware-request-x-nonce")).toBe("n1");
    expect(fresh.headers.get("x-middleware-request-cookie")).toBe("a=1");

    const changed = new Response(null, { headers: { "x-middleware-override-headers": "accept,x-next-intl-locale", "x-middleware-request-x-next-intl-locale": "ar" } });
    addRequestHeaders(changed, request, { "x-nonce": "n2" });
    expect(changed.headers.get("x-middleware-override-headers")).toBe("accept,x-next-intl-locale,x-nonce");
    expect(changed.headers.get("x-middleware-request-x-next-intl-locale")).toBe("ar");
  });
});
