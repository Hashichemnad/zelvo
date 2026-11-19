import { describe, expect, it } from "vitest";
import { pickTenant } from "./tenant";

describe("pickTenant", () => {
  it("always serves the installation's tenant outside demo installations", () => {
    expect(pickTenant("other", "default", [])).toBe("default");
    expect(pickTenant(undefined, "default", [])).toBe("default");
  });

  it("serves the chosen sample organisation in a demo installation", () => {
    expect(pickTenant("demo-company", "demo-municipality", ["demo-municipality", "demo-company"])).toBe("demo-company");
  });

  it("ignores tenants the demo installation does not list", () => {
    expect(pickTenant("default", "demo-municipality", ["demo-municipality", "demo-company"])).toBe("demo-municipality");
    expect(pickTenant(null, "default", ["demo-municipality", "demo-company"])).toBe("demo-municipality");
  });
});
