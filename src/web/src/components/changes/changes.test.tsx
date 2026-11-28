import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { assess, type Change, type ChangeSettings } from "@/lib/changes";
import { RiskQuestionnaire } from "./ChangePlan";
import { ChangeConflicts } from "./ChangeRelated";
import { ChangeWorkPanel } from "./ChangeWork";

const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), refresh }),
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a>,
}));

afterEach(() => {
  vi.unstubAllGlobals();
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
const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function change(patch: Partial<Change> = {}): Change {
  const now = new Date().toISOString();
  return {
    id: id(), number: "CHG-000001", title: "Enlarge the pool", description: null, kind: "Normal", stage: "New", state: "New", phase: "Planning",
    closure: null, template: null, riskAnswers: {}, riskScore: null, riskLevel: null, implementationPlan: "Do it", testPlan: null, backoutPlan: "Undo it",
    plannedStart: now, plannedEnd: now, actualStart: null, actualEnd: null, outcome: null, reviewNotes: null, reviewedBy: null, reviewedAt: null,
    category: null, group: null, assignee: null, createdBy: null, resolutionNotes: null, approval: null, taskCount: 0, openTaskCount: 0, itemCount: 0,
    nextActions: ["Submit", "Cancel"], etag: "\"3\"", createdAt: now, updatedAt: now, submittedAt: null, approvedAt: null, resolvedAt: null, closedAt: null,
    conflicts: [], ...patch,
  };
}

const settings: ChangeSettings = {
  questions: [
    { key: "reach", text: { en: "How many people?", ar: "كم عدد الأشخاص؟" }, active: true, options: [{ key: "few", text: { en: "Few", ar: "قليل" }, weight: 0 }, { key: "all", text: { en: "Everyone", ar: "الجميع" }, weight: 6 }] },
    { key: "test", text: { en: "Tested?", ar: "هل اختُبر؟" }, active: true, options: [{ key: "yes", text: { en: "Yes", ar: "نعم" }, weight: 0 }, { key: "no", text: { en: "No", ar: "لا" }, weight: 4 }] },
  ],
  mediumFrom: 5,
  highFrom: 10,
  paths: { low: [], medium: [], high: [], emergency: [] },
};

describe("risk scoring", () => {
  it("adds the chosen answers' weights and finds the level like the server", () => {
    expect(assess(settings, { reach: "few" })).toEqual({ score: 0, level: "Low", unanswered: 1 });
    expect(assess(settings, { reach: "all", test: "yes" })).toEqual({ score: 6, level: "Medium", unanswered: 0 });
    expect(assess(settings, { reach: "all", test: "no" }).level).toBe("High");
  });
});

describe("risk questionnaire", () => {
  it("shows the live score while answering and saves the answers with If-Match", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    const c = change();
    withIntl(<RiskQuestionnaire change={c} settings={settings} canEdit csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("radio", { name: "Everyone" }));
    fireEvent.click(screen.getByRole("radio", { name: "No" }));
    expect(screen.getByText(/Score 10: High risk/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save the answers" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toContain(`/v1/changes/${c.id}/risk`);
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({ answers: { reach: "all", test: "no" } });
    expect(new Headers((init as RequestInit).headers).get("If-Match")).toBe("\"3\"");
  });

  it("does not ask standard changes, in Arabic", () => {
    withIntl(<RiskQuestionnaire change={change({ kind: "Standard", riskLevel: "Low", riskScore: 0 })} settings={settings} canEdit csrfToken="csrf" />, "ar");
    expect(screen.getByText("التغييرات القياسية معتمدة مسبقًا ومنخفضة المخاطر، ولا تخضع للتقييم.")).toBeInTheDocument();
    expect(screen.getByText("منخفضة")).toBeInTheDocument();
  });
});

describe("change work panel", () => {
  it("submits, and asks for the reason before cancelling", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    const c = change({ conflicts: [{ kind: "blackout", start: new Date().toISOString(), end: new Date().toISOString(), items: [], changeId: null, changeNumber: null, changeTitle: null, changeStage: null, blackoutId: id(), blackoutName: { en: "Year end", ar: null } }] });
    withIntl(<ChangeWorkPanel change={c} groups={[]} canAssign={false} csrfToken="csrf" />);
    expect(screen.getByText(/1 conflict is listed below/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Cancel the change" }));
    const confirm = screen.getByRole("button", { name: "Confirm" });
    expect(confirm).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Why is the change cancelled?"), { target: { value: "Not needed" } });
    fireEvent.click(confirm);
    await waitFor(() => expect(refresh).toHaveBeenCalled());
    expect(JSON.parse(String((fetchMock.mock.calls[0]![1] as RequestInit).body))).toEqual({ to: "Cancel", notes: "Not needed" });

    fireEvent.click(screen.getByRole("button", { name: "Submit for approval" }));
    await waitFor(() => expect(String(fetchMock.mock.calls[1]![0])).toContain(`/v1/changes/${c.id}/submit`));
  });

  it("records the review with its outcome; notes are needed unless successful", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    const c = change({ stage: "Review", state: "Resolved", nextActions: ["Review"] });
    withIntl(<ChangeWorkPanel change={c} groups={[]} canAssign={false} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "Record the review" }));
    fireEvent.click(screen.getByRole("radio", { name: "Backed out" }));
    const close = screen.getByRole("button", { name: "Close with this review" });
    expect(close).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/What did the review find/), { target: { value: "Rolled back." } });
    fireEvent.click(close);
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(String(fetchMock.mock.calls[0]![0])).toContain("/review");
    expect(JSON.parse(String((fetchMock.mock.calls[0]![1] as RequestInit).body))).toEqual({ outcome: "BackedOut", notes: "Rolled back." });
  });
});

describe("change conflicts", () => {
  it("names the other change and the shared items", () => {
    const other = id();
    withIntl(<ChangeConflicts display={display} conflicts={[{
      kind: "change", start: new Date().toISOString(), end: new Date().toISOString(), items: [{ id: "ci1", number: "CI-000001", name: "db-01" }],
      changeId: other, changeNumber: "CHG-000002", changeTitle: "Patch", changeStage: "Scheduled", blackoutId: null, blackoutName: null,
    }]} />);
    expect(screen.getByText("Another change on the same items")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "CHG-000002" })).toHaveAttribute("href", `/changes/${other}`);
    expect(screen.getByRole("link", { name: /db-01/ })).toHaveAttribute("href", "/cmdb/ci1");
  });
});
