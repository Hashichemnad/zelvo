import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { rulesToText, textToRules, type CiDiscovery, type CiReview, type CiReviewPage, type DiscoverySource } from "@/lib/cmdb";
import { CiDiscoveryPanel } from "./CiDiscoveryPanel";
import { DiscoverySources } from "./DiscoverySources";
import { ReviewQueue } from "./ReviewQueue";

vi.mock("@/i18n/navigation", () => ({
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

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const id = () => crypto.randomUUID();
const display = (locale: "en" | "ar" = "en") => ({ locale, calendar: "gregory" as const, digits: "latn" as const, timeZone: "Asia/Dubai" });

const source = (over: Partial<DiscoverySource> = {}): DiscoverySource => ({
  id: id(), name: "vCenter", kind: "VCenter", connectorId: null, connectorName: null,
  settings: { url: "https://vcenter.entity.local", username: "zelvo-ro@vsphere.local", classMap: {} }, hasCredential: true, credentialSetAt: "2026-11-27T08:00:00Z",
  active: true, priority: 50, attributePriorities: {}, syncMinutes: 1440, nextSyncAt: null, lastSyncAt: "2026-11-27T08:00:00Z", lastSyncSucceeded: true,
  lastSyncStats: { received: 12, created: 3, updated: 2, unchanged: 7, linked: 1, review: 1, duplicates: 0, missing: 0, unknownClass: 0, skipped: 0, valuesRefused: 0, software: 0, relationships: 4 },
  lastError: null, recordCount: 12, missingCount: 0, reviewCount: 1, etag: "\"5\"", ...over,
});

describe("identification rules as text", () => {
  it("round-trips one rule per line with keys joined by +", () => {
    expect(rulesToText([["serialNumber"], ["hostName", "macAddress"]])).toBe("serialNumber\nhostName + macAddress");
    expect(textToRules("serialNumber\n\n hostName+ macAddress \n")).toEqual([["serialNumber"], ["hostName", "macAddress"]]);
  });
});

describe("DiscoverySources", () => {
  it("lists sources with their last sync and tests one without changing anything", async () => {
    const fetchMock = vi.fn(async () => ok({ ok: true, error: null, count: 2, classes: { server: 1, "virtual-machine": 1 }, sample: [{ id: "vm:1", class: "virtual-machine", name: "app-01", attributes: {} }] }));
    vi.stubGlobal("fetch", fetchMock);
    const s = source();
    withIntl(<DiscoverySources initial={[s]} connectors={[]} csrfToken="csrf" display={display()} />);
    expect(screen.getByText("3 new, 2 changed, 1 to review")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Test" }));
    expect(await screen.findByText("2 records. Nothing was changed.")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(`/bff/api/v1/cmdb/discovery/sources/${s.id}/test`, expect.objectContaining({ method: "POST" }));
  });

  it("adds an Intune source on an Entra ID connector, with the precedence and settings typed in", async () => {
    const connector = { id: id(), name: "Entra", kind: "EntraId" };
    const fetchMock = vi.fn(async (url: string) => (url.endsWith("/sources") && fetchMock.mock.calls.length > 1 ? ok([]) : ok({}, 201)));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<DiscoverySources initial={[]} connectors={[connector, { id: id(), name: "AD", kind: "Ldap" }]} csrfToken="csrf" display={display()} />);
    fireEvent.change(screen.getByLabelText("Kind of source"), { target: { value: "Intune" } });
    fireEvent.click(screen.getByRole("button", { name: "Add a source" }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Intune" } });
    // Only Entra ID connectors are offered for Intune.
    const select = screen.getByLabelText("Connector");
    expect(select.querySelectorAll("option")).toHaveLength(2);
    fireEvent.change(select, { target: { value: connector.id } });
    fireEvent.change(screen.getByLabelText("Precedence"), { target: { value: "30" } });
    fireEvent.change(screen.getByLabelText("Precedence per value"), { target: { value: "serialNumber=10" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the source" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/cmdb/discovery/sources");
    const body = JSON.parse(String(init.body));
    expect(body).toMatchObject({ name: "Intune", kind: "Intune", connectorId: connector.id, priority: 30, attributePriorities: { serialNumber: 10 }, settings: { includeApps: true } });
    expect(await screen.findByText("The source was saved.")).toBeInTheDocument();
  });

  it("asks for the vCenter password only as a write-only field, in Arabic", () => {
    withIntl(<DiscoverySources initial={[source()]} connectors={[]} csrfToken="csrf" display={display("ar")} />, "ar");
    fireEvent.click(screen.getByRole("button", { name: "تعديل" }));
    const password = screen.getByLabelText("كلمة المرور") as HTMLInputElement;
    expect(password.type).toBe("password");
    expect(password.value).toBe("");
    expect(screen.getByText(/محفوظة مشفّرة/)).toBeInTheDocument();
  });
});

describe("ReviewQueue", () => {
  const side = (number: string, name: string, attributes: Record<string, string>) => ({
    id: id(), number, name, status: "Active" as const, class: { id: id(), key: "laptop", family: "Hardware" as const, path: { en: "Hardware › Laptop", ar: "الأجهزة › حاسوب محمول" } },
    attributes, ticketCount: 1, sources: ["Intune"],
  });
  const review = (): CiReview => ({
    id: id(), kind: "PossibleDuplicate", state: "Open", matchedOn: "hostName", matchedValue: "lt-0042",
    items: [side("CI-000010", "Laptop A", { hostName: "LT-0042", serialNumber: "SN-42" }), side("CI-000011", "Laptop B", { hostName: "lt-0042" })],
    record: null, createdAt: "2026-11-27T08:00:00Z", decidedAt: null, decidedBy: null,
  });

  it("shows the values side by side and merges into the item kept", async () => {
    const r = review();
    const fetchMock = vi.fn(async (url: string) => (url.includes("/merge") ? ok({ ...r, state: "Merged" }) : ok({ items: [], page: 1, pageSize: 25, totalCount: 0 })));
    vi.stubGlobal("fetch", fetchMock);
    const page: CiReviewPage = { items: [r], page: 1, pageSize: 25, totalCount: 1 };
    withIntl(<ReviewQueue initial={page} csrfToken="csrf" display={display()} />);
    expect(screen.getByText("One decision waits.")).toBeInTheDocument();
    expect(screen.getByText("SN-42")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Merge, keeping CI-000010 Laptop A" }));
    expect(await screen.findByText("The items were merged.")).toBeInTheDocument();
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(`/bff/api/v1/cmdb/review/${r.id}/merge`);
    expect(JSON.parse(String(init.body))).toEqual({ keepId: r.items[0]!.id });
  });

  it("offers linking or a new item for a record that matched several items, in Arabic", () => {
    const r: CiReview = { ...review(), kind: "Ambiguous", record: { source: "Intune", externalId: "d-1", name: "LT-0042", class: { key: "computer", path: { en: "Computer", ar: "حاسوب" } }, attributes: { hostName: "LT-0042" } } };
    withIntl(<ReviewQueue initial={{ items: [r], page: 1, pageSize: 25, totalCount: 1 }} csrfToken="csrf" display={display("ar")} />, "ar");
    expect(screen.getByRole("heading", { name: "أي عنصر هو LT-0042؟" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "إنه CI-000010 Laptop A" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "إنه عنصر جديد" })).toBeInTheDocument();
  });
});

describe("CiDiscoveryPanel", () => {
  it("says when no source reports the item, who set each value and what software is on it", () => {
    const discovery: CiDiscovery = {
      missingSince: "2026-11-20T08:00:00Z", mergedInto: null,
      records: [{ source: { id: id(), name: "Intune", kind: "Intune" }, externalId: "dev-1", name: "LT-0042", firstSeenAt: "2026-11-01T08:00:00Z", lastSeenAt: "2026-11-19T08:00:00Z", missingSince: "2026-11-20T08:00:00Z" }],
      valueSetters: { serialNumber: { manual: false, source: { id: id(), name: "Intune", kind: "Intune" } }, name: { manual: true, source: null } },
      software: [{ name: "Microsoft Teams", publisher: "Microsoft", version: "24.1", source: null, lastSeenAt: "2026-11-19T08:00:00Z" }],
    };
    const attributes = [{ field: { key: "serialNumber", type: "Text" as const, label: { en: "Serial number", ar: "الرقم التسلسلي" } }, classId: id(), value: "SN-42", display: null }];
    withIntl(<CiDiscoveryPanel discovery={discovery} attributes={attributes as never} display={display()} />);
    expect(screen.getByText(/No source reports this item any more/)).toBeInTheDocument();
    expect(screen.getByText("Serial number")).toBeInTheDocument();
    expect(screen.getByText("People (on screen or by import)")).toBeInTheDocument();
    expect(screen.getByText("Microsoft Teams")).toBeInTheDocument();
  });
});
