import { describe, expect, it } from "vitest";
import { redactAttributes, withoutQuery } from "./telemetry-redaction";

describe("telemetry redaction", () => {
  it("removes query strings and fragments from addresses", () => {
    expect(withoutQuery("http://api:8080/api/v1/search?q=Fatima%20Ali")).toBe("http://api:8080/api/v1/search");
    expect(withoutQuery("/auth/callback?code=secret#x")).toBe("/auth/callback");
    expect(withoutQuery("/en/account")).toBe("/en/account");
  });

  it("drops client addresses and user ids and keeps other attributes", () => {
    const attributes: Record<string, unknown> = {
      "http.url": "http://api:8080/api/v1/search?q=name",
      "url.query": "q=name",
      "http.client_ip": "203.0.113.7",
      "http.referer": "http://localhost:3000/en?x=1",
      "http.method": "GET",
      "http.status_code": 200,
    };

    redactAttributes(attributes);

    expect(attributes).toEqual({ "http.url": "http://api:8080/api/v1/search", "http.method": "GET", "http.status_code": 200 });
  });
});
