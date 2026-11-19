import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { vi } from "vitest";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import type { DemoInfo } from "@/lib/api";
import { DemoPanel } from "./DemoPanel";

const demo: DemoInfo = {
  organisation: "demo-municipality",
  organisations: [
    { slug: "demo-municipality", kind: "Government", name: { en: "Department of Municipal Services", ar: "دائرة الخدمات البلدية", missing: [] } },
    { slug: "demo-company", kind: "Company", name: { en: "Gulf Horizon Trading", ar: "أفق الخليج للتجارة", missing: [] } },
  ],
  users: [
    { persona: "requester", roleKey: "requester", roleName: { en: "Requester", ar: "مقدّم الطلب", missing: [] }, displayName: "Mariam Al Suwaidi", username: "requester@municipality.example" },
    { persona: "agent", roleKey: "agent", roleName: { en: "Agent", ar: "موظف الدعم", missing: [] }, displayName: "Hamad Al Ketbi", username: "agent@municipality.example" },
  ],
  lastResetAt: "2026-11-18T08:00:00Z",
};

describe("DemoPanel", () => {
  it("signs in as a persona with a POST form carrying the organisation, language and CSRF token", () => {
    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <DemoPanel demo={demo} currentUsername={null} csrfToken="token-1" canReset={false} lastReset={null} />
      </NextIntlClientProvider>,
    );

    const button = screen.getByRole("button", { name: /Agent\s*Hamad Al Ketbi/ });
    const form = button.closest("form")!;
    expect(form).toHaveAttribute("action", "/auth/demo");
    expect(form).toHaveAttribute("method", "post");
    const fields = Object.fromEntries(new FormData(form).entries());
    expect(fields).toEqual({ tenant: "demo-municipality", locale: "en", csrf: "token-1", persona: "agent" });
  });

  it("opens the other sample organisation and names it in Arabic", () => {
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <DemoPanel demo={demo} currentUsername={null} csrfToken={null} canReset={false} lastReset={null} />
      </NextIntlClientProvider>,
    );

    expect(screen.getByText("العرض التوضيحي")).toBeInTheDocument();
    const button = screen.getByRole("button", { name: /أفق الخليج للتجارة/ });
    const fields = Object.fromEntries(new FormData(button.closest("form")!).entries());
    expect(fields).toEqual({ tenant: "demo-company", locale: "ar" });
    expect(screen.queryByRole("button", { name: /دائرة الخدمات البلدية/ })).not.toBeInTheDocument();
  });

  it("marks the signed-in demo user and offers the reset only to those allowed", () => {
    const { rerender } = render(
      <NextIntlClientProvider locale="en" messages={en}>
        <DemoPanel demo={demo} currentUsername="agent@municipality.example" csrfToken="t" canReset={false} lastReset="18 November 2026" />
      </NextIntlClientProvider>,
    );

    expect(screen.getByRole("button", { name: /Hamad Al Ketbi · signed in/ })).toHaveAttribute("aria-current", "true");
    expect(screen.getByText("Last reset: 18 November 2026")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reset demo" })).not.toBeInTheDocument();

    rerender(
      <NextIntlClientProvider locale="en" messages={en}>
        <DemoPanel demo={demo} currentUsername="admin@municipality.example" csrfToken="t" canReset lastReset={null} />
      </NextIntlClientProvider>,
    );
    expect(screen.getByRole("button", { name: "Reset demo" })).toBeInTheDocument();
  });
});

describe("DemoResetButton", () => {
  it("asks for confirmation, then resets through the API proxy with the CSRF token", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ resetAt: "x", organisations: [] }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });

    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <DemoPanel demo={demo} currentUsername="admin@municipality.example" csrfToken="csrf-9" canReset lastReset={null} />
      </NextIntlClientProvider>,
    );

    fireEvent.click(screen.getByRole("button", { name: "Reset demo" }));
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.getByText(/The audit log is kept/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Reset now" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The demo was reset."));
    expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/demo/reset", expect.objectContaining({ method: "POST" }));
    expect(new Headers(fetchMock.mock.calls[0]?.[1]?.headers).get("X-CSRF-Token")).toBe("csrf-9");
    vi.unstubAllGlobals();
  });

  it("says so when the reset fails, and can be cancelled", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 409 })));
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <DemoPanel demo={demo} currentUsername={null} csrfToken="c" canReset lastReset={null} />
      </NextIntlClientProvider>,
    );

    fireEvent.click(screen.getByRole("button", { name: "إعادة ضبط العرض التوضيحي" }));
    fireEvent.click(screen.getByRole("button", { name: "إلغاء" }));
    expect(screen.getByRole("button", { name: "إعادة ضبط العرض التوضيحي" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "إعادة ضبط العرض التوضيحي" }));
    fireEvent.click(screen.getByRole("button", { name: "إعادة الضبط الآن" }));

    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("تعذّرت إعادة ضبط العرض التوضيحي"));
    vi.unstubAllGlobals();
  });
});
