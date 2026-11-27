import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { DateDisplayOptions } from "@/lib/format";
import type { ApprovalDetail, ApprovalProcessView, WorkflowRule } from "@/lib/workflows";
import { ApprovalDecisionForm, ApprovalProcessTimeline } from "./Approvals";
import { RuleDiagram } from "./RuleDiagram";
import { WorkflowRulesPanel } from "./WorkflowRulesPanel";

const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/approvals",
  useRouter: () => ({ push: vi.fn(), refresh }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const choices = {
  services: [{ id: "s1", code: "IT-LAPTOP", name: { en: "New laptop", ar: "حاسوب جديد" } }],
  categories: [], locations: [], departments: [], roles: [],
  groups: [{ id: "g1", name: { en: "Desk", ar: "المكتب" }, description: null, active: true, memberCount: 1, leadCount: 1 }],
  customerGroups: [{ id: "v1", name: { en: "VIP", ar: "كبار المتعاملين" }, active: true }],
};

const story = "When a VIP raises a critical incident, notify the IT manager on WhatsApp";
const vipDraft = (patch: Partial<WorkflowRule> = {}) => rule({
  id: "r9", name: { en: "VIP critical incidents", ar: "البلاغات الحرجة لكبار المتعاملين" }, recordType: "Incident",
  conditions: [{ field: "requesterCustomerGroup", op: "in", value: ["v1"] }, { field: "priority", op: "eq", value: 1 }],
  actions: [{ type: "notify", recipients: ["groupLeads:g1"], subject: { en: "VIP P1", ar: "بلاغ حرج" }, body: { en: "x", ar: "س" }, email: false, channels: ["WhatsApp"] }],
  draft: true, sourceText: story, sourceLanguage: "en", unmapped: ["Only during working hours"], etag: "\"3\"", ...patch,
});

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const rule = (patch: Partial<WorkflowRule> = {}): WorkflowRule => ({
  id: "r1", name: { en: "Laptops need approval", ar: "اعتماد الحواسيب" }, description: { en: null, ar: null }, position: 0, active: true,
  trigger: "TicketCreated", recordType: "ServiceRequest", match: "All",
  conditions: [{ field: "service", op: "in", value: ["s1"] }],
  actions: [{ type: "approval", stage: "BeforeAssignment", levels: [{ name: { en: "Manager", ar: "المدير" }, mode: "Any", approvers: [{ kind: "RequesterManager" }], conditions: [] }] }],
  stopProcessing: false, etag: "\"7\"", createdAt: "2026-11-24T08:00:00Z", updatedAt: "2026-11-24T08:00:00Z",
  draft: false, sourceText: null, sourceLanguage: null, unmapped: [], approvedAt: null, ...patch,
});

const process: ApprovalProcessView = {
  id: "p1", stage: "BeforeAssignment", state: "Pending", currentLevel: 1, reason: null, createdAt: "2026-11-24T08:00:00Z", endedAt: null,
  levels: [
    { index: 0, name: { en: "Manager approval", ar: "اعتماد المدير" }, mode: "Any", state: "Approved", fallback: false,
      decisions: [{ id: "d0", approver: { id: "u0", displayName: "Noura" }, state: "Approved", comment: "Fine.", decidedAt: "2026-11-24T09:00:00Z" }] },
    { index: 1, name: { en: "Budget", ar: "الميزانية" }, mode: "All", state: "Waiting", fallback: false,
      decisions: [{ id: "d1", approver: { id: "u1", displayName: "Saeed" }, state: "Pending", comment: null, decidedAt: null }] },
  ],
};

const detail = (patch: Partial<ApprovalDetail> = {}): ApprovalDetail => ({
  approval: {
    id: "d1", processId: "p1", stage: "BeforeAssignment", level: 1, levelName: { en: "Budget", ar: "الميزانية" }, state: "Pending", comment: null,
    createdAt: "2026-11-24T09:00:00Z", decidedAt: null, approver: { id: "u1", displayName: "Saeed" },
    ticket: { id: "t1", number: "REQ-000001", title: "New laptop", type: "ServiceRequest", state: "New", requester: { id: "u2", displayName: "Mariam" },
      service: { id: "s1", code: "IT-LAPTOP", name: { en: "New laptop", ar: "حاسوب جديد" } }, createdAt: "2026-11-24T08:00:00Z" },
  },
  description: null, form: null, process, mayDecide: true, ...patch,
});

const display: DateDisplayOptions = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" };

afterEach(() => {
  vi.restoreAllMocks();
  refresh.mockReset();
});

describe("WorkflowRulesPanel", () => {
  it("lists rules in order and moves one with the keyboard-reachable buttons", async () => {
    const second = rule({ id: "r2", name: { en: "Tell the desk", ar: "إبلاغ المكتب" }, actions: [{ type: "notify", recipients: ["group"], subject: { en: "Hi", ar: "مرحباً" }, body: { en: "x", ar: "س" }, email: true }] });
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json([second, rule()]));
    wrap(<WorkflowRulesPanel initialRules={[rule(), second]} choices={choices} csrfToken="csrf" />);
    const list = screen.getByTestId("workflow-rules");
    expect(within(list).getAllByRole("listitem")[0]).toHaveTextContent("Laptops need approval");
    fireEvent.click(screen.getByRole("button", { name: "Move Tell the desk up" }));
    await waitFor(() => expect(within(screen.getByTestId("workflow-rules")).getAllByRole("listitem")[0]).toHaveTextContent("Tell the desk"));
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/workflow-rules/order");
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({ ids: ["r2", "r1"] });
  });

  it("builds a rule and shows the problems the API lists", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ code: "validation-failed", problems: [{ field: "actions.0.recipients", code: "recipients-required", message: "x" }] }, 400));
    wrap(<WorkflowRulesPanel initialRules={[]} choices={choices} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "Add a rule" }));
    const form = screen.getByTestId("workflow-rule-form");
    fireEvent.change(within(form).getAllByLabelText("In English")[0]!, { target: { value: "Tell people" } });
    // Service requests submitted: approvals can be asked for.
    const action = within(form).getByLabelText("Action 1");
    expect(within(action).getByRole("option", { name: "Ask for approval" })).toBeInTheDocument();
    fireEvent.click(within(form).getByRole("button", { name: "Add a condition" }));
    expect(within(form).getByLabelText("Field of condition 1")).toHaveValue("service");
    fireEvent.click(within(form).getByRole("button", { name: "Save the rule" }));
    expect(await screen.findByTestId("rule-problems")).toHaveTextContent("Choose who to tell.");
  });

  it("offers no approval action for incidents and works in Arabic", () => {
    wrap(<WorkflowRulesPanel initialRules={[]} choices={choices} csrfToken="csrf" />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "إضافة قاعدة" }));
    fireEvent.change(screen.getByLabelText("على"), { target: { value: "Incident" } });
    expect(within(screen.getByLabelText("الإجراء 1")).queryByRole("option", { name: "طلب اعتماد" })).toBeNull();
  });
});

describe("Plain-language rules (task K1)", () => {
  it("builds a draft from a description, shows it as a diagram with what was left out, and approves it", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json(vipDraft(), 201))
      .mockResolvedValueOnce(json(vipDraft({ draft: false, approvedAt: "2026-11-27T08:00:00Z", etag: "\"4\"" })));
    wrap(<WorkflowRulesPanel initialRules={[]} choices={choices} csrfToken="csrf" plainLanguage={{ available: true, reason: null }} />);
    fireEvent.change(screen.getByLabelText("What should the rule do?"), { target: { value: story } });
    fireEvent.click(screen.getByRole("button", { name: "Build the rule" }));

    const drafts = await screen.findByTestId("rule-drafts");
    expect(drafts).toHaveTextContent(story);
    const diagram = within(drafts).getByTestId("rule-diagram");
    expect(diagram).toHaveTextContent("Requester's customer group (for example VIP) is one of VIP");
    expect(diagram).toHaveTextContent("Priority (1 is highest) equals 1");
    expect(diagram).toHaveTextContent("Tell Team leads of Desk: in the app, WhatsApp");
    expect(within(drafts).getByTestId("rule-unmapped")).toHaveTextContent("Only during working hours");
    expect(screen.getByTestId("workflow-rules")).toHaveTextContent("Draft, not running");
    expect(JSON.parse(String((fetchMock.mock.calls[0]![1] as RequestInit).body))).toEqual({ text: story });

    fireEvent.click(within(drafts).getByRole("button", { name: "Approve and turn on" }));
    expect(await screen.findByText("The rule is approved and now runs.")).toBeInTheDocument();
    const [url, init] = fetchMock.mock.calls[1]!;
    expect(url).toBe("/bff/api/v1/workflow-rules/r9/approve");
    expect((init as RequestInit).headers).toMatchObject({ "If-Match": "\"3\"" });
    expect(screen.queryByTestId("rule-drafts")).toBeNull();
  });

  it("lists what the AI could not use when no rule could be built, and says when the feature is off", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ code: "rule-not-understood", unmapped: ["Action \"sendFlowers\": build it in the builder."] }, 422));
    const { unmount } = wrap(<WorkflowRulesPanel initialRules={[]} choices={choices} csrfToken="csrf" plainLanguage={{ available: true, reason: null }} />);
    fireEvent.change(screen.getByLabelText("What should the rule do?"), { target: { value: "Send flowers" } });
    fireEvent.click(screen.getByRole("button", { name: "Build the rule" }));
    expect(await screen.findByTestId("plain-language-error")).toHaveTextContent("sendFlowers");
    unmount();

    wrap(<WorkflowRulesPanel initialRules={[]} choices={choices} csrfToken="csrf" plainLanguage={{ available: false, reason: "ai-off" }} />, "ar");
    expect(screen.getByTestId("plain-language-rule")).toHaveTextContent("ميزة القواعد باللغة الطبيعية غير مفعّلة");
    expect(screen.queryByRole("button", { name: "إعداد القاعدة" })).toBeNull();
  });

  it("draws every rule as when, if, then in Arabic", () => {
    wrap(<RuleDiagram id="d" rule={vipDraft()} choices={choices} />, "ar");
    const boxes = within(screen.getByTestId("rule-diagram")).getAllByRole("heading");
    expect(boxes.map((b) => b.textContent)).toEqual(["عندما", "إذا", "عندئذٍ"].map((w) => expect.stringContaining(w) as unknown as string));
    expect(screen.getByTestId("rule-diagram")).toHaveTextContent("كبار المتعاملين");
    expect(screen.getByTestId("rule-diagram")).toHaveTextContent("واتساب");
  });
});

describe("Approvals", () => {
  it("shows each level with its state and decisions", () => {
    wrap(<ApprovalProcessTimeline process={process} display={display} />);
    const levels = screen.getAllByTestId("approval-level-view");
    expect(levels[0]).toHaveTextContent("Manager approval");
    expect(levels[0]).toHaveTextContent("approved");
    expect(levels[1]).toHaveTextContent("waiting");
    expect(levels[1]).toHaveTextContent("Saeed: Waiting");
  });

  it("opens with the email's choice made, needs a reason to reject and records the decision", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json(detail({ mayDecide: false })));
    wrap(<ApprovalDecisionForm detail={detail()} initialChoice="Reject" fromEmail csrfToken="csrf" />);
    expect(screen.getByRole("radio", { name: "Reject" })).toBeChecked();
    expect(screen.getByRole("button", { name: "Confirm my decision" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Reason (required)"), { target: { value: "No budget left." } });
    fireEvent.click(screen.getByRole("button", { name: "Confirm my decision" }));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/approvals/d1/decision");
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({ decision: "Reject", comment: "No budget left.", via: "email" });
  });

  it("says when a decision is no longer the approver's to make", () => {
    wrap(<ApprovalDecisionForm detail={detail({ mayDecide: false, approval: { ...detail().approval, state: "NotNeeded" } })} initialChoice={null} fromEmail={false} csrfToken="csrf" />, "ar");
    expect(screen.getByRole("status")).toHaveTextContent("لم يعد قراركم مطلوباً.");
  });
});
