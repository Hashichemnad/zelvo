import { brandingCss, brandingImagePath, contrast, contrastChecks, defaultDarkColour, defaultLightColour } from "./branding";
import { customHost, forgetHosts, tenantOfHost } from "./host-tenant";

describe("branding", () => {
  it("computes WCAG contrast like the API", () => {
    expect(contrast("#000000", "#ffffff")).toBe(21);
    expect(contrastChecks(defaultLightColour, defaultDarkColour).every((c) => c.passed)).toBe(true);
    expect(contrastChecks("#ffd400", defaultDarkColour).find((c) => c.key === "light.text")?.passed).toBe(false);
  });

  it("writes only checked values into the style sheet", () => {
    const css = brandingCss({ lightColour: "#7a1f3d", darkColour: "#f2b8c6", font: "cairo" });
    expect(css).toContain("--color-brand:#7a1f3d");
    expect(css).toContain('"Cairo"');
    expect(css).toContain("prefers-color-scheme: dark");
    const hostile = brandingCss({ lightColour: "red;}body{display:none", darkColour: "</style><script>", font: "x\"}" });
    expect(hostile).not.toContain("display:none");
    expect(hostile).not.toContain("<script>");
    expect(hostile).toContain(defaultLightColour);
    expect(hostile).toContain("IBM Plex Sans Arabic");
  });

  it("serves images through the web app with the tenant in the path", () => {
    expect(brandingImagePath("demo-company", "LogoLight", "/api/v1/branding/images/LogoLight?v=abc123")).toBe("/branding/demo-company/LogoLight?v=abc123");
    expect(brandingImagePath("t", "Favicon", "/api/v1/branding/images/Favicon?draft=true&v=ff")).toBe("/branding/t/Favicon?v=ff&draft=true");
    expect(brandingImagePath("t", "Favicon", null)).toBeNull();
  });
});

describe("custom domains", () => {
  const app = new URL("http://localhost:3000");

  it("looks up only other DNS names than the installation's", () => {
    expect(customHost("localhost:3000", app)).toBeNull();
    expect(customHost("127.0.0.1", app)).toBeNull();
    expect(customHost("Support.Entity.Gov.AE:443", app)).toBe("support.entity.gov.ae");
    expect(customHost("bad host", app)).toBeNull();
    expect(customHost(undefined, app)).toBeNull();
  });

  it("caches answers, unknown hosts too", async () => {
    forgetHosts();
    const lookup = vi.fn(async (host: string) => (host === "support.entity.gov.ae" ? "entity" : null));
    expect(await tenantOfHost("support.entity.gov.ae", lookup, 0)).toEqual({ host: "support.entity.gov.ae", tenant: "entity" });
    expect(await tenantOfHost("support.entity.gov.ae", lookup, 1000)).toEqual({ host: "support.entity.gov.ae", tenant: "entity" });
    expect(await tenantOfHost("unknown.example", lookup, 0)).toBeNull();
    expect(await tenantOfHost("unknown.example", lookup, 1000)).toBeNull();
    expect(lookup).toHaveBeenCalledTimes(2);
    expect(await tenantOfHost("support.entity.gov.ae", lookup, 120_000)).not.toBeNull();
    expect(lookup).toHaveBeenCalledTimes(3);
  });
});
