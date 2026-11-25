import { render, screen } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { ReportFilters } from "./ReportFilters";

const groups = [
  { id: "g1", name: { en: "Service desk", ar: "مكتب الخدمة" } },
  { id: "g2", name: { en: "Network team", ar: "فريق الشبكات" } },
];

function renderFilters(locale: "en" | "ar") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <ReportFilters values={{ from: "2026-11-01", to: "", types: ["Complaint"], groups: ["g2"], interval: "Week" }} types={["Incident", "Complaint"]} groups={groups} />
    </NextIntlClientProvider>,
  );
}

describe("ReportFilters", () => {
  it("is a GET form with labelled fields that keeps the current filter", () => {
    renderFilters("en");
    const form = screen.getByRole("form", { name: "Filter the report" });
    expect(form).toHaveAttribute("method", "get");
    expect(screen.getByLabelText("From")).toHaveValue("2026-11-01");
    expect(screen.getByLabelText("Show by")).toHaveValue("Week");
    expect(screen.getByRole("checkbox", { name: "Complaints" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Incidents" })).not.toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Network team" })).toBeChecked();
    expect(screen.getByRole("button", { name: "Show" })).toHaveAttribute("type", "submit");
  });

  it("is in Arabic with the groups' Arabic names", () => {
    renderFilters("ar");
    expect(screen.getByRole("form", { name: "تصفية التقرير" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "مكتب الخدمة" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "الشكاوى" })).toBeChecked();
  });
});
