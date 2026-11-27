import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { menuHref, viewHref, type AppImport, type PackagePlan } from "@/lib/apps";
import type { RecordType } from "@/lib/records";
import { definitionProblem } from "./AppEditor";
import { DataListsEditor, dataListProblem } from "./DataListsEditor";
import { ImportReview } from "./ImportReview";

const refresh = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), refresh }),
  Link: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a>,
}));

afterEach(() => {
  vi.unstubAllGlobals();
  refresh.mockReset();
});

function withIntl(node: React.ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const ok = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const leave = {
  key: "leave",
  name: { en: "Leave request", ar: "طلب إجازة" },
  states: [{ key: "submitted", name: { en: "Submitted", ar: "مقدّم" }, category: "New", inUse: false }],
} as unknown as RecordType;

function plan(change: Partial<PackagePlan> = {}): PackagePlan {
  return {
    appKey: "hr", appName: { en: "Human resources", ar: "الموارد البشرية" }, icon: "people", source: { profile: "Test", productVersion: "0.1.0" }, exportedAt: null,
    problems: [], conflicts: [], items: [{ kind: "recordType", key: "leave", name: { en: "Leave request", ar: "طلب إجازة" }, exists: false }],
    permissionsCreated: ["records.leave.read"], grants: [{ role: "hr-officer", roleExists: false, permissions: ["records.leave.read"] }],
    references: [{ kind: "group", label: "HR team", foundId: null, foundName: null }],
    forms: 1, rules: 1, listViews: 0, menuEntries: 0, portalEntries: 1, valid: true, ...change,
  };
}

function pending(p: PackagePlan): AppImport {
  return {
    id: "i1", fileName: "hr.zelvo-app.json", sha256: "a".repeat(64), appKey: "hr", appName: p.appName, status: "Pending",
    importedAt: new Date().toISOString(), decidedAt: null, plan: p, choices: {}, result: null,
  };
}

describe("app builder helpers", () => {
  it("links list views and menu entries to the record list with the view's filters", () => {
    const view = { key: "open", name: { en: "Open", ar: null }, recordType: "leave", open: true, assignee: "me" as const };
    expect(viewHref(view)).toBe("/records/leave?open=true&assignee=me");
    expect(menuHref({ key: "m", label: { en: "M", ar: null }, target: "records", recordType: "leave", view: "open" }, [view])).toBe("/records/leave?open=true&assignee=me");
    expect(menuHref({ key: "n", label: { en: "N", ar: null }, target: "new", recordType: "leave" }, [view])).toBe("/records/leave/new");
  });

  it("checks entries against the app's own record types and their states", () => {
    const base = { listViews: [], menuEntries: [], portalEntries: [] };
    expect(definitionProblem(base, [leave])).toBeNull();
    expect(definitionProblem({ ...base, portalEntries: [{ key: "p", label: { en: "P", ar: null }, description: { en: null, ar: null }, recordType: "other" }] }, [leave])).toBe("entryType");
    expect(definitionProblem({ ...base, listViews: [{ key: "v", name: { en: "V", ar: null }, recordType: "leave", state: "gone" }] }, [leave])).toBe("viewState");
    expect(definitionProblem({ ...base, menuEntries: [{ key: "Bad", label: { en: "M", ar: null }, target: "records", recordType: "leave" }] }, [leave])).toBe("entryKey");
  });

  it("refuses data lists with repeated values or unnamed items", () => {
    const draft = { key: "rooms", name: { en: "Rooms", ar: "" }, description: { en: "", ar: "" }, columns: [], items: [{ key: "a", en: "A", ar: "", values: {}, active: true }] };
    expect(dataListProblem(draft, true)).toBeNull();
    expect(dataListProblem({ ...draft, items: [...draft.items, { ...draft.items[0]! }] }, true)).toBe("itemKeys");
    expect(dataListProblem({ ...draft, items: [{ ...draft.items[0]!, en: "" }] }, true)).toBe("itemLabels");
    expect(dataListProblem({ ...draft, key: "Bad key" }, true)).toBe("key");
  });
});

describe("DataListsEditor", () => {
  it("makes a list with its items", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(ok({})));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<DataListsEditor initial={[]} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "New data list" }));
    fireEvent.change(screen.getByLabelText("Key"), { target: { value: "rooms" } });
    fireEvent.change(screen.getAllByLabelText("In English")[0]!, { target: { value: "Rooms" } });
    fireEvent.change(screen.getByLabelText("Item 1 in English"), { target: { value: "Room 1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save data list" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/data-lists");
    expect(JSON.parse(init!.body as string)).toMatchObject({ key: "rooms", items: [{ key: "item1", label: { en: "Room 1", ar: null }, active: true }] });
  });
});

describe("ImportReview", () => {
  it("lists problems and references not found and cannot be approved", () => {
    withIntl(<ImportReview item={pending(plan({ valid: false, problems: [{ path: "references", code: "reference-not-found", message: "x" }] }))} csrfToken="csrf" />);
    expect(screen.getByTestId("import-problems")).toHaveTextContent("It refers to something this installation does not have.");
    expect(screen.getByText(/not found in this installation/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("checkbox"));
    expect(screen.getByRole("button", { name: "Approve and activate" })).toBeDisabled();
  });

  it("needs a choice for everything that exists and the administrator's confirmation, in Arabic too", async () => {
    const fetchMock = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(ok({})));
    vi.stubGlobal("fetch", fetchMock);
    const conflicts = [{ id: "recordType:leave", kind: "recordType" as const, key: "leave", existing: { en: "Leave", ar: "إجازة" }, replace: "" }];
    withIntl(<ImportReview item={pending(plan({ conflicts, references: [] }))} csrfToken="csrf" />, "ar");
    const approve = screen.getByRole("button", { name: "الاعتماد والتفعيل" });
    fireEvent.click(screen.getByRole("checkbox"));
    expect(approve).toBeDisabled();
    fireEvent.click(screen.getByRole("radio", { name: "الاستبدال بما في الحزمة" }));
    expect(approve).toBeEnabled();
    fireEvent.click(approve);
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/bff/api/v1/app-imports/i1/approve");
    expect(JSON.parse(init!.body as string)).toEqual({ choices: { "recordType:leave": "replace" } });
  });
});
