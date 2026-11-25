import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { BrandingSettings } from "@/lib/branding";
import { BrandingPanel } from "./BrandingPanel";

afterEach(() => vi.unstubAllGlobals());

const settings: BrandingSettings = {
  draft: { productName: { en: "City Help", ar: "مساعدة المدينة" }, lightColour: "#0f5c8a", darkColour: "#7cc4f0", font: "ibm-plex-sans-arabic" },
  published: null,
  publishedAt: null,
  updatedAt: "2026-11-24T10:00:00Z",
  unpublished: true,
  checks: [],
  problems: [],
  images: { logoLight: { id: "i1", fileName: "logo.png", contentType: "image/png", size: 10, scanStatus: "Pending", url: "/api/v1/branding/images/LogoLight?draft=true&v=aa" } },
  fonts: [{ key: "ibm-plex-sans-arabic", family: "IBM Plex Sans Arabic" }, { key: "cairo", family: "Cairo" }],
};

function renderIn(locale: "en" | "ar", initial: BrandingSettings = settings) {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      <BrandingPanel initial={initial} domains={[]} sender={null} tenant="city" csrfToken="csrf" />
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });

describe("BrandingPanel", () => {
  it("checks contrast as the administrator types and previews both languages and modes", () => {
    renderIn("en");
    const checks = screen.getByTestId("contrast-checks");
    expect(within(checks).queryByText(/too low/)).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Brand colour, light mode"), { target: { value: "#ffd400" } });
    expect(within(checks).getByText(/Links and headings on light backgrounds/)).toHaveTextContent("too low");
    const light = screen.getByTestId("preview-light");
    expect(within(light).getByText("City Help")).toBeInTheDocument();
    expect(within(light).getByText("مساعدة المدينة")).toHaveAttribute("lang", "ar");
    expect(screen.getByTestId("preview-dark")).toBeInTheDocument();
    expect(screen.getByText(/Powered by Zelvo/)).toBeInTheDocument();
  });

  it("saves the draft with the CSRF token and shows images waiting for the malware scan", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ ...settings, draft: { ...settings.draft, font: "cairo" } }));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("ar");
    expect(screen.getByTestId("branding-image-LogoLight")).toHaveTextContent("جارٍ الفحص");
    fireEvent.change(screen.getByLabelText("الخط"), { target: { value: "cairo" } });
    fireEvent.click(screen.getByRole("button", { name: "حفظ المسودة" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/settings/branding");
    expect((init as RequestInit).headers).toMatchObject({ "X-CSRF-Token": "csrf" });
    expect(JSON.parse((init as RequestInit).body as string)).toMatchObject({ font: "cairo", productName: { en: "City Help", ar: "مساعدة المدينة" } });
  });

  it("does not publish while a problem is left", () => {
    renderIn("en", { ...settings, problems: ["contrast:light.text", "image:logoLight:Pending"] });
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(screen.getByTestId("branding-problems")).toHaveTextContent("Contrast too low");
    expect(screen.getByTestId("branding-problems")).toHaveTextContent("Logo for light backgrounds is not ready");
  });
});
