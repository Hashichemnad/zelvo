import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import type { Preferences } from "@/lib/api";
import { PreferencesForm } from "./PreferencesForm";

const replace = vi.fn();
const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/account",
  useRouter: () => ({ replace, refresh }),
}));

const noChoices: Preferences = {
  language: null,
  calendar: null,
  digits: null,
  effective: { language: "en", calendar: "Hijri", digits: "Western" },
};
const defaults = { language: "en", calendar: "Hijri", digits: "Western" } as const;

function renderForm(locale: "en" | "ar", initial = noChoices) {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      <PreferencesForm initial={initial} defaults={defaults} csrfToken="csrf-1" />
    </NextIntlClientProvider>,
  );
}

function respondWith(body: Preferences) {
  const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status: 200 }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

afterEach(() => {
  vi.unstubAllGlobals();
  replace.mockReset();
  refresh.mockReset();
});

describe("PreferencesForm", () => {
  it("offers the organisation default, named, for each choice", () => {
    renderForm("en");

    expect(screen.getByLabelText("Language")).toHaveDisplayValue("Organisation default (English)");
    expect(screen.getByLabelText("Calendar")).toHaveDisplayValue("Organisation default (Hijri (Umm al-Qura))");
    expect(screen.getByLabelText("Digits")).toHaveDisplayValue("Organisation default (Western (0123456789))");
  });

  it("previews Arabic-Indic digits as soon as they are chosen", () => {
    renderForm("ar");

    expect(screen.getByTestId("preview-number").textContent).toMatch(/[0-9]/);
    fireEvent.change(screen.getByLabelText("الأرقام"), { target: { value: "ArabicIndic" } });
    expect(screen.getByTestId("preview-number").textContent).toMatch(/[٠-٩]/);
    expect(screen.getByTestId("preview-number").textContent).not.toMatch(/[0-9]/);
  });

  it("saves through the API proxy with the CSRF token, sending null for organisation defaults", async () => {
    const fetchMock = respondWith({ ...noChoices, calendar: "Gregorian", effective: { ...noChoices.effective, calendar: "Gregorian" } });
    renderForm("en");

    fireEvent.change(screen.getByLabelText("Calendar"), { target: { value: "Gregorian" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Your preferences are saved."));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/me/preferences");
    expect(init.method).toBe("PUT");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-1");
    expect(JSON.parse(init.body as string)).toEqual({ language: null, calendar: "Gregorian", digits: null });
    expect(refresh).toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
  });

  it("shows the same page in the new language when the language changes", async () => {
    respondWith({ ...noChoices, language: "ar", effective: { ...noChoices.effective, language: "ar" } });
    renderForm("en");

    fireEvent.change(screen.getByLabelText("Language"), { target: { value: "ar" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/account", { locale: "ar" }));
  });

  it("says so when saving fails", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("{}", { status: 403 })));
    renderForm("ar");

    fireEvent.click(screen.getByRole("button", { name: "حفظ" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("تعذّر حفظ تفضيلاتك"));
  });
});
