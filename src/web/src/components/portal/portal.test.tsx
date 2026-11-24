import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { PortalTicket } from "@/lib/portal";
import { PortalSearch } from "./PortalSearch";
import { TicketProgressCard } from "./TicketProgress";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

const display = { locale: "en", calendar: "gregory" as const, digits: "latn" as const, timeZone: "Asia/Dubai" };

function ticket(patch: Partial<PortalTicket> = {}): PortalTicket {
  return {
    id: "0190a8b2-6c1e-7d3a-9f00-1234567890ab", number: "REQ-000004", type: "ServiceRequest", title: "New laptop", state: "InProgress", open: true,
    team: { id: "g", name: { en: "IT service desk", ar: "مكتب خدمات تقنية المعلومات" } }, service: null,
    progress: { steps: [{ key: null, name: { en: "Received", ar: "استلام" } }, { key: null, name: { en: "Ordering", ar: "الطلب" } }, { key: null, name: { en: "Delivered", ar: "التسليم" } }], current: 1 },
    path: "/my-requests/0190a8b2-6c1e-7d3a-9f00-1234567890ab", createdAt: "2026-11-20T08:00:00Z", updatedAt: "2026-11-21T08:00:00Z", ...patch,
  };
}

describe("portal (task D2)", () => {
  afterEach(() => vi.restoreAllMocks());

  it("shows where a ticket is and which team has it, in text", () => {
    wrap(<TicketProgressCard ticket={ticket()} display={display} />);
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuetext", "Step 2 of 3: Ordering");
    expect(screen.getByText("With IT service desk")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "New laptop" })).toHaveAttribute("href", "/en/my-requests/0190a8b2-6c1e-7d3a-9f00-1234567890ab");
  });

  it("names the generic steps in Arabic", () => {
    wrap(<TicketProgressCard ticket={ticket({ type: "Complaint", progress: { steps: ["received", "handling", "resolved", "closed"].map((key) => ({ key: key as "received", name: null })), current: 0 } })} display={display} />, "ar");
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuetext", "المرحلة 1 من 4: تم الاستلام");
    expect(screen.getByText("شكوى")).toBeInTheDocument();
  });

  it("searches as the person types and groups the results", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({
      services: [{ type: "service", id: "s1", title: { en: "Printer toner", ar: null }, path: "/catalog/s1", attributes: {} }],
      articles: [{ type: "article", id: "a1", title: { en: "Clear a printer jam", ar: null }, path: "/help/articles/a1", attributes: {} }],
      tickets: [ticket()],
    }), { headers: { "content-type": "application/json" } }));
    wrap(<PortalSearch initialQuery="" display={display} signedIn />);
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "printer" } });
    await waitFor(() => expect(screen.getByText("3 results found.")).toBeInTheDocument());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/portal/search?q=printer");
    expect(screen.getByRole("link", { name: "Printer toner" })).toHaveAttribute("href", "/en/catalog/s1");
    expect(screen.getByRole("link", { name: "Clear a printer jam" })).toHaveAttribute("href", "/en/help/articles/a1");
    expect(screen.getByRole("heading", { name: "Your tickets" })).toBeInTheDocument();
  });

  it("offers to raise a ticket when nothing is found", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ services: [], articles: [], tickets: [] }), { headers: { "content-type": "application/json" } }));
    wrap(<PortalSearch initialQuery="zzz" display={display} signedIn={false} />);
    await waitFor(() => expect(screen.getByRole("link", { name: "Report a problem" })).toHaveAttribute("href", "/en/my-incidents/new"));
  });
});
