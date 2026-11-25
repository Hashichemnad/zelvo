import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { DateDisplayOptions } from "@/lib/format";
import { exportUrl, parseDays, type MyPrivacy, type PrivacyRequest } from "@/lib/privacy";
import { VoiceForm } from "@/components/voice/VoiceForm";
import { MyPrivacyPanel } from "./MyPrivacyPanel";
import { NoticeEditor, RetentionEditor } from "./PrivacySettingsPanel";
import { PrivacyRequestActions } from "./PrivacyRequestOfficer";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/account/privacy",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const display: DateDisplayOptions = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" };

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const lt = (en: string, ar: string) => ({ en, ar, missing: [] });

const mine = (patch: Partial<MyPrivacy> = {}): MyPrivacy => ({
  notice: { id: "n1", version: 2, title: lt("Privacy notice", "إشعار الخصوصية"), publishedAt: "2026-11-25T08:00:00Z" },
  noticeAccepted: false,
  noticeAcceptedAt: null,
  purposes: [{ id: "p1", key: "surveys", name: lt("Satisfaction surveys", "استبيانات الرضا"), description: lt("Short surveys", "استبيانات قصيرة"), granted: false, changedAt: null }],
  history: [],
  ...patch,
});

const request = (patch: Partial<PrivacyRequest> = {}): PrivacyRequest => ({
  id: "r1", number: "DSR-000001", type: "Erasure", state: "InProgress", channel: "Staff", details: null, receivedAt: "2026-11-20T08:00:00Z", dueAt: "2026-12-20T08:00:00Z",
  overdue: false, closedAt: null, resolution: null, exportedAt: null, erasedAt: null,
  subject: { id: "u1", name: "Mariam", email: "mariam@example.test", erased: false, staff: false }, identityCheck: "Emirates ID seen", handler: null,
  holdings: { tickets: 2, openTickets: 0, messages: 3, files: 1, chats: 0, notifications: 4, payments: 0, consents: 1, erasureRefusal: "None" },
  ...patch,
});

afterEach(() => vi.restoreAllMocks());

describe("privacy", () => {
  it("accepts retention periods of 30 days to 50 years and points downloads at the proxy", () => {
    expect(parseDays("")).toBeNull();
    expect(parseDays(" 365 ")).toBe(365);
    expect(parseDays("7")).toBeUndefined();
    expect(parseDays("1e3")).toBeUndefined();
    expect(parseDays(String(365 * 51))).toBeUndefined();
    expect(exportUrl(null)).toBe("/bff/api/v1/me/privacy/export");
    expect(exportUrl("r1")).toBe("/bff/api/v1/privacy/requests/r1/export");
  });

  it("lets a person accept the new notice and grant a purpose", async () => {
    const fetch = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json(mine({ noticeAccepted: true, noticeAcceptedAt: "2026-11-25T09:00:00Z" })))
      .mockResolvedValueOnce(json(mine({ noticeAccepted: true, purposes: [{ ...mine().purposes[0]!, granted: true }] })));
    wrap(<MyPrivacyPanel initial={mine()} initialRequests={[]} display={display} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "I have read and accept the privacy notice" }));
    await waitFor(() => expect(screen.getByTestId("notice-accepted")).toBeInTheDocument());
    expect(String(fetch.mock.calls[0]![0])).toContain("/v1/me/privacy/notice-acceptance");
    expect(JSON.parse(String(fetch.mock.calls[0]![1]!.body))).toEqual({ noticeId: "n1" });
    fireEvent.click(screen.getByRole("checkbox", { name: "Satisfaction surveys" }));
    await waitFor(() => expect(screen.getByRole("checkbox", { name: "Satisfaction surveys" })).toBeChecked());
    expect(fetch.mock.calls[1]![1]!.method).toBe("PUT");
    expect(screen.getByTestId("download-my-data")).toHaveAttribute("href", "/bff/api/v1/me/privacy/export");
  });

  it("shows the person's requests in Arabic and lets them withdraw an open one", async () => {
    const open = request({ type: "Access", state: "Received", holdings: null });
    const fetch = vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(json({})).mockResolvedValueOnce(json([{ ...open, state: "Withdrawn" }]));
    wrap(<MyPrivacyPanel initial={mine({ noticeAccepted: true })} initialRequests={[open]} display={{ ...display, locale: "ar" }} csrfToken="csrf" />, "ar");
    expect(screen.getByText(/الحصول على نسخة من بياناتي/, { selector: "li span" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "سحب الطلب DSR-000001" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "سحب الطلب DSR-000001" })).not.toBeInTheDocument());
    expect(String(fetch.mock.calls[0]![0])).toContain("/v1/me/privacy/requests/r1/withdraw");
  });

  it("refuses retention periods under 30 days and previews valid ones", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ tickets: { Incident: 3 }, accounts: 1 }));
    wrap(<RetentionEditor initial={{ requestDueDays: 30, ticketRetentionDays: {}, inactiveAccountDays: null }} csrfToken="csrf" />);
    fireEvent.change(screen.getByLabelText("Incidents"), { target: { value: "7" } });
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    expect(screen.getByText("Please check the values.")).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText("Incidents"), { target: { value: "365" } });
    fireEvent.change(screen.getByLabelText("Days before an unused requester account is erased"), { target: { value: "730" } });
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    await waitFor(() => expect(screen.getByTestId("retention-preview")).toHaveTextContent("Incidents: 3 tickets"));
    expect(String(fetch.mock.calls[0]![0])).toContain("incident=365");
    expect(String(fetch.mock.calls[0]![0])).toContain("inactiveAccounts=730");
  });

  it("says that a notice needs both languages when publishing is refused", async () => {
    vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json({}))
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json({ code: "validation-failed" }, 400));
    wrap(<NoticeEditor initial={[]} display={display} csrfToken="csrf" />);
    fireEvent.change(screen.getByLabelText("Title (English)"), { target: { value: "Privacy notice" } });
    fireEvent.click(screen.getByRole("button", { name: "Publish" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("both English and Arabic");
  });

  it("erases a person only after the request's number is typed, and explains refusals", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(request({ state: "Completed", erasedAt: "2026-11-25T10:00:00Z" })));
    const { unmount } = wrap(<PrivacyRequestActions initial={request()} csrfToken="csrf" />);
    const erase = screen.getByRole("button", { name: "Erase the person's data" });
    expect(erase).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Type DSR-000001 to confirm"), { target: { value: "dsr-000001" } });
    expect(erase).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Complete" })).not.toBeInTheDocument();
    fireEvent.click(erase);
    await waitFor(() => expect(screen.getByTestId("privacy-request-closed")).toBeInTheDocument());
    expect(String(fetch.mock.calls[0]![0])).toContain("/v1/privacy/requests/r1/erase");
    unmount();

    wrap(<PrivacyRequestActions initial={request({ holdings: { ...request().holdings!, openTickets: 1, erasureRefusal: "OpenTickets" } })} csrfToken="csrf" />);
    expect(screen.getByRole("alert")).toHaveTextContent("not resolved or closed");
    fireEvent.change(screen.getByLabelText("Type DSR-000001 to confirm"), { target: { value: "DSR-000001" } });
    expect(screen.getByRole("button", { name: "Erase the person's data" })).toBeDisabled();
  });

  it("asks anonymous senders to accept the published privacy notice first", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(async (input) =>
      String(input).includes("/privacy/notice") ? json({ id: "n1", version: 1 }) : json({ number: "SUG-000001", trackingCode: "ABCD1234EFGH", type: "Suggestion" }, 201));
    wrap(<VoiceForm type="Suggestion" csrfToken={null} categories={[]} locations={[]} />);
    const checkbox = await screen.findByRole("checkbox", { name: /I have read the privacy notice/ });
    fireEvent.change(screen.getByLabelText("What do you suggest?"), { target: { value: "Longer opening hours" } });
    fireEvent.click(screen.getByRole("button", { name: /anonymously/i }));
    expect(fetch.mock.calls.filter((c) => String(c[0]).includes("customer-voice"))).toHaveLength(0);
    fireEvent.click(checkbox);
    fireEvent.click(screen.getByRole("button", { name: /anonymously/i }));
    await waitFor(() => expect(fetch.mock.calls.some((c) => String(c[0]).includes("/v1/public/customer-voice"))).toBe(true));
    const sent = fetch.mock.calls.find((c) => String(c[0]).includes("/v1/public/customer-voice"))!;
    expect(JSON.parse(String(sent[1]!.body)).privacyNoticeAccepted).toBe(true);
  });
});
