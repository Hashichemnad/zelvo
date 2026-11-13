import { formatLongDate, formatNumber } from "./format";

const date = new Date("2026-11-13T08:00:00Z");

describe("formatLongDate", () => {
  it("shows Gregorian dates in English", () => {
    const text = formatLongDate(date, { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" });
    expect(text).toContain("November");
    expect(text).toContain("2026");
  });

  it("shows Hijri dates with the Umm al-Qura calendar", () => {
    const text = formatLongDate(date, {
      locale: "en",
      calendar: "islamic-umalqura",
      digits: "latn",
      timeZone: "Asia/Dubai",
    });
    expect(text).toContain("1448");
  });

  it("uses Arabic-Indic digits when asked", () => {
    const text = formatLongDate(date, { locale: "ar", calendar: "gregory", digits: "arab", timeZone: "Asia/Dubai" });
    expect(text).toContain("٢٠٢٦");
    expect(text).not.toMatch(/[0-9]/);
  });
});

describe("formatNumber", () => {
  it("switches between Western and Arabic-Indic digits", () => {
    expect(formatNumber(1234, "ar", "latn")).toBe("1,234");
    expect(formatNumber(1234, "ar", "arab")).toBe("١٬٢٣٤");
  });
});

describe("formatDateTime", () => {
  it("uses the chosen calendar and digits", async () => {
    const { formatDateTime } = await import("./format");
    const text = formatDateTime(new Date("2026-11-13T19:18:00Z"), {
      locale: "ar",
      calendar: "islamic-umalqura",
      digits: "arab",
      timeZone: "Asia/Dubai",
    });
    expect(text).toMatch(/[٠-٩]/);
    expect(text).not.toMatch(/[0-9]/);
    expect(text).toContain("١٤٤٨");
  });
});
