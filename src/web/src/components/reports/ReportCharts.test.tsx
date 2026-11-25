import { render, screen, within } from "@testing-library/react";
import { BarList, KpiTile, TrendChart } from "./ReportCharts";

describe("BarList", () => {
  it("writes every value in a captioned table and draws bars relative to the largest", () => {
    const { container } = render(
      <BarList caption="By channel" labelHeading="Name" valueHeading="Tickets" empty="Nothing"
        rows={[{ key: "Email", label: "Email", value: { value: 4, text: "4" } }, { key: "Chat", label: "Chat", value: { value: 2, text: "2" } }]} />,
    );
    const table = screen.getByRole("table", { name: "By channel" });
    expect(within(table).getByRole("rowheader", { name: "Email" })).toBeInTheDocument();
    expect(within(table).getAllByRole("cell").map((c) => c.textContent)).toContain("2");
    const bars = container.querySelectorAll("[aria-hidden='true'] > span");
    expect((bars[0] as HTMLElement).style.width).toBe("100%");
    expect((bars[1] as HTMLElement).style.width).toBe("50%");
  });

  it("says so when there is nothing", () => {
    render(<BarList caption="By group" labelHeading="Name" valueHeading="Tickets" empty="Nothing to show" rows={[]} />);
    expect(screen.getByText("Nothing to show")).toBeInTheDocument();
    expect(screen.queryByRole("table")).toBeNull();
  });
});

describe("TrendChart", () => {
  it("hides the drawing from screen readers and lists each bucket in a table", () => {
    const { container } = render(
      <TrendChart caption="Targets met" bucketHeading="Period" empty="Nothing" max={100} target={{ value: 95, text: "Target 95 %" }}
        buckets={["1 Nov", "2 Nov"]}
        series={[{ key: "r", name: "Response", colour: "bg-brand", values: [{ value: 90, text: "90 %" }, { value: null, text: "No data" }] }]} />,
    );
    expect(container.querySelector("[aria-hidden='true']")).not.toBeNull();
    const table = screen.getByRole("table", { name: "Targets met" });
    expect(within(table).getByRole("rowheader", { name: "2 Nov" })).toBeInTheDocument();
    expect(within(table).getByText("No data")).toBeInTheDocument();
    expect(screen.getAllByText("Target 95 %").length).toBeGreaterThan(0);
  });

  it("shows the empty message when every value is zero", () => {
    render(<TrendChart caption="Created" bucketHeading="Period" empty="Nothing in this period" buckets={["a"]} series={[{ key: "c", name: "Created", colour: "bg-brand", values: [{ value: 0, text: "0" }] }]} />);
    expect(screen.getByText("Nothing in this period")).toBeInTheDocument();
  });
});

describe("KpiTile", () => {
  it("marks a figure that misses its target", () => {
    render(<dl><KpiTile label="Response targets met" value="80 %" note="Target 95 %" state="bad" testId="kpi" /></dl>);
    expect(screen.getByTestId("kpi")).toHaveClass("border-bad");
    expect(screen.getByText("80 %")).toBeInTheDocument();
  });
});
