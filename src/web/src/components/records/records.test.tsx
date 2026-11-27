import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { hasRecordPermission, statesProblem, type CustomRecord } from "@/lib/records";
import { RecordTypesEditor } from "./RecordTypesEditor";
import { RecordWorkPanel } from "./RecordWorkPanel";

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

function record(change: Partial<CustomRecord> = {}): CustomRecord {
  return {
    id: crypto.randomUUID(), number: "FAC-000001", type: "facilities", typeName: { en: "Facilities request", ar: "طلب مرافق" }, title: "AC broken",
    description: null, state: "InProgress", stateOfType: { key: "scheduled", name: { en: "Scheduled", ar: "مجدول" }, category: "InProgress" }, priority: 3,
    channel: "Portal", requester: null, loggedBy: null, assignee: null, group: null, category: null, awaitingApproval: false, onHoldReason: null, resolutionNotes: null,
    nextStates: [
      { key: "on-site", name: { en: "On site", ar: "في الموقع" }, category: "InProgress" },
      { key: "done", name: { en: "Done", ar: "منجز" }, category: "Resolved" },
    ],
    sla: [], etag: "\"4\"", createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), resolvedAt: null, closedAt: null, ...change,
  };
}

describe("record states", () => {
  it("need a New, a Resolved and a Closed state with unique keys", () => {
    const state = (key: string, category: "New" | "InProgress" | "Resolved" | "Closed") => ({ key, name: { en: key, ar: "" }, category });
    expect(statesProblem([state("new", "New"), state("done", "Resolved")])).toBe("count");
    expect(statesProblem([state("new", "New"), state("work", "InProgress"), state("done", "Resolved")])).toBe("categories");
    expect(statesProblem([state("new", "New"), state("new", "Resolved"), state("closed", "Closed")])).toBe("unique");
    expect(statesProblem([state("new", "New"), state("done", "Resolved"), state("closed", "Closed")])).toBeNull();
  });

  it("tell staff of any custom type from their permissions", () => {
    expect(hasRecordPermission(["incidents.read", "records.facilities.read"])).toBe(true);
    expect(hasRecordPermission(["incidents.read"])).toBe(false);
  });
});

describe("record work panel", () => {
  it("moves within a category at once and asks for notes before a Resolved state", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}));
    vi.stubGlobal("fetch", fetchMock);
    const r = record();
    withIntl(<RecordWorkPanel record={r} reasons={[]} groups={[]} canAssign={false} csrfToken="csrf" />);

    fireEvent.click(screen.getByRole("button", { name: "Move to On site" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(JSON.parse(String(fetchMock.mock.calls[0]![1].body))).toEqual({ stateKey: "on-site" });

    fireEvent.click(screen.getByRole("button", { name: "Move to Done" }));
    fireEvent.change(screen.getByLabelText("How was it done?"), { target: { value: "Filter replaced." } });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    const [url, init] = fetchMock.mock.calls[1]!;
    expect(String(url)).toContain(`/v1/records/facilities/${r.id}/transitions`);
    expect(JSON.parse(String(init.body))).toEqual({ stateKey: "done", resolutionNotes: "Filter replaced." });
    expect(new Headers(init.headers).get("If-Match")).toBe("\"4\"");
  });

  it("names the states in Arabic", () => {
    withIntl(<RecordWorkPanel record={record()} reasons={[]} groups={[]} canAssign={false} csrfToken="csrf" />, "ar");
    expect(screen.getByRole("button", { name: "النقل إلى في الموقع" })).toBeInTheDocument();
  });
});

describe("Settings → Record types", () => {
  it("makes a type with the starter states and its key, prefix and names", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok({}, 201));
    vi.stubGlobal("fetch", fetchMock);
    withIntl(<RecordTypesEditor initial={[]} csrfToken="csrf" />);

    fireEvent.click(screen.getByRole("button", { name: "New record type" }));
    fireEvent.change(screen.getByLabelText(/^Key/), { target: { value: "facilities" } });
    fireEvent.change(screen.getByLabelText("Number prefix"), { target: { value: "fac" } });
    fireEvent.change(screen.getByLabelText("Name (English)"), { target: { value: "Facilities request" } });
    fireEvent.change(screen.getByLabelText("Name (Arabic)"), { target: { value: "طلب مرافق" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const body = JSON.parse(String(fetchMock.mock.calls[0]![1].body));
    expect(body).toMatchObject({ key: "facilities", numberPrefix: "FAC", name: { en: "Facilities request", ar: "طلب مرافق" }, portalEntry: true });
    expect(body.states.map((s: { category: string }) => s.category)).toEqual(["New", "InProgress", "OnHold", "Resolved", "Closed"]);
  });

  it("refuses states without a Closed one", () => {
    withIntl(<RecordTypesEditor initial={[]} csrfToken="csrf" />);
    fireEvent.click(screen.getByRole("button", { name: "New record type" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove closed" }));
    expect(screen.getByText("Give at least one New, one Resolved and one Closed state.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });
});
