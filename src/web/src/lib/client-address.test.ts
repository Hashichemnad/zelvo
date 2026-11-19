import { describe, expect, it } from "vitest";
import { clientAddress, trustedProxyHops } from "./client-address";

describe("clientAddress", () => {
  it("uses the connection's address when no proxy is in front", () => {
    expect(clientAddress("172.18.0.1", 0)).toBe("172.18.0.1");
  });

  it("takes the address the trusted proxy saw, ignoring what the client sent before it", () => {
    expect(clientAddress("1.2.3.4, 203.0.113.7", 1)).toBe("203.0.113.7");
    expect(clientAddress("1.2.3.4, 203.0.113.7, 10.0.0.2", 2)).toBe("203.0.113.7");
  });

  it("keeps IPv6 addresses and drops entries that are not addresses", () => {
    expect(clientAddress("2001:db8::1", 1)).toBe("2001:db8::1");
    expect(clientAddress("<script>, unknown", 1)).toBeNull();
    expect(clientAddress(null, 1)).toBeNull();
  });

  it("reads TRUSTED_PROXY_HOPS safely", () => {
    expect(trustedProxyHops("1")).toBe(1);
    expect(trustedProxyHops(undefined)).toBe(0);
    expect(trustedProxyHops("-3")).toBe(0);
    expect(trustedProxyHops("many")).toBe(0);
  });
});
