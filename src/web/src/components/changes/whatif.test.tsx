import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { WhatIf } from "@/lib/changes";
import { ChangeWhatIf } from "./ChangeWhatIf";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a>,
}));

afterEach(() => vi.unstubAllGlobals());

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;
const now = new Date().toISOString();

function whatIf(patch: Partial<WhatIf> = {}): WhatIf {
  return {
    changeId: "c1",
    blast: {
      items: [{ id: "ci1", number: "CI-000001", name: "web-01" }],
      impact: {
        items: [{ node: { kind: "ConfigurationItem", id: "ci2", number: "CI-000002", name: "E-services", localizedName: null, class: null }, depth: 1 }],
        businessServices: [{ node: { kind: "ConfigurationItem", id: "ci2", number: "CI-000002", name: "E-services", localizedName: null, class: null }, depth: 1 }],
        consumers: [{ node: { kind: "Department", id: "d1", number: null, name: "Customer happiness", localizedName: { en: "Customer happiness", ar: "إسعاد المتعاملين" }, class: null }, depth: 2 }],
        userCount: 1, truncated: false,
      },
      assetUsers: 1, peopleCount: 2,
    },
    similar: [{ id: "c0", number: "CHG-000001", title: "Upgrade web-01", kind: "Normal", outcome: "BackedOut", reviewNotes: "Sessions dropped.", implementedAt: now, score: 9, reasons: ["items", "words"], sharedItems: [] }],
    predicted: { level: "Medium", failureChance: 34, baseRate: 15, history: true, questionnaire: "Low",
      reasons: [{ cohort: "items", changes: 1, failed: 1, withProblems: 0, failureRate: 40 }] },
    explanation: null, computedAt: now, kept: false, ...patch,
  };
}

describe("what-if panel", () => {
  it("shows the blast radius, the predicted risk beside the questionnaire's and the similar changes", () => {
    withIntl(<ChangeWhatIf initial={whatIf()} aiAvailable={false} csrfToken="csrf" display={display} />);
    expect(screen.getByText("1 business service affected", { exact: false })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "E-services" })).toHaveAttribute("href", "/cmdb/ci2");
    expect(screen.getByText(/2 people affected, 1 of them through an asset/)).toBeInTheDocument();
    expect(screen.getByText(/34% chance of failing/)).toBeInTheDocument();
    expect(screen.getByText(/On the same items: 1 change, 1 failed or backed out/)).toBeInTheDocument();
    expect(screen.getByText("Backed out")).toBeInTheDocument();
    expect(screen.getByText(/the same items, similar words/)).toBeInTheDocument();
    expect(screen.getByText(/The AI explanation is off/)).toBeInTheDocument();
  });

  it("asks the AI for an explanation in Arabic and shows it", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(whatIf({ explanation: { text: "قد يمسّ التغيير خدمة واحدة.", language: "ar", at: now } })),
      { status: 200, headers: { "content-type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<ChangeWhatIf initial={whatIf()} aiAvailable csrfToken="csrf" display={{ ...display, locale: "ar" }} />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "الشرح بالعربية" }));
    await waitFor(() => expect(screen.getByText("قد يمسّ التغيير خدمة واحدة.")).toBeInTheDocument());
    expect(JSON.parse(String((fetchMock.mock.calls[0]![1] as RequestInit).body))).toEqual({ language: "ar" });
  });

  it("says when it is the simulation kept at approval, and leaves out similar changes when compact", () => {
    withIntl(<ChangeWhatIf initial={whatIf({ kept: true })} aiAvailable={false} csrfToken="csrf" display={display} compact />);
    expect(screen.getByText(/As it was when the change was approved/)).toBeInTheDocument();
    expect(screen.queryByTestId("what-if-similar")).not.toBeInTheDocument();
  });
});
