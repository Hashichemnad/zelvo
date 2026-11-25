import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { emptySettings, type Connector, type MonitoringSource } from "@/lib/connectors";
import { ConnectorsPanel } from "./ConnectorsPanel";
import { MonitoringPanel } from "./MonitoringPanel";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/connectors",
  useRouter: () => ({ push: vi.fn(), refresh: vi.fn() }),
}));

const display = { locale: "en", calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

function renderIn(locale: "en" | "ar", node: ReactNode) {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar} timeZone="Asia/Dubai">
      {node}
    </NextIntlClientProvider>,
  );
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const entra: Connector = {
  id: "c1", name: "Entity directory", kind: "EntraId", settings: { entra: { directoryId: "d", clientId: "c", usersGroupId: null, groupMappings: [], signIn: true } },
  credentialSet: true, credentialSetAt: null, active: true, syncMinutes: 60, lastSyncAt: "2026-11-25T08:00:00Z", lastSyncSucceeded: true,
  lastSyncStats: { read: 120, created: 3, linked: 2, updated: 5, disabled: 1, enabled: 0, membershipsAdded: 4, membershipsRemoved: 0, skipped: 0 },
  nextSyncAt: "2026-11-25T09:00:00Z", lastError: null, signInRedirectUri: "https://id.entity.gov.ae/realms/zelvo-entity/broker/entra/endpoint",
  createdAt: "2026-11-25T07:00:00Z", updatedAt: "2026-11-25T07:00:00Z",
};

afterEach(() => vi.unstubAllGlobals());

describe("Settings → Connectors (task E7)", () => {
  it("shows a directory's last sync, its sign-in redirect address and syncs on request", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ ...entra, nextSyncAt: "2026-11-25T08:05:00Z" }));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("en", <ConnectorsPanel initial={[entra]} csrfToken="csrf" groups={[]} display={display} />);
    fireEvent.click(screen.getByRole("button", { name: "Open Entity directory" }));
    const detail = screen.getByTestId("connector-detail");
    expect(within(detail).getByTestId("sync-stats")).toHaveTextContent("120 read, 3 created, 5 changed, 1 disabled");
    expect(within(detail).getByText(/broker\/entra\/endpoint/)).toBeInTheDocument();
    fireEvent.click(within(detail).getByRole("button", { name: "Sync now" }));
    await waitFor(() => expect(within(detail).getByText("The sync starts within a minute.")).toBeInTheDocument());
    expect(fetchMock.mock.calls[0]![0]).toBe("/bff/api/v1/connectors/c1/sync");
  });

  it("tests a REST operation before saving and shows what was sent and read", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({
      ok: true, status: 200, durationMs: 42, request: "POST https://crm.example.ae/cases/INC-000123", response: "{\"case\":{\"status\":\"Open\"}}",
      results: { status: "Open" }, externalRef: "C-1", error: null,
    }));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("ar", <ConnectorsPanel initial={[]} csrfToken="csrf" groups={[]} display={{ ...display, locale: "ar" }} />);
    fireEvent.click(screen.getByRole("button", { name: "واجهة REST (دون برمجة)" }));
    const builder = screen.getByTestId("rest-builder");
    fireEvent.change(within(builder).getByLabelText("العنوان الأساسي"), { target: { value: "https://crm.example.ae" } });
    fireEvent.change(within(builder).getByLabelText("المسار"), { target: { value: "/cases/{{ticket.number}}" } });
    fireEvent.click(within(builder).getByRole("button", { name: "اختبار هذه العملية" }));
    await waitFor(() => expect(within(builder).getByText("الرد 200 خلال 42 ملي ثانية.")).toBeInTheDocument());
    const body = JSON.parse(fetchMock.mock.calls[0]![1].body as string);
    expect(body).toMatchObject({ operation: "update-case", settings: { baseUrl: "https://crm.example.ae", operations: [{ path: "/cases/{{ticket.number}}" }] } });
    expect(emptySettings("Rest").rest?.operations).toHaveLength(1);
  });
});

describe("Settings → Monitoring (task E7)", () => {
  it("shows the token once after adding a Zabbix source", async () => {
    const source: MonitoringSource = {
      id: "s1", name: "Zabbix", format: "Generic", mapping: {}, webhookUrl: "https://zelvo.entity.gov.ae/hooks/entity/monitoring/s1", categoryId: null, locationId: null,
      groupId: null, resolveOnClear: false, active: true, lastAlertAt: null, createdAt: "2026-11-25T08:00:00Z", updatedAt: "2026-11-25T08:00:00Z",
    };
    const fetchMock = vi.fn().mockResolvedValue(json({ source, token: "token-made-once" }, 201));
    vi.stubGlobal("fetch", fetchMock);
    renderIn("en", <MonitoringPanel initial={[]} csrfToken="csrf" categories={[]} groups={[]} display={display} />);
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Zabbix" } });
    fireEvent.click(screen.getByLabelText("Zabbix"));
    fireEvent.click(screen.getByRole("button", { name: "Add a monitoring source" }));
    await waitFor(() => expect(screen.getByTestId("monitoring-token")).toHaveTextContent("token-made-once"));
    expect(JSON.parse(fetchMock.mock.calls[0]![1].body as string)).toMatchObject({ name: "Zabbix", format: "Generic", preset: "zabbix" });
  });
});
