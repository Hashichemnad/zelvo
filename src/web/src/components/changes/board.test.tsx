import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { AgendaItem, Meeting } from "@/lib/changes";
import { ChangeBoardHistory, MeetingView } from "./ChangeBoard";

vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
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

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;
const now = new Date().toISOString();

function item(patch: Partial<AgendaItem> = {}): AgendaItem {
  return {
    id: "item1", position: 0, changeId: "c1", changeNumber: "CHG-000009", changeTitle: "Move billing", kind: "Normal", stage: "Authorisation", riskLevel: "High",
    plannedStart: now, plannedEnd: now, conflictCount: 1, waiting: true,
    votes: [{ member: { id: "u2", displayName: "Mona" }, choice: "Reject", comment: "Month end", votedAt: now }],
    tally: { Approve: 0, Reject: 1, Abstain: 0 }, decision: null, comment: null, minutes: null, decidedBy: null, decidedAt: null, ...patch,
  };
}

function meeting(patch: Partial<Meeting> = {}): Meeting {
  return {
    id: "m1", title: { en: "Weekly board", ar: "الاجتماع الأسبوعي" }, startsAt: now, endsAt: now, location: "Room 3", chair: { id: "u1", displayName: "Omar" },
    state: "Planned", minutes: null, members: [{ id: "u1", displayName: "Omar" }, { id: "u2", displayName: "Mona" }], agenda: [item()],
    isChair: true, isMember: true, mayManage: true, etag: "\"1\"", createdAt: now, updatedAt: now, ...patch,
  };
}

describe("board meeting", () => {
  it("shows the votes as advice and lets the chair record a decision with minutes", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok(meeting({ agenda: [item({ decision: "Approved", decidedBy: { id: "u1", displayName: "Omar" }, minutes: "Run after month end." })] })));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<MeetingView initial={meeting()} csrfToken="csrf" display={display} />);
    expect(screen.getByText(/0 approve · 1 reject · 0 abstain/)).toBeInTheDocument();
    expect(screen.getByText("Month end")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Minutes", { selector: "[data-testid=decision-form] textarea" }), { target: { value: "Run after month end." } });
    fireEvent.click(screen.getByRole("button", { name: "Record the decision" }));
    await waitFor(() => expect(screen.getByText(/Approved by the board/)).toBeInTheDocument());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toContain("/v1/cab-meetings/m1/agenda/item1/decision");
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({ decision: "Approved", comment: null, minutes: "Run after month end." });
  });

  it("needs a reason to reject, and members who do not chair only vote", () => {
    withIntl(<MeetingView initial={meeting({ isChair: false, mayManage: false })} csrfToken="csrf" display={display} />);
    expect(screen.queryByRole("button", { name: "Record the decision" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save my vote" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Update the agenda" })).not.toBeInTheDocument();
  });

  it("disables rejecting without a reason, in Arabic", () => {
    withIntl(<MeetingView initial={meeting()} csrfToken="csrf" display={{ ...display, locale: "ar" }} />, "ar");
    fireEvent.click(within(screen.getByTestId("decision-form")).getByRole("radio", { name: "رفض" }));
    expect(screen.getByRole("button", { name: "تسجيل القرار" })).toBeDisabled();
  });
});

describe("board history on a change", () => {
  it("lists the meetings with votes and the decision", () => {
    withIntl(<ChangeBoardHistory display={display} entries={[{ meetingId: "m1", meetingTitle: { en: "Weekly board", ar: null }, meetingStartsAt: now, meetingState: "Held", item: item({ decision: "Deferred" }) }]} />);
    expect(screen.getByRole("link", { name: "Weekly board" })).toHaveAttribute("href", "/changes/board/m1");
    expect(screen.getByText("Deferred to a later meeting")).toBeInTheDocument();
  });
});
