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

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const live = () => document.querySelector("[aria-live=polite]");
const idle = { state: "Idle", id: null, organisation: null, stage: null, done: 0, total: 0, percent: 0 };

describe("DemoResetButton", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("asks for confirmation, starts the reset as a job and follows its progress until it ends", async () => {
    const statuses = [
      { state: "Running", id: "r1", organisation: "demo-municipality", stage: "Seeding", done: 12, total: 70, percent: 17 },
      { state: "Running", id: "r1", organisation: "demo-company", stage: "Indexing", done: 69, total: 70, percent: 98 },
      { state: "Succeeded", id: "r1", organisation: null, stage: null, done: 70, total: 70, percent: 100 },
    ];
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === "POST") {
        return Promise.resolve(json({ state: "Running", id: "r1", organisation: "demo-municipality", stage: "Emptying", done: 0, total: 70, percent: 0 }, 202));
      }
      return Promise.resolve(json(fetchMock.mock.calls.filter(([, i]) => i?.method === "POST").length === 0 ? idle : statuses.shift() ?? statuses[2]));
    });
    vi.stubGlobal("fetch", fetchMock);
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });

    render(
      <NextIntlClientProvider locale="en" messages={en}>
        <DemoPanel demo={demo} currentUsername="admin@municipality.example" csrfToken="csrf-9" canReset lastReset={null} />
      </NextIntlClientProvider>,
    );

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith("/bff/api/v1/demo/reset", expect.objectContaining({ cache: "no-store" })));
    fireEvent.click(screen.getByRole("button", { name: "Reset demo" }));
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);
    expect(screen.getByText(/The audit log is kept/)).toBeInTheDocument();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fireEvent.click(screen.getByRole("button", { name: "Reset now" }));

    await waitFor(() => expect(screen.getByRole("progressbar", { name: "Progress of the reset" })).toBeInTheDocument());
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === "POST")!;
    expect(new Headers(post[1]?.headers).get("X-CSRF-Token")).toBe("csrf-9");

    await vi.advanceTimersByTimeAsync(2000);
    await waitFor(() => expect(live()).toHaveTextContent("17 % done · Department of Municipal Services: adding the sample data"));
    await vi.advanceTimersByTimeAsync(2000);
    await waitFor(() => expect(live()).toHaveTextContent("Gulf Horizon Trading: rebuilding the search index"));
    await vi.advanceTimersByTimeAsync(2000);
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("The demo was reset."));
    expect(reload).toHaveBeenCalled();
  });

  it("follows a reset that is already running when the panel opens", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ state: "Running", id: "r2", organisation: "demo-company", stage: "Emptying", done: 36, total: 70, percent: 51 })));
    render(
      <NextIntlClientProvider locale="ar" messages={ar}>
        <DemoPanel demo={demo} currentUsername={null} csrfToken="c" canReset lastReset={null} />
      </NextIntlClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("progressbar", { name: "مدى تقدّم إعادة الضبط" })).toHaveAttribute("value", "51"));
    expect(live()).toHaveTextContent("أفق الخليج للتجارة: حذف التغييرات");
    expect(screen.queryByRole("button", { name: "إعادة ضبط العرض التوضيحي" })).not.toBeInTheDocument();
  });

  it("says so when the reset cannot start, and can be cancelled", async () => {
    vi.stubGlobal("fetch", vi.fn((_url: string, init?: RequestInit) => Promise.resolve(init?.method === "POST" ? new Response(null, { status: 500 }) : json(idle))));
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
  });
});
