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
});
