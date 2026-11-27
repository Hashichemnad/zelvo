import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { suggestionTitle, type ProblemDetectionSettings, type ProblemSuggestion } from "@/lib/problems";
import { ProblemDetectionSettingsForm } from "./ProblemDetectionSettingsForm";
import { DetectNowButton, SuggestionCard } from "./ProblemSuggestions";

const push = vi.fn();
const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push, refresh }),
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a>,
}));

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
  refresh.mockReset();
});

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const id = () => crypto.randomUUID();
const display = { locale: "en", calendar: "gregory", digits: "latn" } as const;

function incidents(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    id: id(), number: `INC-00010${i}`, title: `VPN disconnects ${i}`, state: "New" as const, priority: 3, createdAt: new Date(2026, 10, i + 1).toISOString(),
  }));
}

function suggestion(change: Partial<ProblemSuggestion> = {}): ProblemSuggestion {
  return {
    id: id(), kind: "SimilarIncidents", status: "Open", title: "VPN drops every hour", suggestedTitle: { en: "VPN drops every hour", ar: null },
    reason: "All report the VPN dropping at the hour.", confidence: 85, language: "en", dimension: null, category: null, configurationItem: null,
    windowStart: new Date(2026, 10, 1).toISOString(), windowEnd: new Date(2026, 10, 20).toISOString(), incidentCount: 10, baseline: null,
    incidents: incidents(10), problem: null, decidedBy: null, decidedAt: null, dismissReason: null, etag: "\"4\"",
    createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), ...change,
  };
}

describe("suggested problems", () => {
  it("names the incidents and raises the problem with the title the manager chose", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ id: "p1" }, 201));
    vi.stubGlobal("fetch", fetchMock);
    const s = suggestion();
    withIntl(<SuggestionCard suggestion={s} display={display} csrfToken="csrf" />);

    expect(screen.getByRole("heading", { name: "VPN drops every hour" })).toBeInTheDocument();
    expect(screen.getByText("AI confidence: 85 %.", { exact: false })).toBeInTheDocument();
    // The first eight incidents, then all of them on request.
    expect(screen.getAllByRole("listitem")).toHaveLength(8);
    fireEvent.click(screen.getByRole("button", { name: "Show all 10" }));
    expect(screen.getAllByRole("listitem")).toHaveLength(10);

    fireEvent.change(screen.getByLabelText("Title of the problem"), { target: { value: "VPN concentrator drops sessions" } });
    fireEvent.click(screen.getByRole("button", { name: "Raise the problem" }));
    await waitFor(() => expect(push).toHaveBeenCalledWith("/problems/p1"));
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toContain(`/problem-suggestions/${s.id}/accept`);
    expect(JSON.parse(init.body)).toEqual({ title: "VPN concentrator drops sessions" });
    expect(new Headers(init.headers).get("If-Match")).toBe("\"4\"");
  });

  it("needs a reason to dismiss and says so when someone decided first", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok({ code: "suggestion-decided", title: "Decided" }, 409)));
    withIntl(<SuggestionCard suggestion={suggestion()} display={display} csrfToken="csrf" />);
    const dismiss = screen.getByRole("button", { name: "Dismiss" });
    expect(dismiss).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Reason for dismissing"), { target: { value: "Known ISP outage" } });
    fireEvent.click(dismiss);
    expect(await screen.findByText("Someone already raised or dismissed this suggestion. Reload the page.")).toBeInTheDocument();
    expect(refresh).not.toHaveBeenCalled();
  });

  it("shows a trend in Arabic with its usual level and the title in Arabic", () => {
    const s = suggestion({
      kind: "Trend", dimension: "Category", title: null, reason: null, confidence: null, language: null, incidentCount: 12, baseline: 2.5,
      suggestedTitle: { en: "Rising incidents: Payroll", ar: "ارتفاع في البلاغات: الرواتب" },
      category: { id: id(), name: { en: "Payroll", ar: "الرواتب" }, path: { en: "HR › Payroll", ar: "الموارد البشرية › الرواتب" } },
    });
    withIntl(<SuggestionCard suggestion={s} display={{ ...display, locale: "ar" }} csrfToken="csrf" />, "ar");
    expect(screen.getByRole("heading", { name: "ارتفاع في البلاغات: الرواتب" })).toBeInTheDocument();
    expect(screen.getByText("اتجاه متصاعد")).toBeInTheDocument();
    expect(screen.getByText("الموارد البشرية › الرواتب")).toBeInTheDocument();
    expect(screen.getByText(/والمعتاد 2\.5/)).toBeInTheDocument();
    expect(suggestionTitle(s, "en")).toBe("Rising incidents: Payroll");
  });

  it("shows what a decided suggestion became", () => {
    withIntl(<SuggestionCard suggestion={suggestion({ status: "Accepted", problem: { id: "p9", number: "PRB-000009" }, decidedBy: { id: id(), displayName: "Lina" }, decidedAt: new Date().toISOString() })}
      display={display} csrfToken="csrf" />);
    expect(screen.getByRole("link", { name: "PRB-000009" })).toHaveAttribute("href", "/problems/p9");
    expect(screen.queryByRole("button", { name: "Raise the problem" })).not.toBeInTheDocument();
  });

  it("looks now and reports that similar incidents wait for the AI feature", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok({ similar: 0, trends: 1, updated: 0, similarSkipped: "ai-off" })));
    withIntl(<DetectNowButton csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "Look now" }));
    expect(await screen.findByText(/1 new suggestion\./)).toBeInTheDocument();
    expect(screen.getByText(/not compared until the AI feature/)).toBeInTheDocument();
    expect(refresh).toHaveBeenCalled();
  });

  it("keeps the detection settings in range and links to the AI feature while it is off", async () => {
    const fetchMock = vi.fn().mockImplementation((_url: string, init: RequestInit) => Promise.resolve(ok({ ...JSON.parse(String(init.body)), aiAvailable: false, aiReason: "ai-off" })));
    vi.stubGlobal("fetch", fetchMock);
    const initial: ProblemDetectionSettings = { similarEnabled: true, windowDays: 60, minimumIncidents: 5, trendsEnabled: true, trendMinimum: 10, trendFactor: 2, aiAvailable: false, aiReason: "ai-off" };
    withIntl(<ProblemDetectionSettingsForm initial={initial} csrfToken="csrf" />);
    expect(screen.getByRole("link", { name: "Settings → AI → Features" })).toHaveAttribute("href", "/settings/ai/features");
    const days = screen.getByLabelText("Days compared (30–90)");
    fireEvent.change(days, { target: { value: "120" } });
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    fireEvent.change(days, { target: { value: "45" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(JSON.parse(fetchMock.mock.calls[0]![1].body)).toMatchObject({ windowDays: 45, trendMinimum: 10, trendFactor: 2 });
  });
});
