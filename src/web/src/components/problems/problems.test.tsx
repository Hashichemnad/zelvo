import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { composeInsertEvent, type KnownError, type MatchingKnownError, type Problem } from "@/lib/problems";
import { IncidentKnownErrors } from "./IncidentKnownErrors";
import { ProblemAnalysisEditor } from "./ProblemAnalysisEditor";
import { ProblemTimeline } from "./ProblemNotes";
import { ProblemWorkPanel } from "./ProblemWorkPanel";

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

function knownError(): KnownError {
  return {
    id: id(), number: "KE-000001", problemId: id(), problemNumber: "PRB-000001",
    title: { en: "Portal times out", ar: "انتهاء مهلة البوابة" }, symptoms: { en: null, ar: null },
    workaround: { en: "Restart the pool.", ar: "أعيدوا تشغيل المجمّع." }, status: "Active",
    articleId: null, articleNumber: null, articleState: null, etag: "\"1\"", createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
  };
}

function problem(change: Partial<Problem> = {}): Problem {
  return {
    id: id(), number: "PRB-000001", title: "Portal keeps going down", description: null, stage: "Assessment", state: "InProgress", phase: "Assessment",
    priority: 2, category: null, group: null, assignee: null, createdBy: null, rootCause: null, fiveWhys: null, fishbone: null, knownError: null,
    incidentCount: 2, openIncidentCount: 2, onHoldReason: null, resolutionNotes: null, nextTargets: ["RootCauseAnalysis", "OnHold", "Resolved"], sla: [],
    etag: "\"7\"", createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), resolvedAt: null, closedAt: null, ...change,
  };
}

describe("problem work panel", () => {
  it("offers the API's moves and resolves the open incidents with the notes", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    const p = problem();
    withIntl(<ProblemWorkPanel problem={p} reasons={[]} groups={[]} canAssign={false} csrfToken="csrf" />);

    expect(screen.getByRole("button", { name: "Analyse the root cause" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Close" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Resolve" }));
    fireEvent.change(screen.getByLabelText("How was it fixed?"), { target: { value: "Pool enlarged." } });
    expect(screen.getByRole("checkbox", { name: /Also resolve its 2 open incidents/ })).toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toContain(`/v1/problems/${p.id}/transitions`);
    expect(JSON.parse(String(init.body))).toEqual({ to: "Resolved", resolutionNotes: "Pool enlarged.", resolveIncidents: true });
    expect(new Headers(init.headers).get("If-Match")).toBe("\"7\"");
    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });

  it("names the stages in Arabic", () => {
    withIntl(<ProblemWorkPanel problem={problem()} reasons={[]} groups={[]} canAssign={false} csrfToken="csrf" />, "ar");
    expect(screen.getByRole("button", { name: "تحليل السبب الجذري" })).toBeInTheDocument();
  });
});

describe("root-cause analysis", () => {
  it("saves the 5 Whys and the fishbone under the template's categories and lists the causes by category", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    const categories = [{ key: "technology", name: { en: "Technology", ar: "التقنية" }, active: true }];
    withIntl(<ProblemAnalysisEditor problemId="p1" etag={"\"3\""} fiveWhys={{ statement: null, steps: [], conclusion: null }}
      fishbone={{ effect: null, causes: [] }} categories={categories} canEdit csrfToken="csrf" />);

    fireEvent.click(screen.getByRole("button", { name: "Add a step" }));
    fireEvent.change(screen.getByLabelText("Why?"), { target: { value: "Why is it slow?" } });
    fireEvent.change(screen.getByLabelText("Because…"), { target: { value: "The pool is full." } });
    fireEvent.click(screen.getByRole("button", { name: "Add a cause" }));
    fireEvent.change(screen.getByLabelText("Cause"), { target: { value: "Pool too small" } });
    fireEvent.click(screen.getByRole("checkbox", { name: "Root cause" }));
    expect(screen.getByTestId("fishbone-list")).toHaveTextContent("Technology");
    fireEvent.click(screen.getByRole("button", { name: "Save the analysis" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const body = JSON.parse(String(fetchMock.mock.calls[0]![1].body));
    expect(body.fiveWhys.steps).toEqual([{ why: "Why is it slow?", because: "The pool is full." }]);
    expect(body.fishbone.causes).toEqual([{ category: "technology", text: "Pool too small", root: true }]);
  });
});

describe("known errors on an incident", () => {
  it("pastes the workaround into the workspace's reply in the language chosen", async () => {
    const match: MatchingKnownError = { knownError: knownError(), reasons: ["problem", "category"] };
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok([match])));
    const inserted: unknown[] = [];
    const listener = (e: Event) => inserted.push((e as CustomEvent).detail);
    window.addEventListener(composeInsertEvent, listener);
    withIntl(<IncidentKnownErrors incidentId="i1" canInsert canSeeProblems={false} canRaise={false} />);

    expect(await screen.findByText("Restart the pool.")).toBeInTheDocument();
    expect(screen.getByText("Matches: linked problem, same category")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Insert into reply (Arabic)" }));
    expect(inserted).toEqual([{ incidentId: "i1", text: "أعيدوا تشغيل المجمّع.", language: "ar" }]);
    window.removeEventListener(composeInsertEvent, listener);
  });

  it("says so when nothing matches, and links raising a problem from the incident", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok([])));
    withIntl(<IncidentKnownErrors incidentId="i2" canInsert={false} canSeeProblems={false} canRaise />, "ar");
    expect(await screen.findByText("لا يوجد خطأ معروف يطابق هذا البلاغ.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "إنشاء مشكلة من هذا البلاغ" })).toHaveAttribute("href", "/problems/new?incidents=i2");
  });
});

describe("problem timeline", () => {
  it("names each entry in both languages", () => {
    const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;
    const at = new Date().toISOString();
    const entries = [
      { at, kind: "incident.linked" as const, ticket: { id: "i1", number: "INC-000001", type: "Incident", title: "Portal down" }, reference: null },
      { at, kind: "problem.known-error" as const, ticket: { id: "p1", number: "PRB-000001", type: "Problem", title: "Portal" }, reference: "KE-000001" },
    ];
    const { unmount } = withIntl(<ProblemTimeline entries={entries} display={display} />);
    expect(screen.getByRole("link", { name: "Incident INC-000001 linked" })).toHaveAttribute("href", "/incidents/i1");
    expect(screen.getByText("Known error KE-000001 recorded")).toBeInTheDocument();
    unmount();
    withIntl(<ProblemTimeline entries={entries} display={{ ...display, locale: "ar" }} />, "ar");
    expect(screen.getByText("تسجيل الخطأ المعروف KE-000001")).toBeInTheDocument();
  });
});
