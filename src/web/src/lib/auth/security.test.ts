import { isSameOriginRequest, localeOf, safeReturnTo, secretsMatch, sessionKey } from "./security";

describe("safeReturnTo", () => {
  it.each([
    ["/ar/account", "/ar/account"],
    ["/en/account?tab=roles", "/en/account?tab=roles"],
  ])("keeps paths on this site: %s", (value, expected) => {
    expect(safeReturnTo(value, "en")).toBe(expected);
  });

  it.each([
    "https://evil.example/steal",
    "//evil.example/steal",
    "/\\evil.example",
    "javascript:alert(1)",
    "",
    null,
  ])("sends anything else home: %s", (value) => {
    expect(safeReturnTo(value, "ar")).toBe("/ar");
  });
});

describe("secretsMatch", () => {
  it("matches only identical, non-empty secrets", () => {
    expect(secretsMatch("abc", "abc")).toBe(true);
    expect(secretsMatch("abc", "abd")).toBe(false);
    expect(secretsMatch("abc", "abcd")).toBe(false);
    expect(secretsMatch("", "")).toBe(false);
    expect(secretsMatch(null, "abc")).toBe(false);
  });
});

describe("session keys", () => {
  it("never contain the session id itself", () => {
    const key = sessionKey("the-cookie-value");
    expect(key).toMatch(/^zelvo:session:/);
    expect(key).not.toContain("the-cookie-value");
  });
});

describe("request checks", () => {
  const app = new URL("http://localhost:3000/en");

  it("accepts only the app's own origin", () => {
    expect(isSameOriginRequest("http://localhost:3000", app)).toBe(true);
    expect(isSameOriginRequest("http://localhost:3001", app)).toBe(false);
    expect(isSameOriginRequest("null", app)).toBe(false);
    expect(isSameOriginRequest(null, app)).toBe(false);
  });

  it("falls back to English for unknown languages", () => {
    expect(localeOf("ar")).toBe("ar");
    expect(localeOf("fr")).toBe("en");
    expect(localeOf(null)).toBe("en");
  });
});
