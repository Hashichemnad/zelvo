import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import en from "../../../messages/en.json";
import { LocalizationSettingsForm } from "./LocalizationSettingsForm";

afterEach(() => vi.unstubAllGlobals());

describe("LocalizationSettingsForm", () => {
  it("saves the tenant defaults with the CSRF token", async () => {
    const saved = { defaultLanguage: "ar", defaultCalendar: "Hijri", defaultDigits: "Western", updatedAt: "2026-11-13T10:00:00Z" };
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(saved), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <LocalizationSettingsForm csrfToken="csrf-2" initial={{ defaultLanguage: "en", defaultCalendar: "Gregorian", defaultDigits: "Western" }} />
      </NextIntlClientProvider>,
    );

    fireEvent.change(screen.getByLabelText("Default language"), { target: { value: "ar" } });
    fireEvent.change(screen.getByLabelText("Default calendar"), { target: { value: "Hijri" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The defaults are saved."));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/settings/localization");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-2");
    expect(JSON.parse(init.body as string)).toEqual({ defaultLanguage: "ar", defaultCalendar: "Hijri", defaultDigits: "Western" });
  });

  it("names the language options in the page's language", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <LocalizationSettingsForm csrfToken="x" initial={{ defaultLanguage: "ar", defaultCalendar: "Gregorian", defaultDigits: "ArabicIndic" }} />
      </NextIntlClientProvider>,
    );

    expect(screen.getByLabelText("Default language")).toHaveDisplayValue("Arabic");
    expect(screen.getByLabelText("Default digits")).toHaveDisplayValue("Arabic-Indic (٠١٢٣٤٥٦٧٨٩)");
  });
});
