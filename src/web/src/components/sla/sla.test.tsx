import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { DateDisplayOptions } from "@/lib/format";
import { formatDuration, isTime, toMinutes, type BusinessCalendar, type CalendarTemplate, type SlaClock, type SlaPolicy } from "@/lib/sla";
import { CalendarsPanel } from "./CalendarsPanel";
import { CustomerGroupsPanel } from "./CustomerGroupsPanel";
import { SlaPoliciesPanel, type SlaChoices } from "./SlaPoliciesPanel";
import { SlaCell, SlaClocks, mostUrgent } from "./SlaClocks";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

afterEach(() => vi.unstubAllGlobals());

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const bodyOf = (call: unknown) => JSON.parse(((call as [string, RequestInit])[1].body as string) ?? "null");
const urlOf = (call: unknown) => (call as [string, RequestInit])[0];
const methodOf = (call: unknown) => (call as [string, RequestInit])[1]?.method ?? "GET";

const display = (locale: "en" | "ar" = "en", digits: "latn" | "arab" = "latn"): DateDisplayOptions =>
  ({ locale, calendar: "gregory", digits, timeZone: "Asia/Dubai" });

const government: CalendarTemplate = {
  key: "uae-government",
  periods: [
    { day: "Monday", start: "07:30", end: "15:30" }, { day: "Tuesday", start: "07:30", end: "15:30" },
    { day: "Wednesday", start: "07:30", end: "15:30" }, { day: "Thursday", start: "07:30", end: "15:30" },
    { day: "Friday", start: "07:30", end: "12:00" },
  ],
};
const allDay: CalendarTemplate = { key: "all-day", periods: (["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"] as const).map((day) => ({ day, start: "00:00", end: "24:00" })) };

const calendar: BusinessCalendar = {
  id: "cal1",
  name: { en: "Government hours", ar: "ساعات العمل الحكومية" },
  timeZone: "Asia/Dubai",
  isDefault: true,
  periods: government.periods,
  holidays: [{ id: "h1", startDate: "2026-12-02", endDate: "2026-12-03", name: { en: "National Day", ar: "عيد الاتحاد" } }],
  groupCount: 1,
  policyCount: 2,
  updatedAt: "2026-11-24T08:00:00Z",
  version: 1,
};

const clock = (overrides: Partial<SlaClock>): SlaClock => ({
  metric: "Response",
  status: "Running",
  state: "Running",
  targetMinutes: 60,
  elapsedSeconds: 1800,
  percent: 50,
  dueAt: "2026-11-24T10:00:00Z",
  breachedAt: null,
  stoppedAt: null,
  policy: { id: "p1", name: { en: "Standard", ar: "قياسي" } },
  calendar: { id: "cal1", name: calendar.name },
  ...overrides,
});

describe("SLA helpers", () => {
  it("turns hours and minutes into business minutes and formats them", () => {
    expect(toMinutes("1", "30")).toBe(90);
    expect(toMinutes("", "")).toBeNull();
    expect(toMinutes("0", "0")).toBeNull();
    expect(formatDuration(90, "en", "latn")).toBe("1 hr 30 min");
    expect(formatDuration(1440, "ar", "arab")).toContain("٢٤");
  });

  it("checks HH:mm times up to 24:00", () => {
    expect(isTime("07:30")).toBe(true);
    expect(isTime("24:00")).toBe(true);
    expect(isTime("24:30")).toBe(false);
    expect(isTime("7:30")).toBe(false);
  });

  it("puts breached clocks first and leaves met ones out of lists", () => {
    const met = clock({ metric: "Response", status: "Met", state: "Stopped" });
    const breached = clock({ metric: "Resolution", status: "Breached" });
    expect(mostUrgent([met, breached])?.metric).toBe("Resolution");
    expect(mostUrgent([met])).toBeNull();
  });
});

describe("SlaClocks", () => {
  it("shows each target with its status, time used, due time and calendar", () => {
    withIntl(<SlaClocks clocks={[clock({}), clock({ metric: "Resolution", status: "AtRisk", targetMinutes: 240, elapsedSeconds: 11400, percent: 79 })]} display={display()} />);
    const section = screen.getByTestId("sla-clocks");
    expect(within(section).getByText("Response: On track")).toBeInTheDocument();
    expect(within(section).getByText("Resolution: At risk")).toBeInTheDocument();
    expect(within(section).getAllByRole("progressbar")[1]).toHaveAttribute("aria-valuenow", "79");
    expect(within(section).getAllByText("Business hours: Government hours")).toHaveLength(2);
    expect(within(section).getAllByText(/^Due /)).toHaveLength(2);
  });

  it("says so when no policy applies, in Arabic with Arabic-Indic digits", () => {
    withIntl(<SlaClocks clocks={[clock({ status: "None", targetMinutes: null })]} display={display("ar", "arab")} />, "ar");
    expect(screen.getByText("لا تنطبق أي سياسة لمستوى الخدمة على هذا البلاغ.")).toBeInTheDocument();
  });

  it("shows the breach time and the badge in a list cell", () => {
    withIntl(<SlaCell clocks={[clock({ status: "Breached", breachedAt: "2026-11-24T09:00:00Z" })]} display={display()} />);
    expect(screen.getByText("Response: Breached")).toBeInTheDocument();
    expect(screen.queryByText(/^Due /)).toBeNull();
  });
});

describe("CalendarsPanel", () => {
  it("lists working hours and holidays and adds a holiday", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({ ...calendar, holidays: [...calendar.holidays, { id: "h2", startDate: "2027-01-01", endDate: "2027-01-01", name: { en: "New Year", ar: null } }] }, 201));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<CalendarsPanel initial={[calendar]} templates={[government, allDay]} csrfToken="csrf" canManage display={display()} />);
    expect(screen.getByText("Default")).toBeInTheDocument();
    expect(screen.getByText("07:30–12:00")).toBeInTheDocument();
    expect(screen.getByText("National Day")).toBeInTheDocument();

    const form = screen.getByRole("form", { name: "Add a holiday" });
    fireEvent.change(within(form).getByLabelText("In English"), { target: { value: "New Year" } });
    fireEvent.change(within(form).getByLabelText("First day"), { target: { value: "2027-01-01" } });
    fireEvent.click(within(form).getByRole("button", { name: "Add a holiday" }));
    await waitFor(() => expect(screen.getByText("New Year")).toBeInTheDocument());
    expect(urlOf(fetchMock.mock.calls[0])).toBe("/bff/api/v1/sla/calendars/cal1/holidays");
    expect(bodyOf(fetchMock.mock.calls[0])).toEqual({ startDate: "2027-01-01", endDate: null, name: { en: "New Year", ar: null } });
  });

  it("starts a new calendar from a template and sends the week Monday first", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(ok(calendar, 201)).mockResolvedValueOnce(ok([calendar]));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<CalendarsPanel initial={[]} templates={[government, allDay]} csrfToken="csrf" canManage display={display()} />);
    expect(screen.getByText(/No business calendars yet/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add a calendar" }));
    const form = screen.getByTestId("calendar-form");
    fireEvent.change(within(form).getAllByLabelText("In English")[0]!, { target: { value: "Night shift" } });
    fireEvent.click(within(form).getByRole("button", { name: "Around the clock" }));
    fireEvent.click(within(form).getByRole("button", { name: "Save calendar" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    const body = bodyOf(fetchMock.mock.calls[0]);
    expect(body.timeZone).toBe("Asia/Dubai");
    expect(body.periods).toHaveLength(7);
    expect(body.periods[0]).toEqual({ day: "Monday", start: "00:00", end: "24:00" });
  });

  it("is read-only without sla.manage", () => {
    withIntl(<CalendarsPanel initial={[calendar]} templates={[government]} csrfToken="csrf" canManage={false} display={display()} />);
    expect(screen.queryByRole("button", { name: "Add a calendar" })).toBeNull();
    expect(screen.queryByRole("form", { name: "Add a holiday" })).toBeNull();
  });

  it("names the days in Arabic", () => {
    withIntl(<CalendarsPanel initial={[calendar]} templates={[government]} csrfToken="csrf" canManage={false} display={display("ar")} />, "ar");
    expect(screen.getByText("الجمعة")).toBeInTheDocument();
    expect(screen.getByText("عيد الاتحاد")).toBeInTheDocument();
  });
});

describe("CustomerGroupsPanel", () => {
  it("adds a group with email domains", async () => {
    const created = { id: "g1", name: { en: "VIP", ar: null }, active: true, emailDomains: ["moi.gov.ae"], members: [], policyCount: 0, updatedAt: "", version: 1 };
    const fetchMock = vi.fn().mockResolvedValueOnce(ok(created, 201)).mockResolvedValueOnce(ok([created]));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<CustomerGroupsPanel initial={[]} csrfToken="csrf" canManage />);
    fireEvent.click(screen.getByRole("button", { name: "Add a customer group" }));
    const form = screen.getByTestId("customer-group-form");
    fireEvent.change(within(form).getAllByLabelText("In English")[0]!, { target: { value: "VIP" } });
    fireEvent.change(within(form).getByLabelText("Email domains"), { target: { value: "moi.gov.ae, ajman.ae" } });
    fireEvent.click(within(form).getByRole("button", { name: "Save customer group" }));
    await waitFor(() => expect(screen.getByText("Email domains: moi.gov.ae")).toBeInTheDocument());
    expect(bodyOf(fetchMock.mock.calls[0]).emailDomains).toEqual(["moi.gov.ae", "ajman.ae"]);
  });
});

describe("SlaPoliciesPanel", () => {
  const priorities = [1, 2, 3, 4, 5].map((level) => ({ level, name: { en: `P${level}`, ar: `P${level}` } }));
  const choices: SlaChoices = { categories: [], customerGroups: [], calendars: [calendar], groups: [], priorities };
  const policy: SlaPolicy = {
    id: "p1",
    name: { en: "Standard", ar: "قياسي" },
    position: 0,
    active: true,
    recordType: null,
    category: null,
    customerGroup: null,
    calendar: null,
    targets: [{ priority: 1, responseMinutes: 30, resolutionMinutes: 240 }],
    escalations: [{ id: "e1", metric: null, percent: 75, notifyAssignee: true, notifyGroupLeads: true, notifyGroup: null }],
    updatedAt: "",
    version: 1,
  };

  it("summarises policies and warns when none covers every incident", () => {
    withIntl(<SlaPoliciesPanel initial={[{ ...policy, customerGroup: { id: "g1", name: { en: "VIP", ar: null } } }]} choices={choices} csrfToken="csrf" canManage display={display()} />);
    expect(screen.getByText("customer group VIP")).toBeInTheDocument();
    expect(screen.getByText(/respond 30 min, resolve 4 hr/)).toBeInTheDocument();
    expect(screen.getByRole("note")).toHaveTextContent("No active policy without conditions");
  });

  it("sends targets in minutes with the default escalations", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(ok(policy, 201)).mockResolvedValueOnce(ok([policy]));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<SlaPoliciesPanel initial={[]} choices={choices} csrfToken="csrf" canManage display={display()} />);
    fireEvent.click(screen.getByRole("button", { name: "Add a policy" }));
    const form = screen.getByTestId("policy-form");
    fireEvent.change(within(form).getAllByLabelText("In English")[0]!, { target: { value: "Standard" } });
    fireEvent.change(within(form).getByLabelText("Response hours for P1"), { target: { value: "1" } });
    fireEvent.change(within(form).getByLabelText("Resolution hours for P1"), { target: { value: "4" } });
    fireEvent.change(within(form).getByLabelText("Resolution minutes for P1"), { target: { value: "30" } });
    fireEvent.click(within(form).getByRole("button", { name: "Save policy" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(methodOf(fetchMock.mock.calls[0])).toBe("POST");
    const body = bodyOf(fetchMock.mock.calls[0]);
    expect(body.targets).toEqual([{ priority: 1, responseMinutes: 60, resolutionMinutes: 270 }]);
    expect(body.escalations.map((e: { percent: number }) => e.percent)).toEqual([75, 100]);
    expect(body.recordType).toBe("Incident");
  });

  it("shows what a trial incident would get", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok({
      policy: { id: "p1", name: policy.name }, calendar: { id: "cal1", name: calendar.name }, customerGroups: [],
      response: { minutes: 30, dueAt: "2026-11-24T08:00:00Z" }, resolution: null,
    })));
    withIntl(<SlaPoliciesPanel initial={[policy]} choices={choices} csrfToken="csrf" canManage={false} display={display()} />);
    expect(screen.queryByRole("button", { name: "Add a policy" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Try" }));
    await waitFor(() => expect(screen.getByTestId("sla-test-result")).toHaveTextContent("Policy Standard, business hours: Government hours."));
    expect(screen.getByTestId("sla-test-result")).toHaveTextContent(/Response within 30 min, due/);
  });
});
