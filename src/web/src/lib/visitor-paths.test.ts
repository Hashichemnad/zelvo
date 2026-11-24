import { isVisitorPath } from "./visitor-paths";

const id = "0190a8b2-6c1e-7d3a-9f00-1234567890ab";

describe("visitor paths of the API proxy", () => {
  it("lets visitors read published articles, images and knowledge settings, count views and give feedback", () => {
    expect(isVisitorPath("GET", ["v1", "knowledge", "published"])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "knowledge", "published", id])).toBe(true);
    expect(isVisitorPath("POST", ["v1", "knowledge", "published", id, "views"])).toBe(true);
    expect(isVisitorPath("PUT", ["v1", "knowledge", "published", id, "feedback"])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "knowledge", "published", id, "feedback"])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "knowledge", "articles", id, "images", id])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "settings", "knowledge"])).toBe(true);
  });

  it("lets visitors browse the catalog, read a service card and download its files (task C2)", () => {
    expect(isVisitorPath("GET", ["v1", "catalog", "published"])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "catalog", "published", id])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "catalog", "published", id, "files", id])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "catalog", "published", id, "request-form"])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "catalog", "published"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "catalog", "services"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "catalog", "published", "x"])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "service-requests"])).toBe(false);
  });

  it("refuses everything else", () => {
    expect(isVisitorPath("GET", ["v1", "knowledge", "articles"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "knowledge", "articles", id])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "knowledge", "articles", id, "images"])).toBe(false);
    expect(isVisitorPath("DELETE", ["v1", "knowledge", "published", id])).toBe(false);
    expect(isVisitorPath("PUT", ["v1", "settings", "knowledge"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "knowledge", "published", "not-a-uuid"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "knowledge", "published", id, "views", "x"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "incidents"])).toBe(false);
    expect(isVisitorPath("GET", ["v2", "knowledge", "published"])).toBe(false);
  });

  it("lets visitors send and follow anonymous complaints, inquiries and suggestions and rate the portal (task C6)", () => {
    expect(isVisitorPath("GET", ["v1", "settings", "customer-voice"])).toBe(true);
    expect(isVisitorPath("PUT", ["v1", "settings", "customer-voice"])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "public", "customer-voice"])).toBe(true);
    expect(isVisitorPath("POST", ["v1", "public", "customer-voice", "track"])).toBe(true);
    for (const sub of ["comments", "confirmation", "happiness"]) {
      expect(isVisitorPath("POST", ["v1", "public", "customer-voice", "track", sub])).toBe(true);
    }
    expect(isVisitorPath("GET", ["v1", "public", "customer-voice", "track"])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "public", "customer-voice", "track", "other"])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "happiness", "portal"])).toBe(true);
    expect(isVisitorPath("GET", ["v1", "customer-voice"])).toBe(false);
    expect(isVisitorPath("POST", ["v1", "tickets", id, "happiness"])).toBe(false);
  });

  it("lets visitors search the portal (task D2) but not list tickets", () => {
    expect(isVisitorPath("GET", ["v1", "portal", "search"])).toBe(true);
    expect(isVisitorPath("POST", ["v1", "portal", "search"])).toBe(false);
    expect(isVisitorPath("GET", ["v1", "me", "tickets"])).toBe(false);
  });
});
