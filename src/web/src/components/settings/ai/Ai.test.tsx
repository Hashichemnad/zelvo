import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../../messages/ar.json";
import en from "../../../../messages/en.json";
import { aiFeatureKeys, type AiCall, type AiFeature, type AiProvider, type AiUsage } from "@/lib/ai";
import type { Page } from "@/lib/integrations";
import { sectionsFor } from "@/lib/settings-sections";
import { AiFeaturesPanel } from "./AiFeaturesPanel";
import { AiLogPanel } from "./AiLogPanel";
import { AiPricesPanel } from "./AiPricesPanel";
import { AiProvidersPanel } from "./AiProvidersPanel";
import { AiUsagePanel, shiftMonth } from "./AiUsagePanel";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/ai",
  useRouter: () => ({ push: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

const provider: AiProvider = {
  id: "p1",
  name: "Claude (production)",
  kind: "Anthropic",
  endpoint: null,
  apiVersion: null,
  status: "Active",
  apiKeySet: true,
  apiKeyChangedAt: "2026-11-20T08:00:00Z",
  lastTestAt: null,
  lastTestSucceeded: null,
  lastTestError: null,
  createdAt: "2026-11-20T08:00:00Z",
  updatedAt: "2026-11-20T08:00:00Z",
};

const totals = (key: string, calls: number, costUsd: number) => ({ key, calls, failedCalls: 0, inputTokens: calls * 1000, outputTokens: calls * 200, costUsd });

const usage = (state: "NoLimit" | "Ok" | "Warning" | "Reached", spent: number, limit: number): AiUsage => ({
  month: "2026-11",
  periodStart: "2026-11-01T00:00:00Z",
  periodEnd: "2026-12-01T00:00:00Z",
  totals: { ...totals("total", 12, spent), failedCalls: 1 },
  unpricedCalls: 2,
  budget: { monthlyLimitUsd: limit, spentUsd: spent, percent: limit ? Math.floor((spent * 100) / limit) : null, state, warningPercent: 80, periodStart: "2026-11-01T00:00:00Z", periodEnd: "2026-12-01T00:00:00Z" },
  days: Array.from({ length: 30 }, (_, i) => totals(`2026-11-${String(i + 1).padStart(2, "0")}`, i === 22 ? 12 : 0, i === 22 ? spent : 0)),
  features: [totals("summary", 12, spent)],
  models: [{ providerId: "p1", providerName: "Claude (production)", model: "claude-haiku", totals: totals("claude-haiku", 12, spent), unpricedCalls: 0 }],
});

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      {children}
    </NextIntlClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function keys(value: unknown, prefix = ""): string[] {
  return value && typeof value === "object"
    ? Object.entries(value as Record<string, unknown>).flatMap(([k, v]) => keys(v, `${prefix}${k}.`))
    : [prefix];
}

afterEach(() => vi.unstubAllGlobals());

describe("Settings → AI", () => {
  it("is shown to holders of ai.manage only", () => {
    expect(sectionsFor(["ai.manage"]).map((s) => s.key)).toEqual(["ai"]);
    expect(sectionsFor(["settings.manage"]).map((s) => s.key)).not.toContain("ai");
  });

  it("has every text in English and Arabic", () => {
    expect(keys(ar.settings.ai).sort()).toEqual(keys(en.settings.ai).sort());
    for (const feature of [...aiFeatureKeys, "test"]) {
      expect(en.settings.ai.featureNames).toHaveProperty(feature);
    }
  });
});

describe("AiProvidersPanel", () => {
  it("says AI is off while no provider is connected, and connects one without ever showing the key", async () => {
    const fetchMock = vi.fn((url: string, init?: RequestInit) =>
      Promise.resolve(init?.method === "POST" ? json(provider, 201) : json([provider])),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AiProvidersPanel initial={[]} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    expect(screen.getByTestId("ai-providers-empty")).toHaveTextContent("AI is off");
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Claude (production)" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "test-key-value" } });
    expect(screen.getByLabelText("API key")).toHaveAttribute("type", "password");
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));

    await waitFor(() => expect(screen.getByTestId("ai-provider")).toBeInTheDocument());
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/ai/providers");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-1");
    expect(JSON.parse(init.body as string)).toMatchObject({ kind: "Anthropic", name: "Claude (production)", apiKey: "test-key-value" });
    expect(screen.getByTestId("ai-provider-key")).toHaveTextContent("Stored encrypted");
    expect(document.body).not.toHaveTextContent("test-key-value");
    expect(screen.getByLabelText("API key")).toHaveValue("");
  });

  it("explains a refused endpoint", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json({ code: "validation-failed", endpointProblem: "address-not-allowed", errors: { endpoint: ["x"] } }, 400))));
    wrap(<AiProvidersPanel initial={[]} csrfToken="c" display={{ ...display, locale: "en" }} />);

    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "OpenAICompatible" } });
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Local" } });
    fireEvent.change(screen.getByLabelText("Endpoint"), { target: { value: "http://169.254.169.254/v1" } });
    fireEvent.click(screen.getByRole("button", { name: "Connect" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Zelvo may not connect to this address");
  });

  it("tests a model and shows the answer, or why it failed", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(json({ succeeded: true, answer: "OK", errorCode: null, error: null, durationMs: 840, inputTokens: 120, outputTokens: 2, callId: 5 }))
      .mockResolvedValueOnce(json([provider]))
      .mockResolvedValueOnce(json({ succeeded: false, answer: null, errorCode: "provider-auth-failed", error: "The provider refused the API key (401).", durationMs: 90, inputTokens: 0, outputTokens: 0, callId: 6 }))
      .mockResolvedValueOnce(json([provider]));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AiProvidersPanel initial={[provider]} csrfToken="c" display={{ ...display, locale: "en" }} />);

    const card = screen.getByTestId("ai-provider");
    fireEvent.change(within(card).getByLabelText("Model to test"), { target: { value: "claude-haiku" } });
    fireEvent.click(within(card).getByRole("button", { name: "Test" }));
    expect(await screen.findByTestId("ai-test-result")).toHaveTextContent("The model answered in 840 ms.");
    expect(JSON.parse((fetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toEqual({ model: "claude-haiku" });

    fireEvent.click(within(card).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(screen.getByTestId("ai-test-result")).toHaveTextContent("The provider refused the API key."));
  });

  it("is in Arabic with Western-script technical values", () => {
    wrap(<AiProvidersPanel initial={[{ ...provider, kind: "OpenAICompatible", endpoint: "https://llm.example.ae/v1" }]} csrfToken="c" display={{ ...display, locale: "ar", digits: "arab" }} />, "ar");

    const card = screen.getByTestId("ai-provider");
    expect(card).toHaveTextContent("متوافق مع OpenAI");
    expect(within(card).getByText("https://llm.example.ae/v1")).toHaveAttribute("dir", "ltr");
    expect(card).toHaveTextContent("مفعّل");
  });
});

describe("AiFeaturesPanel", () => {
  const features: AiFeature[] = aiFeatureKeys.map((feature) => ({ feature, enabled: false, providerId: null, model: null, updatedAt: null }));

  it("sets a provider and model per feature", async () => {
    const fetchMock = vi.fn(() => Promise.resolve(json({ feature: "summary", enabled: true, providerId: "p1", model: "claude-sonnet", updatedAt: "2026-11-23T10:00:00Z" })));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AiFeaturesPanel initial={features} providers={[provider]} csrfToken="c" />);

    const form = screen.getByTestId("ai-feature-summary");
    fireEvent.click(within(form).getByRole("checkbox", { name: "On" }));
    fireEvent.change(within(form).getByLabelText("Provider"), { target: { value: "p1" } });
    fireEvent.change(within(form).getByLabelText("Model"), { target: { value: "claude-sonnet" } });
    fireEvent.click(within(form).getByRole("button", { name: /Save/ }));

    expect(await within(form).findByRole("status")).toHaveTextContent("Saved.");
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/ai/features/summary");
    expect(JSON.parse(init.body as string)).toEqual({ enabled: true, providerId: "p1", model: "claude-sonnet" });
  });

  it("says a feature needs a provider and a model before it can be on", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json({ code: "validation-failed", errors: { enabled: ["x"] } }, 400))));
    wrap(<AiFeaturesPanel initial={features} providers={[]} csrfToken="c" />);

    expect(screen.getByText("Connect a provider first.")).toBeInTheDocument();
    const form = screen.getByTestId("ai-feature-triage");
    fireEvent.click(within(form).getByRole("checkbox", { name: "On" }));
    fireEvent.click(within(form).getByRole("button", { name: /Save/ }));
    expect(await within(form).findByRole("alert")).toHaveTextContent("Choose a provider and a model");
  });
});

describe("AiUsagePanel", () => {
  it("shows the month's cost, calls and tokens, and warns at 80 % of the limit", () => {
    wrap(<AiUsagePanel initial={usage("Warning", 85, 100)} csrfToken="c" display={{ ...display, locale: "en" }} />);

    const tiles = screen.getByTestId("ai-usage-totals");
    expect(tiles).toHaveTextContent("$85.00");
    expect(tiles).toHaveTextContent("12");
    expect(screen.getByTestId("ai-budget-warning")).toHaveTextContent("80 % or more");
    expect(screen.getByRole("meter")).toHaveAttribute("aria-valuenow", "85");
    expect(screen.getByText(/2 calls have no cost/)).toBeInTheDocument();
    expect(screen.getByRole("table", { name: "By feature" })).toHaveTextContent("Thread summaries");
  });

  it("says AI is stopped when the limit is reached, in Arabic", () => {
    wrap(<AiUsagePanel initial={usage("Reached", 100, 100)} csrfToken="c" display={{ ...display, locale: "ar", digits: "arab" }} />, "ar");

    expect(screen.getByTestId("ai-budget-reached")).toHaveTextContent("توقفت ميزات الذكاء الاصطناعي");
    expect(screen.getByTestId("ai-usage-totals")).toHaveTextContent("١٠٠");
  });

  it("saves a new limit and shows the daily figures as a table", async () => {
    const fetchMock = vi.fn((url: string) => Promise.resolve(json(url.includes("/limit") ? usage("Ok", 10, 500).budget : usage("Ok", 10, 500))));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AiUsagePanel initial={usage("NoLimit", 10, 0)} csrfToken="c" display={{ ...display, locale: "en" }} />);

    expect(screen.getByTestId("ai-budget")).toHaveTextContent("No limit is set");
    fireEvent.change(screen.getByLabelText("Limit in US dollars"), { target: { value: "500" } });
    fireEvent.click(screen.getByRole("button", { name: "Save limit" }));
    await waitFor(() => expect(screen.getByTestId("ai-budget")).toHaveTextContent("$10.00 of $500.00"));
    expect(JSON.parse((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ monthlyLimitUsd: 500 });

    fireEvent.click(screen.getByRole("button", { name: "Show as table" }));
    expect(screen.getByRole("table", { name: "Cost per day" })).toHaveTextContent("23 November 2026");
    expect(shiftMonth("2026-01", -1)).toBe("2025-12");
  });
});

describe("AiPricesPanel", () => {
  it("sets the price of a model in US dollars per million tokens", async () => {
    const price = { providerId: "p1", model: "claude-haiku", inputPerMillion: 1, outputPerMillion: 5, updatedAt: "2026-11-23T10:00:00Z" };
    const fetchMock = vi.fn((_url: string, init?: RequestInit) => Promise.resolve(json(init?.method === "PUT" ? price : [price])));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AiPricesPanel initial={[]} providers={[provider]} csrfToken="c" display={{ ...display, locale: "en" }} />);

    fireEvent.change(screen.getByLabelText("Model"), { target: { value: "claude-haiku" } });
    fireEvent.change(screen.getByLabelText("Input, USD per million tokens"), { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Output, USD per million tokens"), { target: { value: "5" } });
    fireEvent.click(screen.getByRole("button", { name: "Save price" }));

    expect(await screen.findByTestId("ai-price-row")).toHaveTextContent("$5.00");
    expect(JSON.parse((fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ providerId: "p1", model: "claude-haiku", inputPerMillion: 1, outputPerMillion: 5 });
  });
});

describe("AiLogPanel", () => {
  const call: AiCall = {
    id: 7,
    occurredAt: "2026-11-23T10:00:00Z",
    feature: "summary",
    providerId: "p1",
    providerName: "Claude (production)",
    providerKind: "Anthropic",
    model: "claude-haiku",
    userId: "u1",
    userName: "Aisha",
    entityType: "ticket",
    entityId: "INC-000001",
    outcome: "Succeeded",
    errorCode: null,
    error: null,
    inputTokens: 1000,
    outputTokens: 200,
    costUsd: 0.006,
    durationMs: 900,
    injectionSuspected: true,
    maskedValues: 3,
    correlationId: "abc",
  };
  const page: Page<AiCall> = { items: [call], page: 1, pageSize: 25, totalCount: 1 };

  it("lists calls with injection flags and shows the masked text on demand", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(json({ call, prompt: "[system]\n… [PERSON_1] cannot print", response: "[PERSON_1] cannot print.", textExpired: false }))));
    wrap(<AiLogPanel initial={page} display={{ ...display, locale: "en" }} />);

    const row = screen.getByTestId("ai-log-row");
    expect(row).toHaveTextContent("Thread summaries");
    expect(row).toHaveTextContent("Possible prompt injection");
    expect(row).toHaveTextContent("3 personal values masked");
    expect(row).toHaveTextContent("$0.0060");
    fireEvent.click(within(row).getByRole("button", { name: "Details" }));
    expect(await within(row).findByText(/\[PERSON_1\] cannot print\.$/)).toBeInTheDocument();
  });
});
