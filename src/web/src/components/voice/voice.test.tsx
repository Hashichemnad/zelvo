import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { HappinessSettings, VoiceRecord, VoiceSettings } from "@/lib/voice";
import { HappinessMeter } from "./HappinessMeter";
import { VoiceForm } from "./VoiceForm";
import { ConfirmResolution, VoiceFacts, VoiceProgress } from "./VoiceViews";
import { VoiceSettingsForm } from "./VoiceSettingsForm";

const push = vi.fn();
const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/contact",
  useRouter: () => ({ push, refresh }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

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

const happiness: HappinessSettings = {
  enabled: true, points: 3,
  question: { en: "How happy are you with how we handled this?", ar: "ما مدى سعادتك بطريقة معالجتنا لهذا الطلب؟" },
  portalQuestion: { en: "How happy are you with our services?", ar: "ما مدى سعادتك بخدماتنا؟" },
  labels: [{ en: "Unhappy", ar: "غير سعيد" }, { en: "Neutral", ar: "محايد" }, { en: "Happy", ar: "سعيد" }],
  askOnClosure: true, inPortal: true,
};

const settings: VoiceSettings = {
  anonymousEnabled: true,
  severities: [1, 2, 3].map((level) => ({ level, name: { en: ["High", "Medium", "Low"][level - 1]!, ar: ["عالية", "متوسطة", "منخفضة"][level - 1]! }, priority: level + 1 })),
  rootCauses: [{ key: "service-delay", name: { en: "Delay in delivering the service", ar: "تأخر في تقديم الخدمة" }, active: true }],
  inquiryPriority: 3, suggestionPriority: 4, happiness, updatedAt: null,
};

function record(patch: Partial<VoiceRecord> = {}): VoiceRecord {
  return {
    id: "0190a8b2-6c1e-7d3a-9f00-1234567890ab", number: "CMP-000001", type: "Complaint", title: "Long wait", description: "An hour",
    state: "Resolved", channel: "Portal", requester: { id: "u1", displayName: "Fatima" }, anonymous: false, loggedBy: null, assignee: null, group: null,
    category: null, service: null, priority: 3, severity: 2, rootCauseKey: "service-delay", confirmation: "Pending", confirmationAt: null,
    confirmationComment: null, review: null, reviewFeedback: null, convertedTo: null, convertedFrom: null, onHoldReason: null,
    resolutionNotes: "We added staff.", nextStates: [], canConfirm: true, rating: null, canRate: false, sla: [], etag: "\"1\"",
    createdAt: "2026-11-01T08:00:00Z", updatedAt: "2026-11-02T08:00:00Z", resolvedAt: "2026-11-02T08:00:00Z", closedAt: null, ...patch,
  };
}

const display = { locale: "en", calendar: "gregory" as const, digits: "latn" as const, timeZone: "Asia/Dubai" };

describe("customer voice (task C6)", () => {
  afterEach(() => vi.restoreAllMocks());

  it("asks the happiness question with one labelled choice per face and sends the score", async () => {
    const send = vi.fn().mockResolvedValue({ score: 3, points: 3, comment: null, createdAt: "2026-11-24T08:00:00Z" });
    wrap(<HappinessMeter settings={happiness} kind="ticket" send={send} />);
    const group = screen.getByRole("group", { name: "How happy are you with how we handled this?" });
    expect(group).toBeInTheDocument();
    expect(screen.getAllByRole("radio")).toHaveLength(3);
    expect(screen.getByRole("button", { name: "Send my rating" })).toBeDisabled();
    fireEvent.click(screen.getByRole("radio", { name: /Happy$/ }));
    fireEvent.click(screen.getByRole("button", { name: "Send my rating" }));
    await waitFor(() => expect(screen.getByText("Thank you for your rating")).toBeInTheDocument());
    expect(send).toHaveBeenCalledWith(3, "", null);
    expect(screen.getByText("You rated: Happy")).toBeInTheDocument();
  });

  it("shows the portal question in Arabic", () => {
    wrap(<HappinessMeter settings={happiness} kind="portal" send={vi.fn()} />, "ar");
    expect(screen.getByRole("group", { name: "ما مدى سعادتك بخدماتنا؟" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /محايد/ })).toBeInTheDocument();
  });

  it("sends anonymously and shows the number and tracking code once", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => json({ number: "CMP-000007", trackingCode: "K7P2QX9M4HTA", type: "Complaint" }, 201));
    wrap(<VoiceForm type="Complaint" csrfToken={null} categories={[]} locations={[]} />);
    expect(screen.getByText(/sent anonymously/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("What is your complaint about?"), { target: { value: "Rude reply at the counter" } });
    fireEvent.click(screen.getByRole("button", { name: "Send anonymously" }));
    await waitFor(() => expect(screen.getByTestId("anonymous-code")).toHaveTextContent("K7P2-QX9M-4HTA"));
    expect(screen.getByTestId("anonymous-number")).toHaveTextContent("CMP-000007");
    // Task F5: the form first asks whether a privacy notice is published (none here: no "version").
    const sent = fetchMock.mock.calls.find(([url]) => url === "/bff/api/v1/public/customer-voice")!;
    expect(JSON.parse(sent[1]!.body as string)).toEqual({ type: "Complaint", title: "Rude reply at the counter" });
  });

  it("asks for a reason before rejecting a complaint's resolution", async () => {
    const send = vi.fn().mockResolvedValue({});
    wrap(<ConfirmResolution send={send} />);
    fireEvent.click(screen.getByRole("button", { name: "No, it is not resolved" }));
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Why is it not resolved?"), { target: { value: "Still waiting" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(send).toHaveBeenCalledWith(false, "Still waiting"));
  });

  it("shows where a record is and its confirmation, in text", () => {
    wrap(<><VoiceProgress record={record()} /><VoiceFacts record={record()} display={display} settings={settings} incidentSettings={null} staff /></>);
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuetext", "Step 3 of 4: Resolved");
    expect(screen.getByText("Waiting for the customer's confirmation")).toBeInTheDocument();
    expect(screen.getByText("Delay in delivering the service")).toBeInTheDocument();
    expect(screen.getByText("Medium")).toBeInTheDocument();
  });

  it("saves the settings with the happiness scale", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(settings));
    wrap(<VoiceSettingsForm settings={settings} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("radio", { name: "Five points" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const body = JSON.parse(fetchMock.mock.calls[0]![1]!.body as string);
    expect(body.happiness.points).toBe(5);
    expect(body.happiness.labels).toBeUndefined();
    expect(body.anonymousEnabled).toBe(true);
  });
});
