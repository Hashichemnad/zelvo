import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { display, incident, settings } from "@/components/incidents/fixtures";
import type { SlaClock } from "@/lib/sla";
import { builtInViews, filtersFrom, filtersQuery, sameFilters, type CannedResponse, type Comment } from "@/lib/workspace";
import { BulkBar } from "./BulkBar";
import { CannedResponsesPanel } from "./CannedResponsesPanel";
import { Composer, composeEvent, ConversationList } from "./Conversation";
import { SlaCountdown } from "./SlaCountdown";
import { ViewControls } from "./ViewControls";
import { WorkspaceShell } from "./WorkspaceShell";

const push = vi.fn();
const replace = vi.fn();
const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push, replace, refresh }),
  Link: ({ href, children, scroll: _scroll, ...rest }: { href: string; children: React.ReactNode; scroll?: boolean }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const live = { handlers: {} as Record<string, (...args: unknown[]) => void> };
vi.mock("@/lib/live", () => ({
  useLive: (_csrf: string | null, handlers: Record<string, (...args: unknown[]) => void>) => {
    live.handlers = handlers;
    return "live";
  },
}));

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
  replace.mockReset();
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

const comments: Comment[] = [
  { id: "c1", ticketId: incident.id, visibility: "Public", body: "الطابعة لا تعمل", language: "ar", author: { id: "r", displayName: "Mariam" }, byStaff: false, mentions: [], cannedResponseId: null, createdAt: "2026-11-24T08:00:00Z" },
  { id: "c2", ticketId: incident.id, visibility: "Internal", body: "Driver issue", language: "en", author: { id: "a", displayName: "Hamad" }, byStaff: true, mentions: [{ id: "l", displayName: "Lead" }], cannedResponseId: null, createdAt: "2026-11-24T08:05:00Z" },
];

const response: CannedResponse = {
  id: "resp-1", scope: "Tenant", group: null, title: { en: "Greeting", ar: "تحية" }, body: { en: "Hello {{requester.name}}", ar: null, missing: ["ar"] },
  active: true, canEdit: true, updatedAt: "2026-11-24T08:00:00Z",
};

describe("workspace helpers", () => {
  it("read only known filters from the address and build the list query", () => {
    expect(filtersFrom({ open: "true", priority: "2", assignee: "me", sla: "atRisk", drop: "table", state: "Nope", group: "a;b" }))
      .toEqual({ open: true, priority: 2, assignee: "me", sla: "atRisk" });
    expect(filtersQuery({ open: true, sort: "due" }, 2)).toBe("open=true&sort=due&page=2&pageSize=25");
    expect(sameFilters({ open: true, sort: "due" }, { sort: "due", open: true })).toBe(true);
    expect(builtInViews.map((v) => v.key)).toContain("groupQueue");
  });
});

describe("ConversationList", () => {
  it("marks internal notes and each message's language for staff", () => {
    withIntl(<ConversationList comments={comments} display={display} staff />);
    const [reply, note] = screen.getAllByTestId("comment");
    expect(within(reply!).getByText("From the requester")).toBeInTheDocument();
    expect(within(reply!).getByText("الطابعة لا تعمل")).toHaveAttribute("lang", "ar");
    expect(within(note!).getByText("Internal note")).toBeInTheDocument();
    expect(within(note!).getByText("Mentioned: Lead")).toBeInTheDocument();
  });
});

describe("Composer", () => {
  it("sends an internal note with mentions and a filled-in canned response", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(ok({ id: "resp-1", language: "en", fallback: false, text: "Hello Mariam" }))
      .mockResolvedValueOnce(ok(comments[1], 201));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<Composer incidentId={incident.id} csrfToken="csrf-1" staff requesterLanguage="ar" responses={[response]} people={[{ id: "l", displayName: "Lead" }]} />);

    expect(screen.getByText("The requester's language: Arabic")).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("Internal note (staff only)"));
    fireEvent.change(screen.getByLabelText("Canned response"), { target: { value: "resp-1" } });
    await waitFor(() => expect(screen.getByTestId("composer-body")).toHaveValue("Hello Mariam"));
    fireEvent.change(screen.getByLabelText("Mention"), { target: { value: "l" } });
    expect(screen.getByTestId("composer-body")).toHaveValue("Hello Mariam @Lead ");
    fireEvent.click(screen.getByRole("button", { name: "Add note" }));

    await waitFor(() => expect(refresh).toHaveBeenCalled());
    const [renderUrl, renderInit] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(renderUrl).toBe(`/bff/api/v1/incidents/${incident.id}/canned-responses/resp-1/render`);
    expect(JSON.parse(renderInit.body as string)).toEqual({ language: "en" });
    const [url, init] = fetchMock.mock.calls[1] as [string, RequestInit];
    expect(url).toBe(`/bff/api/v1/incidents/${incident.id}/comments`);
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-1");
    expect(JSON.parse(init.body as string)).toEqual({
      body: "Hello Mariam @Lead ", visibility: "Internal", language: "en", mentionIds: ["l"], cannedResponseId: "resp-1",
    });
    expect(screen.getByTestId("composer-body")).toHaveValue("");
  });

  it("starts a reply or a note from the keyboard shortcut's event", () => {
    withIntl(<Composer incidentId={incident.id} csrfToken="c" staff responses={[]} people={[]} />);
    act(() => {
      window.dispatchEvent(new CustomEvent(composeEvent, { detail: "Internal" }));
    });
    expect(screen.getByLabelText("Internal note (staff only)")).toBeChecked();
    expect(screen.getByTestId("composer-body")).toHaveFocus();
  });

  it("tells why a reply was refused, in Arabic too", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ status: 409, code: "ticket-closed" }), { status: 409 })));
    withIntl(<Composer incidentId={incident.id} csrfToken="c" staff={false} responses={[]} people={[]} />, "ar");
    fireEvent.change(screen.getByTestId("composer-body"), { target: { value: "شكراً" } });
    fireEvent.click(screen.getByRole("button", { name: "إرسال الرد" }));
    expect(await screen.findByText("البلاغ مغلق ولا يقبل ردوداً إضافية.")).toBeInTheDocument();
    expect(screen.queryByLabelText("ملاحظة داخلية (للموظفين فقط)")).not.toBeInTheDocument();
  });
});

describe("SlaCountdown", () => {
  const clock = (overrides: Partial<SlaClock>): SlaClock => ({
    metric: "Resolution", status: "Running", state: "Running", targetMinutes: 480, elapsedSeconds: 0, percent: 10,
    dueAt: "2026-11-24T12:00:00Z", breachedAt: null, stoppedAt: null, policy: null, calendar: null, ...overrides,
  });
  const now = new Date("2026-11-24T10:30:00Z").getTime();

  it("counts down to the due time and turns breached when it passes", () => {
    const { rerender } = withIntl(<SlaCountdown clock={clock({})} display={display} now={now} />);
    expect(screen.getByText("Resolution: 1 h 30 min left")).toBeInTheDocument();
    rerender(
      <NextIntlClientProvider locale="en" messages={en} timeZone="Asia/Dubai">
        <SlaCountdown clock={clock({ status: "AtRisk" })} display={display} now={new Date("2026-11-24T12:20:00Z").getTime()} />
      </NextIntlClientProvider>,
    );
    expect(screen.getByText("Resolution: overdue by 20 min")).toBeInTheDocument();
    expect(screen.getByText("Resolution: overdue by 20 min").closest("[data-sla-status]")).toHaveAttribute("data-sla-status", "Breached");
  });

  it("shows paused clocks without a countdown, with Arabic-Indic digits when chosen", () => {
    withIntl(<SlaCountdown clock={clock({ state: "Paused", status: "Paused" })} display={display} now={now} />);
    expect(screen.getByText("Resolution: paused")).toBeInTheDocument();
    withIntl(<SlaCountdown clock={clock({ metric: "Response" })} display={{ ...display, locale: "ar", digits: "arab" }} now={now} />, "ar");
    expect(screen.getByText(/١ س ٣٠ د/)).toBeInTheDocument();
  });
});

describe("BulkBar", () => {
  it("applies one action to the selection and lists the incidents that stayed as they were", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({
      succeeded: 1, failed: 1,
      results: [{ id: "a", ok: true, status: 200, code: null, title: null }, { id: "b", ok: false, status: 409, code: "ticket-closed", title: "closed" }],
    }));
    vi.stubGlobal("fetch", fetchMock);
    const onDone = vi.fn();
    withIntl(<BulkBar selected={["a", "b"]} numbers={{ a: "INC-000001", b: "INC-000002" }} csrfToken="c" permissions={["incidents.edit"]}
      settings={settings} assignees={null} groups={[]} categories={[]} locations={[]} onDone={onDone} onClear={() => {}} />);

    expect(screen.getByText("2 incidents selected")).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Assign to an agent" })).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Action"), { target: { value: "Transition" } });
    fireEvent.change(screen.getByLabelText("New state"), { target: { value: "Resolved" } });
    fireEvent.change(screen.getByLabelText("Resolution notes"), { target: { value: "Mail server restarted" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(await screen.findByText("1 changed, 1 left as they were.")).toBeInTheDocument();
    expect(screen.getByText(/closed incidents do not change/)).toBeInTheDocument();
    expect(onDone).toHaveBeenCalled();
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/incidents/bulk");
    expect(JSON.parse(init.body as string)).toEqual({ ids: ["a", "b"], action: "Transition", state: "Resolved", onHoldReason: null, resolutionNotes: "Mail server restarted" });
  });
});

describe("ViewControls", () => {
  it("switches between built-in and saved views", () => {
    const onChange = vi.fn();
    withIntl(<ViewControls filters={builtInViews[0]!.filters} csrfToken="c" permissions={[]} settings={settings} groups={[]} onChange={onChange} onViewsChanged={() => {}}
      views={[{ id: "v1", scope: "Group", group: null, name: { en: "Network queue", ar: null }, filters: { group: "g1" }, canEdit: false }]} />);
    expect(screen.getByTestId("view-picker")).toHaveValue("myOpen");
    fireEvent.change(screen.getByTestId("view-picker"), { target: { value: "v1" } });
    expect(onChange).toHaveBeenCalledWith({ group: "g1" });
    fireEvent.click(screen.getByRole("button", { name: "Save as a view" }));
    expect(screen.queryByRole("option", { name: "Everyone" })).not.toBeInTheDocument();
  });
});

describe("CannedResponsesPanel", () => {
  it("saves English and Arabic side by side and reports a missing language", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(ok(response, 201)).mockResolvedValueOnce(ok([response]));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<CannedResponsesPanel initial={[response]} groups={[]} permissions={["incidents.edit"]} csrfToken="c" />);
    expect(screen.getByText(/missing: Arabic/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "New response" }));
    const form = screen.getByRole("form", { name: "New canned response" });
    expect(within(form).queryByRole("option", { name: "Everyone" })).not.toBeInTheDocument();
    const [titleEn, titleAr] = within(form).getAllByLabelText("Title");
    fireEvent.change(titleEn!, { target: { value: "Thanks" } });
    fireEvent.change(titleAr!, { target: { value: "شكر" } });
    fireEvent.change(within(form).getAllByLabelText("Wording")[1]!, { target: { value: "شكراً لكم {{requester.name}}" } });
    fireEvent.click(within(form).getByRole("button", { name: "Save" }));

    expect(await screen.findByText("The response is saved.")).toBeInTheDocument();
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({
      scope: "Personal", groupId: null, title: { en: "Thanks", ar: "شكر" }, body: { en: null, ar: "شكراً لكم {{requester.name}}" }, active: true,
    });
  });
});

describe("WorkspaceShell", () => {
  const page = { items: [incident, { ...incident, id: "0195f000-0000-7000-8000-000000000002", number: "INC-000043", title: "Screen flickers" }], page: 1, pageSize: 25, totalCount: 2 };
  const shell = (selectedId: string | null) => (
    <WorkspaceShell page={page} filters={builtInViews[0]!.filters} views={[]} selectedId={selectedId} settings={settings} groups={[]} categories={[]} locations={[]}
      assignees={null} permissions={["incidents.read", "incidents.edit"]} csrfToken="c" display={display} detail={<p>detail</p>} aside={<p>aside</p>} />
  );

  it("moves through the list with j and k, selects with x and shows the shortcuts with ?", () => {
    withIntl(shell(incident.id));
    expect(screen.getByTestId("live-status")).toHaveTextContent("Live updates on");
    fireEvent.keyDown(window, { key: "j" });
    expect(push).toHaveBeenCalledWith("/workspace/0195f000-0000-7000-8000-000000000002?open=true&assignee=me&sort=due&page=1", { scroll: false });
    fireEvent.keyDown(window, { key: "x" });
    expect(screen.getByTestId("bulk-bar")).toBeInTheDocument();
    fireEvent.keyDown(window, { key: "?" });
    expect(screen.getByText("Write an internal note")).toBeInTheDocument();
  });

  it("ignores shortcuts while typing and loads changes announced live", async () => {
    vi.useFakeTimers();
    try {
      withIntl(<>{shell(incident.id)}<input aria-label="search" /></>);
      const input = screen.getByLabelText("search");
      fireEvent.keyDown(input, { key: "j" });
      expect(push).not.toHaveBeenCalled();
      act(() => {
        live.handlers.ticketChanged?.({ ticketId: incident.id });
        live.handlers.ticketChanged?.({ ticketId: "other" });
        vi.advanceTimersByTime(2000);
      });
      expect(refresh).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });
});
