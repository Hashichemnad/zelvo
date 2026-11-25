import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { useEffect, useState, type AnchorHTMLAttributes, type ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { bundleStages, completeCard, type BundleItem, type ServiceKind, type ServiceOffer, type ServiceProactive, type ServiceRequest } from "@/lib/catalog";
import type { DateDisplayOptions } from "@/lib/format";
import type { ResolvedForm } from "@/lib/forms/types";
import { BundleEditor, ProactiveEditor } from "./BundleProactiveEditor";
import { sharedFields } from "./BundleRequestForm";
import { BundleParts } from "./ServiceRequests";
import { ServiceOffers } from "./ServiceOffers";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/my-requests",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const choices = {
  services: [
    { id: "s1", code: "IT-LAPTOP", name: { en: "Laptop", ar: "حاسوب" } },
    { id: "s2", code: "IT-EMAIL", name: { en: "Email account", ar: "حساب بريد" } },
  ],
  categories: [], locations: [], departments: [], roles: [],
  groups: [{ id: "g1", name: { en: "Drivers", ar: "السائقون" }, description: null, active: true, memberCount: 2, leadCount: 0 }],
};
const display: DateDisplayOptions = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" };

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(<NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>{children}</NextIntlClientProvider>);
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

afterEach(() => vi.restoreAllMocks());

let latestBundle: { kind: ServiceKind; items: BundleItem[] } | null = null;
function Bundle() {
  const [value, setValue] = useState<{ kind: ServiceKind; items: BundleItem[] }>({ kind: "Service", items: [] });
  useEffect(() => { latestBundle = value; }, [value]);
  return <BundleEditor kind={value.kind} items={value.items} choices={choices} selfId="self" onChange={(kind, items) => setValue({ kind, items })} />;
}

let latestProactive: ServiceProactive | null = null;
function Proactive() {
  const [value, setValue] = useState(completeCard({}).proactive);
  useEffect(() => { latestProactive = value; }, [value]);
  return <ProactiveEditor value={value} choices={choices} serviceId="self" display={display} onChange={setValue} />;
}

describe("bundles", () => {
  it("works out the stage of each item as the API does", () => {
    expect(bundleStages([{ parallelWithPrevious: false }, { parallelWithPrevious: true }, { parallelWithPrevious: false }, { parallelWithPrevious: true }])).toEqual([0, 0, 1, 1]);
    expect(bundleStages([{ parallelWithPrevious: true }])).toEqual([0]);
  });

  it("makes a bundle of mandatory and optional services in stages", () => {
    wrap(<Bundle />);
    fireEvent.click(screen.getByLabelText("A bundle of services"));
    fireEvent.click(screen.getByRole("button", { name: "Add a service" }));
    fireEvent.click(screen.getByRole("button", { name: "Add a service" }));
    const items = screen.getAllByTestId("bundle-item");
    expect(within(items[0]!).queryByLabelText("Starts together with the service before")).toBeNull();
    fireEvent.change(within(items[0]!).getByLabelText("Service 1"), { target: { value: "s1" } });
    fireEvent.change(within(items[1]!).getByLabelText("Service 2"), { target: { value: "s2" } });
    fireEvent.click(within(items[1]!).getByLabelText("Mandatory"));
    expect(within(items[1]!).getByText("Starts in stage 2")).toBeInTheDocument();
    fireEvent.click(within(items[1]!).getByLabelText("Starts together with the service before"));
    expect(within(screen.getAllByTestId("bundle-item")[1]!).getByText("Starts in stage 1")).toBeInTheDocument();
    expect(latestBundle).toEqual({ kind: "Bundle", items: [
      { serviceId: "s1", required: true, parallelWithPrevious: false },
      { serviceId: "s2", required: false, parallelWithPrevious: true },
    ] });
  });

  it("asks a question several forms share once", () => {
    const form = (version: string, ...keys: string[]): ResolvedForm => ({
      form: { id: version, key: version, name: { en: version, ar: version }, description: { en: null, ar: null }, versionId: version, versionNumber: 1 },
      fields: keys.map((key) => ({ field: { key, type: "Text", label: { en: key, ar: key } } }) as ResolvedForm["fields"][number]),
      defaults: {}, references: {} as ResolvedForm["references"],
    });
    expect(sharedFields([form("own", "startDate"), form("laptop", "startDate", "model"), null, form("email", "model", "alias")])
      .map((fields) => fields.map((f) => f.field.key))).toEqual([["startDate"], ["model"], [], ["alias"]]);
  });

  it("shows each part of a bundle with its state, team and progress, in Arabic", () => {
    const part = { stage: 0, optional: false, held: false, awaitingApproval: false, stepCount: 3, currentStep: 1, title: "x" };
    const request = {
      isBundle: true,
      parts: [
        { ...part, id: "p1", number: "REQ-000002", state: "InProgress", service: { id: "s1", code: "IT-LAPTOP", name: { en: "Laptop", ar: "حاسوب" } }, group: { id: "g", name: { en: "IT", ar: "تقنية المعلومات" } } },
        { ...part, id: "p2", number: "REQ-000003", state: "New", held: true, stage: 1, optional: true, service: { id: "s2", code: "IT-EMAIL", name: { en: "Email account", ar: "حساب بريد" } }, group: null, stepCount: 0, currentStep: -1 },
      ],
    } as unknown as ServiceRequest;
    wrap(<BundleParts request={request} basePath="/my-requests" />, "ar");
    expect(screen.getByRole("heading", { name: "أجزاء الباقة" })).toBeInTheDocument();
    const rows = screen.getAllByTestId("bundle-part");
    expect(within(rows[0]!).getByText("تقنية المعلومات")).toBeInTheDocument();
    expect(within(rows[0]!).getByRole("progressbar")).toHaveAttribute("aria-valuetext", "الخطوة 2 من 3");
    expect(within(rows[1]!).getByText("بانتظار الأجزاء السابقة")).toBeInTheDocument();
    expect(within(rows[1]!).getByText("(اختياري)")).toBeInTheDocument();
  });
});

describe("proactive services", () => {
  it("sets up a date trigger and a schedule, and lists the runs", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(json({ items: [{
      id: "r1", service: null, trigger: "beforeExpiry", kind: "Date", person: { id: "u1", displayName: "Mariam" }, occurrence: "x", dueOn: "2026-11-25",
      state: "Offered", reason: null, request: null, source: null, offeredAt: null, expiresAt: null, endedAt: null, createdAt: "2026-11-25T08:00:00Z",
    }] }));
    wrap(<Proactive />);
    fireEvent.click(screen.getByLabelText("Offer or start this service without the person asking"));
    fireEvent.click(screen.getByLabelText(/Start the request automatically/));
    fireEvent.click(screen.getByRole("button", { name: "Add a trigger" }));
    fireEvent.change(screen.getByLabelText("Trigger 1"), { target: { value: "Date" } });
    fireEvent.change(screen.getByLabelText("Service with the date"), { target: { value: "s1" } });
    fireEvent.change(screen.getByLabelText("Date field of its form"), { target: { value: "expiryDate" } });
    fireEvent.change(screen.getByLabelText("Days from the date"), { target: { value: "-30" } });
    fireEvent.click(screen.getByRole("button", { name: "Add a trigger" }));
    fireEvent.change(screen.getByLabelText("Trigger 2"), { target: { value: "Schedule" } });
    fireEvent.click(screen.getByLabelText("Drivers"));
    expect(latestProactive).toMatchObject({
      enabled: true, mode: "AutoStart",
      triggers: [
        { kind: "Date", sourceServiceId: "s1", sourceFieldKey: "expiryDate", offsetDays: -30 },
        { kind: "Schedule", people: { groupIds: ["g1"] } },
      ],
    });
    await waitFor(() => expect(screen.getByTestId("proactive-run")).toHaveTextContent("Offered"));
  });

  it("lists the offers made to the person and declines one", async () => {
    const fetch = vi.spyOn(globalThis, "fetch").mockResolvedValue(json({}));
    const offer: ServiceOffer = {
      id: "o1", service: { id: "s9", code: "LIC-RENEW", name: { en: "Licence renewal", ar: "تجديد الرخصة" } }, summary: { en: "Renew it", ar: "جدّدها" },
      state: "Offered", offeredAt: "2026-11-25T08:00:00Z", expiresAt: "2026-12-05T08:00:00Z", kind: "Service", answers: null,
    };
    wrap(<ServiceOffers offers={[offer]} display={display} csrfToken="csrf" />);
    expect(screen.getByRole("link", { name: "Check and confirm" })).toHaveAttribute("href", "/en/catalog/s9/request?offer=o1");
    fireEvent.click(screen.getByRole("button", { name: "Decline" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("You declined the offer."));
    expect(String(fetch.mock.calls[0]![0])).toContain("/v1/me/offers/o1/decline");
    expect(screen.queryByTestId("service-offer")).toBeNull();
  });
});
