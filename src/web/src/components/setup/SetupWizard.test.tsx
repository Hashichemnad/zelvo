import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { DepartmentTemplate, SetupState } from "@/lib/reports";
import { SetupWizard } from "./SetupWizard";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const keys = ["organisation", "language", "calendar", "branding", "signIn", "departments", "users", "channels", "ai"] as const;
const state = (done: string[] = []): SetupState => ({
  steps: keys.map((key) => ({ key, status: done.includes(key) ? "Done" : "Pending", configured: key === "signIn" ? null : key === "organisation" })),
  completedAt: null, finished: done.length, total: keys.length, applied: [],
});
const templates: DepartmentTemplate[] = [{
  key: "it-service-desk", name: { en: "IT service desk", ar: "مكتب خدمة تقنية المعلومات" }, description: { en: "Hardware and accounts", ar: "الأجهزة والحسابات" }, codePrefix: "IT",
  groups: [{ key: "service-desk", name: { en: "IT service desk", ar: "مكتب خدمة تقنية المعلومات" } }],
  categories: [{ name: { en: "Hardware", ar: "الأجهزة" }, group: "service-desk", children: [{ en: "Printers", ar: "الطابعات" }] }],
  policies: [{ recordType: "Incident", targets: [{ priority: 1, responseMinutes: 30, resolutionMinutes: 240 }] }],
  services: [{ template: "new-laptop", code: "IT-LAPTOP", name: { en: "New laptop", ar: "حاسوب محمول جديد" } }],
  responses: [{ title: { en: "We are looking into it", ar: "نعمل على معالجة طلبكم" }, body: { en: "Thanks.", ar: "شكرًا." } }],
}];

function renderWizard(locale: "en" | "ar" = "en", initial = state()) {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <SetupWizard initial={initial} templates={templates} tenantName={{ en: "City Council", ar: null }} canRename csrfToken="csrf-2" digits="latn" />
    </NextIntlClientProvider>,
  );
}

afterEach(() => vi.unstubAllGlobals());

describe("SetupWizard", () => {
  it("shows the steps with their progress and the first step to do", () => {
    renderWizard();
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuetext", "0 of 9 steps finished");
    expect(screen.getAllByRole("listitem")).toHaveLength(9);
    expect(screen.getByRole("heading", { level: 2, name: "Organisation" })).toBeInTheDocument();
    expect(screen.getByTestId("setup-check")).toHaveTextContent("This already looks set up.");
    expect(screen.getByLabelText("English")).toHaveValue("City Council");
  });

  it("marks a step done and moves to the next one", async () => {
    const next = state(["organisation"]);
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(next), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderWizard();
    fireEvent.click(screen.getByRole("button", { name: "Mark as done" }));
    await waitFor(() => expect(screen.getByRole("heading", { level: 2, name: "Language and region" })).toBeInTheDocument());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/setup/steps/organisation");
    expect(JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)).toEqual({ status: "Done" });
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "1");
    expect(screen.getByRole("link", { name: "Open language and region" })).toHaveAttribute("href", "/en/settings/language");
  });

  it("previews a department template and applies it with changed names", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ template: "it-service-desk", departmentId: "d", groupIds: ["g"], rootCategoryId: "c", categories: 1, policies: 1, catalogId: null, serviceIds: ["s"], responses: 1 }), { status: 201 }))
      .mockResolvedValue(new Response(JSON.stringify(state(["departments"])), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    renderWizard();
    fireEvent.click(screen.getByRole("button", { name: /Departments and service desks/ }));
    fireEvent.click(screen.getByRole("radio", { name: "IT service desk" }));
    expect(screen.getByTestId("template-preview")).toHaveTextContent("Hardware");
    expect(screen.getByRole("table", { name: "Incidents" })).toHaveTextContent("0.5 h");
    fireEvent.change(screen.getAllByLabelText("English")[0]!, { target: { value: "Information technology" } });
    fireEvent.click(screen.getByLabelText("Create canned responses"));
    fireEvent.click(screen.getByRole("button", { name: "Create the department" }));
    expect(await screen.findByTestId("template-result")).toHaveTextContent("1 groups");
    const body = JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string);
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/setup/templates/it-service-desk/apply");
    expect(body).toMatchObject({ departmentName: { en: "Information technology", ar: "مكتب خدمة تقنية المعلومات" }, includeServices: true, includeResponses: false });
  });

  it("is in Arabic", () => {
    renderWizard("ar");
    expect(screen.getByRole("heading", { level: 2, name: "الجهة" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "تحديد كمنجزة" })).toBeInTheDocument();
  });
});
