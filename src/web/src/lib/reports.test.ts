import { bucketLabel, canSeeReports, dashboardOf, filterQuery, formatDay, readFilter, seesDashboards } from "./reports";

describe("report filters", () => {
  it("read valid values from the address and leave out the rest", () => {
    const id = "01a21118-49ca-72eb-8a3a-111513905e25";
    const filter = readFilter({ from: "2026-11-01", to: "not a date", types: ["Incident", "Nope", "Complaint,Inquiry"], groups: [id, "x"], interval: "Week" });
    expect(filter).toEqual({ from: "2026-11-01", to: "", types: ["Incident", "Complaint", "Inquiry"], groups: [id], interval: "Week" });
    expect(filterQuery(filter)).toBe(`from=2026-11-01&types=Incident%2CComplaint%2CInquiry&groups=${id}&interval=Week`);
    expect(filterQuery(readFilter({}))).toBe("");
  });

  it("know the dashboards by their path", () => {
    expect(dashboardOf("sla")?.kind).toBe("Sla");
    expect(dashboardOf("schedules")).toBeNull();
  });
});

describe("report display", () => {
  it("shows days in the reader's calendar and digits", () => {
    expect(formatDay("2026-11-25", { locale: "en", calendar: "gregory", digits: "latn" })).toBe("25 November 2026");
    expect(formatDay("2026-11-25", { locale: "ar", calendar: "gregory", digits: "arab" })).toContain("٢٥");
    expect(formatDay("2026-11-25", { locale: "ar", calendar: "islamic-umalqura", digits: "latn" })).toMatch(/1448/);
    expect(bucketLabel("2026-11-01", "Month", { locale: "en", calendar: "gregory", digits: "latn" })).toBe("Nov 2026");
  });

  it("offers reports to staff and dashboards to leads and executives", () => {
    expect(canSeeReports([])).toBe(false);
    expect(canSeeReports(["incidents.read"])).toBe(true);
    expect(seesDashboards(["incidents.read"])).toBe(false);
    expect(seesDashboards(["incidents.read", "reports.read"])).toBe(true);
    expect(seesDashboards(["reports.all"])).toBe(false);
  });
});
