import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { MyWellbeing, WellbeingAgent, WellbeingGroupDetail, WellbeingGroupSummary } from "@/lib/wellbeing";
import { MyWorkload, WellbeingDashboard, WellbeingSettingsPanel } from "./Wellbeing";

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

function json(body: unknown, status = 200) {
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const thresholds = { maxWeightedLoad: 40, maxNewPerDay: 12, maxAfterHoursHours: 5, maxDifficultPercent: 50 };
const totals = { agents: 2, openAssigned: 13, weightedLoad: 52, newPerAgentPerDay: 6.5, afterHoursHours: 9, difficultPercent: 40 };
const agent = (patch: Partial<WellbeingAgent>): WellbeingAgent => ({
  id: "u1", name: "Amna", available: true, newPerDayThisWeek: 2, alerts: [], days: [],
  figures: { openAssigned: 2, weightedLoad: 6, newPerDay: 2, afterHoursHours: 0, afterHoursActions: 0, handledTickets: 4, difficultTickets: 0, difficultPercent: 0 },
  ...patch,
});
const busy = agent({
  id: "u2", name: "Zayed",
  figures: { openAssigned: 11, weightedLoad: 46, newPerDay: 15, afterHoursHours: 9, afterHoursActions: 30, handledTickets: 20, difficultTickets: 8, difficultPercent: 40 },
  alerts: [{ id: "a1", groupId: "g1", kind: "Workload", value: 46, threshold: 40, raisedAt: "2026-11-27T05:00:00Z" }],
});
const groups: WellbeingGroupSummary[] = [
  { id: "g1", name: { en: "Service desk", ar: "مكتب الخدمة" }, led: true, thresholds, totals, agentsWithAlerts: 1 },
  { id: "g2", name: { en: "Network", ar: "الشبكة" }, led: false, thresholds, totals, agentsWithAlerts: 0 },
];
const detail = (patch: Partial<WellbeingGroupDetail> = {}): WellbeingGroupDetail => ({ ...groups[0]!, days: 7, agents: [agent({}), busy], ...patch });
const display = { locale: "en", digits: "latn" as const };

afterEach(() => vi.restoreAllMocks());

describe("WellbeingDashboard", () => {
  it("shows the led group's agents by name with alerts, and moves a ticket only when the team lead chooses", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(json(detail()))
      .mockResolvedValueOnce(json([{ ticketId: "t1", number: "INC-000001", title: "Printer", type: "Incident", state: "Assigned", priority: 3,
        candidates: [{ id: "u1", name: "Amna", weightedLoad: 6 }] }]))
      .mockResolvedValueOnce(json({ ticketId: "t1", number: "INC-000001", assigneeId: "u1" }))
      .mockResolvedValue(json(detail()));
    wrap(<WellbeingDashboard groups={groups} csrfToken="csrf" display={display} />);
    expect(screen.getByText(/not for ranking or scoring people/)).toBeInTheDocument();
    const list = await screen.findByTestId("wellbeing-agents");
    expect(within(list).getAllByRole("heading").map((h) => h.textContent)).toEqual(["Amna", "Zayed"]);
    expect(within(list).getByTestId("wellbeing-alerts")).toHaveTextContent("Open work weighted 46, above the group's 40");
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/wellbeing/groups/g1?days=7");

    fireEvent.click(screen.getByRole("button", { name: "Suggest reassignment for Zayed" }));
    fireEvent.click(await screen.findByRole("button", { name: "Give INC-000001 to Amna" }));
    expect(await screen.findByText("INC-000001 was given to Amna.")).toBeInTheDocument();
    const [url, init] = fetchMock.mock.calls[2]!;
    expect(url).toBe("/bff/api/v1/wellbeing/reassign");
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({ groupId: "g1", ticketId: "t1", toUserId: "u1" });
  });

  it("shows only totals for groups the person does not lead, in Arabic", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(json(detail({ id: "g2", name: groups[1]!.name, led: false, agents: null })));
    wrap(<WellbeingDashboard groups={[groups[1]!]} csrfToken="csrf" display={{ locale: "ar", digits: "arab" }} />, "ar");
    const group = await screen.findByTestId("wellbeing-group");
    expect(group).toHaveTextContent("تطّلعون على إجماليات هذه المجموعة فقط");
    expect(within(group).getByTestId("wellbeing-totals")).toHaveTextContent("٥٢");
    expect(screen.queryByTestId("wellbeing-agents")).toBeNull();
  });
});

describe("MyWorkload", () => {
  it("shows the agent's own figures and alerts, and says when nothing is recorded", () => {
    const mine: MyWellbeing = { enabled: true, enabledAt: "2026-11-20T08:00:00Z", days: 7, figures: busy, groups: [{ id: "g1", name: groups[0]!.name, thresholds }] };
    const { unmount } = wrap(<MyWorkload mine={mine} display={display} />);
    expect(screen.getByTestId("my-workload")).toHaveTextContent("11 tickets, weighted 46");
    expect(screen.getByTestId("wellbeing-alerts")).toHaveTextContent("above the group's 40");
    expect(screen.getByText(/Nothing about your presence or typing/)).toBeInTheDocument();
    unmount();
    wrap(<MyWorkload mine={{ enabled: false, enabledAt: null, days: 7, figures: null, groups: [] }} display={display} />, "ar");
    expect(screen.getByTestId("my-workload-off")).toHaveTextContent("غير مفعّلة");
  });
});

describe("WellbeingSettingsPanel", () => {
  it("turns wellbeing on only after the administrator confirms staff were told", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ enabled: true, enabledAt: "2026-11-27T08:00:00Z", staffInformed: true, defaults: thresholds }));
    wrap(<WellbeingSettingsPanel initial={{ enabled: false, enabledAt: null, staffInformed: false, defaults: thresholds }} csrfToken="csrf" />);
    const turnOn = screen.getByRole("button", { name: "Turn wellbeing on and notify agents" });
    expect(turnOn).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: /I confirm that staff were told/ }));
    fireEvent.click(turnOn);
    await waitFor(() => expect(screen.getByTestId("wellbeing-settings-status")).toHaveTextContent("Every agent is being notified."));
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/wellbeing/settings");
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({ enabled: true, staffInformed: true, defaults: thresholds });
    expect(screen.getByRole("button", { name: "Turn wellbeing off and remove the figures" })).toBeInTheDocument();
  });
});
