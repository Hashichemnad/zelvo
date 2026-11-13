import { browserLanguage, hasLanguagePrefix, resolveLanguage, withLanguage } from "./localization";

describe("browserLanguage", () => {
  it("takes the most preferred of English and Arabic", () => {
    expect(browserLanguage("fr-FR, ar-AE;q=0.8, en;q=0.5")).toBe("ar");
    expect(browserLanguage("en-GB,en;q=0.9,ar;q=0.8")).toBe("en");
    expect(browserLanguage("en;q=0.4, ar;q=0.9")).toBe("ar");
  });

  it("is null when neither is asked for", () => {
    expect(browserLanguage("fr-FR, de;q=0.5")).toBeNull();
    expect(browserLanguage("ar;q=0")).toBeNull();
    expect(browserLanguage("")).toBeNull();
    expect(browserLanguage(null)).toBeNull();
  });
});

describe("resolveLanguage", () => {
  const base = { deviceLanguage: null, signedIn: false, acceptLanguage: null, tenantDefault: null };

  it("keeps the visit's choice on this device first", () => {
    expect(resolveLanguage({ ...base, deviceLanguage: "ar", acceptLanguage: "en", tenantDefault: "en" })).toBe("ar");
  });

  it("uses the browser language for visitors, then the tenant default, then English", () => {
    expect(resolveLanguage({ ...base, acceptLanguage: "ar-AE", tenantDefault: "en" })).toBe("ar");
    expect(resolveLanguage({ ...base, acceptLanguage: "fr", tenantDefault: "ar" })).toBe("ar");
    expect(resolveLanguage({ ...base, acceptLanguage: "fr" })).toBe("en");
  });

  it("ignores the browser for signed-in users (their profile language is set at sign-in)", () => {
    expect(resolveLanguage({ ...base, signedIn: true, acceptLanguage: "ar", tenantDefault: "en" })).toBe("en");
  });

  it("ignores values that are not supported languages", () => {
    expect(resolveLanguage({ ...base, deviceLanguage: "fr", tenantDefault: "de" })).toBe("en");
  });
});

describe("language in paths", () => {
  it("recognises and replaces the language prefix", () => {
    expect(hasLanguagePrefix("/ar")).toBe(true);
    expect(hasLanguagePrefix("/en/account")).toBe(true);
    expect(hasLanguagePrefix("/english")).toBe(false);
    expect(withLanguage("/en/account", "ar")).toBe("/ar/account");
    expect(withLanguage("/ar", "en")).toBe("/en");
    expect(withLanguage("/settings/language", "ar")).toBe("/ar/settings/language");
    expect(withLanguage("/", "ar")).toBe("/ar");
  });
});

describe("language cookie", () => {
  it("is the cookie next-intl writes when a language is visited", async () => {
    const { routing } = await import("@/i18n/routing");
    const { languageCookieName, languageCookieMaxAgeSeconds } = await import("./localization");
    expect(routing.localeCookie).toMatchObject({ name: languageCookieName, maxAge: languageCookieMaxAgeSeconds });
  });
});
