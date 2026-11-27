import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { useState } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { mapLines, mapQuery, neighbours, type CiClass, type CiMap as CiMapData, type CiNode, type CiPick, type CiRelationshipType, type TicketCi } from "@/lib/cmdb";
import { checkAnswers } from "@/lib/forms/check";
import type { Answers, FormField, ResolvedField } from "@/lib/forms/types";
import { FormAnswersView } from "@/components/forms/FormAnswersView";
import { FormRenderer } from "@/components/forms/FormRenderer";
import { ClassesEditor } from "./ClassesEditor";
import { CiMap } from "./CiMap";
import { CiMapList } from "./CiMapList";
import { TicketCiPanel } from "./TicketCiPanel";

const push = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push, refresh: vi.fn() }),
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a>,
}));

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
});

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const id = () => crypto.randomUUID();

const classRef = (family: "Hardware" | "BusinessService" | "Application", en: string, ar: string) => ({
  id: id(), key: en.toLowerCase().replace(/\s+/g, "-"), family, name: { en, ar }, path: { en, ar },
});
const item = (number: string, name: string, cls = classRef("Hardware", "Server", "خادم")): CiNode => ({
  kind: "ConfigurationItem", id: id(), number, name, localizedName: null, class: cls, status: "Active",
});
const runsOn = { id: id(), key: "runs-on", outgoingName: { en: "runs on", ar: "يعمل على" }, incomingName: { en: "hosts", ar: "يستضيف" }, impact: "ToSource" as const };
const usedBy = { id: id(), key: "used-by", outgoingName: { en: "is used by", ar: "يستخدمه" }, incomingName: { en: "uses", ar: "يستخدم" }, impact: "ToSource" as const };

function sampleMap() {
  const web = item("CI-000001", "srv-web-01");
  const host = item("CI-000002", "vm-host-03");
  const portal = item("CI-000003", "Customer portal", classRef("BusinessService", "Business service", "خدمة أعمال"));
  const finance: CiNode = { kind: "Department", id: id(), number: null, name: null, localizedName: { en: "Finance", ar: "المالية" }, class: null, status: null };
  const map: CiMapData = {
    nodes: [{ node: web, depth: 0 }, { node: host, depth: 1 }, { node: portal, depth: 1 }, { node: finance, depth: 2 }],
    edges: [
      { id: id(), source: web.id, target: host.id, type: runsOn, note: null },
      { id: id(), source: portal.id, target: web.id, type: runsOn, note: "front end" },
      { id: id(), source: portal.id, target: finance.id, type: usedBy, note: null },
    ],
    truncated: false,
  };
  return { map, web, host, portal, finance };
}

describe("relationship map helpers", () => {
  it("reads every relationship from its source and finds a node's neighbours both ways", () => {
    const { map, web, host, portal } = sampleMap();
    const lines = mapLines(map);
    expect(lines).toHaveLength(3);
    expect(lines[0]!.source.id).toBe(web.id);
    const around = neighbours(map, web.id);
    expect(around.map((n) => [n.node.id, n.outgoing])).toEqual([[host.id, true], [portal.id, false]]);
    expect(mapQuery({ depth: 3, typeIds: ["a", "b"], classIds: [], consumers: false, retired: true })).toBe("depth=3&consumers=false&retired=true&typeIds=a&typeIds=b");
  });
});

describe("CiMapList", () => {
  it("lists the same nodes and relationships as the map, in English", () => {
    const { map, web } = sampleMap();
    withIntl(<CiMapList map={map} centreId={web.id} />);
    const table = screen.getByRole("table", { name: "4 nodes" });
    expect(within(table).getAllByRole("row")).toHaveLength(5);
    expect(within(table).getByText("(this item)")).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "CI-000002 vm-host-03" })).toHaveAttribute("href", expect.stringMatching(/^\/cmdb\//));
    expect(within(table).getByText("Finance")).toBeInTheDocument();
    const lines = within(screen.getByTestId("ci-map-lines")).getAllByRole("listitem").map((li) => li.textContent);
    expect(lines).toContain("CI-000001 srv-web-01 — runs on → CI-000002 vm-host-03");
    expect(lines).toContain("CI-000003 Customer portal — is used by → Finance");
  });

  it("reads right to left in Arabic", () => {
    const { map, web } = sampleMap();
    withIntl(<CiMapList map={map} centreId={web.id} />, "ar");
    const lines = within(screen.getByTestId("ci-map-lines")).getAllByRole("listitem").map((li) => li.textContent);
    expect(lines).toContain("العلاقة: CI-000001 srv-web-01 — يعمل على ← CI-000002 vm-host-03");
    expect(screen.getByText("المالية")).toBeInTheDocument();
    expect(screen.getAllByText("خدمات الأعمال").length).toBeGreaterThan(0);
  });
});

describe("CiMap", () => {
  it("loads the map with its filters and shows the list view with the same content", async () => {
    const { map, web } = sampleMap();
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(ok(map)));
    vi.stubGlobal("fetch", fetchMock);
    const types: CiRelationshipType[] = [{ ...runsOn, targetKind: "ConfigurationItem", builtIn: true, active: true, relationshipCount: 2 }];
    withIntl(<CiMap itemId={web.id} types={types} classes={[]} version={0} initialView="list" />);
    await screen.findByTestId("ci-map-list");
    expect(fetchMock.mock.calls[0]![0]).toBe(`/bff/api/v1/cmdb/items/${web.id}/map?depth=2&consumers=true&retired=false`);
    expect(screen.getByRole("button", { name: "List" })).toHaveAttribute("aria-pressed", "true");

    fireEvent.change(screen.getByLabelText("Steps from this item"), { target: { value: "3" } });
    fireEvent.click(screen.getByLabelText("runs on"));
    fireEvent.click(screen.getByLabelText("Show people, departments and customer groups"));
    await waitFor(() => expect(fetchMock).toHaveBeenLastCalledWith(
      `/bff/api/v1/cmdb/items/${web.id}/map?depth=3&consumers=false&retired=false&typeIds=${runsOn.id}`, expect.anything()));
    // The live region is always on the page, so selections in the map are announced.
    expect(screen.getByTestId("ci-map-announcement")).toHaveAttribute("aria-live", "polite");
  });
});

const ciField = (multiple = false): FormField => ({ key: "service", type: "ConfigurationItem", label: { en: "Affected service", ar: "الخدمة المتأثرة" }, multiple, configurationItemClasses: ["c1"] });

function RendererHarness({ fields, onAnswers }: { fields: ResolvedField[]; onAnswers: (a: Answers) => void }) {
  const [answers, setAnswers] = useState<Answers>({});
  return (
    <FormRenderer fields={fields} answers={answers} defaults={{}} errors={[]} contentLocale="en" locations={[]} idPrefix="t"
      onChange={(next) => {
        setAnswers(next);
        onAnswers(next);
      }} />
  );
}

describe("configuration item field", () => {
  const picks: CiPick[] = [
    { id: id(), number: "CI-000010", name: "Email service", status: "Active", class: classRef("BusinessService", "Business service", "خدمة أعمال") },
    { id: id(), number: "CI-000011", name: "Payroll", status: "Active", class: classRef("Application", "Application", "تطبيق") },
  ];

  it("finds items of the field's classes and picks one", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(ok(picks)));
    vi.stubGlobal("fetch", fetchMock);
    const onAnswers = vi.fn();
    withIntl(<RendererHarness fields={[{ field: ciField() }]} onAnswers={onAnswers} />);
    expect(screen.getByRole("group", { name: "Affected service" })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Find an item by number or name"), { target: { value: "mail" } });
    fireEvent.click(screen.getByRole("button", { name: "Find" }));
    expect(await screen.findByText("2 items found.")).toBeInTheDocument();
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/cmdb/lookup?q=mail&classIds=c1");
    fireEvent.click(screen.getByRole("button", { name: /Choose CI-000010 Email service/ }));
    expect(onAnswers).toHaveBeenLastCalledWith({ service: picks[0]!.id });
    expect(screen.getByText("CI-000010 Email service")).toBeInTheDocument();
    expect(screen.getByLabelText("Choose another item instead")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove CI-000010 Email service" }));
    expect(onAnswers).toHaveBeenLastCalledWith({ service: null });
  });

  it("keeps several items and checks them like the server", async () => {
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(ok(picks))));
    const onAnswers = vi.fn();
    withIntl(<RendererHarness fields={[{ field: ciField(true) }]} onAnswers={onAnswers} />);
    fireEvent.click(screen.getByRole("button", { name: "Find" }));
    fireEvent.click(await screen.findByRole("button", { name: /Choose CI-000010/ }));
    fireEvent.click(screen.getByRole("button", { name: "Find" }));
    // An item already chosen is not offered again.
    fireEvent.click(await screen.findByRole("button", { name: /Choose CI-000011/ }));
    expect(screen.queryByRole("button", { name: /Choose CI-000010/ })).toBeNull();
    expect(onAnswers).toHaveBeenLastCalledWith({ service: [picks[0]!.id, picks[1]!.id] });

    const field = ciField(true);
    expect(checkAnswers([field], { service: [picks[0]!.id] }, {}).errors).toEqual([]);
    expect(checkAnswers([{ ...field, required: true }], { service: [] }, {}).errors).toEqual([{ field: "service", code: "required" }]);
    expect(checkAnswers([field], { service: picks[0]!.id }, {}).errors).toEqual([{ field: "service", code: "type" }]);
    expect(checkAnswers([field], { service: Array.from({ length: 21 }, () => id()) }, {}).errors).toEqual([{ field: "service", code: "too-many" }]);
    expect(checkAnswers([ciField()], { service: "not-an-id" }, {}).errors).toEqual([{ field: "service", code: "type" }]);
  });

  it("shows picked items by number and name in the answers", () => {
    const itemId = id();
    withIntl(<FormAnswersView attachmentBase="/x" view={{
      form: { id: "f", key: "k", name: { en: "Report", ar: "بلاغ" }, description: { en: null, ar: null }, versionId: "v", versionNumber: 1 },
      fields: [{ field: ciField(true) }], answers: { service: [itemId] },
      references: { users: {}, locations: {}, files: {}, configurationItems: { [itemId]: "CI-000010 Email service" } },
    }} />);
    expect(screen.getByText("CI-000010 Email service")).toBeInTheDocument();
  });
});

const ciClass = (over: Partial<CiClass>): CiClass => ({
  id: id(), key: "server", parentId: null, depth: 1, family: "Hardware", name: { en: "Server", ar: "خادم" }, description: { en: null, ar: null },
  path: { en: "Server", ar: "خادم" }, attributes: [], inheritedAttributes: [], requesterVisible: false, active: true, selectable: true, itemCount: 0, etag: "\"1\"", ...over,
});

describe("ClassesEditor", () => {
  it("adds a sub-class with an attribute, showing the inherited ones and the server's problems", async () => {
    const parent = ciClass({ attributes: [{ key: "hostName", type: "Text", label: { en: "Host name", ar: "اسم المضيف" } }] });
    const bodies: unknown[] = [];
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === "POST") {
        bodies.push(JSON.parse(init.body as string));
        if (bodies.length === 1) {
          return Promise.resolve(ok({ code: "attributes-invalid", problems: [{ field: "number1", code: "attribute-inherited", message: "taken" }] }, 409));
        }
        return Promise.resolve(ok(ciClass({ key: "database-server" }), 201));
      }
      return Promise.resolve(ok([parent, ciClass({ key: "database-server", parentId: parent.id, depth: 2, name: { en: "Database server", ar: "خادم قاعدة بيانات" } })]));
    });
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<ClassesEditor initial={[parent]} csrfToken="csrf" canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Add a class below Server" }));
    const form = screen.getByRole("form", { name: "New class below Server" });
    expect(within(form).getByTestId("inherited-attributes")).toHaveTextContent("Host name (Text), from Server");
    fireEvent.change(within(form).getByLabelText("Key"), { target: { value: "database-server" } });
    fireEvent.change(within(form).getAllByLabelText("In English")[0]!, { target: { value: "Database server" } });
    // Only the attribute types the CMDB allows are offered.
    const typeSelect = within(form).getByLabelText("Type of the new attribute");
    expect(within(typeSelect).getAllByRole("option").map((o) => o.getAttribute("value"))).toEqual(["Text", "Number", "Date", "Dropdown", "Checkbox", "Radio", "User", "Location"]);
    fireEvent.change(typeSelect, { target: { value: "Number" } });
    fireEvent.click(within(form).getByRole("button", { name: "Add the attribute" }));
    // Attributes have no rules, read-only setting or personal data.
    expect(within(form).queryByRole("button", { name: "Add a rule" })).toBeNull();
    expect(within(form).queryByLabelText("Read only")).toBeNull();

    fireEvent.click(within(form).getByRole("button", { name: "Save the class" }));
    expect(await within(form).findByText("A class above already has an attribute with this key.")).toBeInTheDocument();
    expect(bodies[0]).toMatchObject({ key: "database-server", parentId: parent.id, family: null, name: { en: "Database server", ar: null },
      attributes: [{ key: "number1", type: "Number" }] });

    fireEvent.click(within(form).getByRole("button", { name: "Save the class" }));
    await waitFor(() => expect(screen.queryByRole("form", { name: "New class below Server" })).toBeNull());
    expect(screen.getAllByTestId("cmdb-class")).toHaveLength(2);
  });
});

describe("TicketCiPanel", () => {
  it("lists, adds and removes the items of a ticket", async () => {
    const pick: CiPick = { id: id(), number: "CI-000020", name: "Mail gateway", status: "Active", class: classRef("Hardware", "Server", "خادم") };
    const link: TicketCi = { item: pick, addedById: null, addedByName: null, addedAt: "2026-11-27T08:00:00Z" };
    const ticketId = id();
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (url.includes("/cmdb/lookup")) return Promise.resolve(ok([pick]));
      if (init?.method === "PUT") return Promise.resolve(ok([link]));
      if (init?.method === "DELETE") return Promise.resolve(new Response(null, { status: 204 }));
      return Promise.resolve(ok([]));
    });
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<TicketCiPanel ticketId={ticketId} kind="incidents" canEdit canOpenItems csrfToken="csrf" />);
    expect(await screen.findByText("No configuration items yet.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Find" }));
    fireEvent.click(await screen.findByRole("button", { name: /Choose CI-000020 Mail gateway/ }));
    expect(await screen.findByRole("link", { name: "CI-000020 Mail gateway" })).toHaveAttribute("href", `/cmdb/${pick.id}`);
    expect(fetchMock).toHaveBeenCalledWith(`/bff/api/v1/incidents/${ticketId}/configuration-items/${pick.id}`, expect.objectContaining({ method: "PUT" }));

    fireEvent.click(screen.getByRole("button", { name: "Remove CI-000020 Mail gateway" }));
    await waitFor(() => expect(screen.queryByRole("link", { name: "CI-000020 Mail gateway" })).toBeNull());
    expect(fetchMock).toHaveBeenCalledWith(`/bff/api/v1/incidents/${ticketId}/configuration-items/${pick.id}`, expect.objectContaining({ method: "DELETE" }));
  });

  it("shows names without links for people who cannot open the CMDB, and no changes without the edit permission", async () => {
    const pick: CiPick = { id: id(), number: "CI-000021", name: "Printer", status: "Maintenance", class: classRef("Hardware", "Printer", "طابعة") };
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(ok([{ item: pick, addedById: null, addedByName: null, addedAt: "2026-11-27T08:00:00Z" }]))));
    withIntl(<TicketCiPanel ticketId={id()} kind="service-requests" canEdit={false} canOpenItems={false} csrfToken="csrf" />, "ar");
    expect(await screen.findByText("CI-000021 Printer")).toBeInTheDocument();
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByText("قيد الصيانة")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "بحث" })).toBeNull();
  });
});
