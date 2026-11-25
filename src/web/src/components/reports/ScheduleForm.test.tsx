import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { newSchedule, ScheduleForm } from "./ScheduleForm";

const push = vi.fn();
vi.mock("@/i18n/navigation", () => ({ useRouter: () => ({ push, refresh: vi.fn() }) }));

const me = { id: "u1", name: "Lena Lead" };

function renderForm(canChooseRecipients: boolean, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <ScheduleForm initial={newSchedule(me, "Sla", ["Incident"])} types={["Incident", "Complaint"]} groups={[]} canChooseRecipients={canChooseRecipients} csrfToken="csrf-1" />
    </NextIntlClientProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
});

describe("ScheduleForm", () => {
  it("asks for a name, then creates a weekly schedule for the person themselves", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: "s1" }), { status: 201 }));
    vi.stubGlobal("fetch", fetchMock);
    renderForm(false);

    expect(screen.getByText("The report is sent to you.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Find people")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Schedule the report" }));
    expect(screen.getByRole("alert")).toHaveTextContent("Name the scheduled report.");
    expect(fetchMock).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Weekly SLA" } });
    expect(screen.getByLabelText("Day of the week")).toHaveValue("1");
    fireEvent.change(screen.getByLabelText("How often"), { target: { value: "Monthly" } });
    expect(screen.getByText("Each report covers the month before.")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Day of the month"), { target: { value: "40" } });
    expect(screen.getByLabelText("Day of the month")).toHaveValue(28);
    fireEvent.click(screen.getByRole("button", { name: "Schedule the report" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/reports/schedules");
    expect((init as RequestInit).headers).toMatchObject({ "X-CSRF-Token": "csrf-1" });
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({
      name: "Weekly SLA", report: "Sla", frequency: "Monthly", dayOfWeek: null, dayOfMonth: 28, time: "07:00", types: ["Incident"], groups: [], active: true,
    });
    await waitFor(() => expect(push).toHaveBeenCalledWith("/reports/schedules"));
  });

  it("lets reports managers find and add recipients", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify([{ id: "u2", name: "Eva Executive" }]), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderForm(true);

    fireEvent.change(screen.getByLabelText("Find people"), { target: { value: "Eva" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    fireEvent.click(await screen.findByRole("button", { name: "Add Eva Executive" }));
    expect(screen.getByRole("button", { name: "Remove Eva Executive" })).toBeInTheDocument();
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/reports/schedules/recipients?search=Eva");
  });

  it("is in Arabic", () => {
    renderForm(false, "ar");
    expect(screen.getByLabelText("الاسم")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "جدولة التقرير" })).toBeInTheDocument();
  });
});
