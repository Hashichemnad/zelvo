import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../../messages/ar.json";
import en from "../../../../messages/en.json";
import type { ApiClient, IntegrationInfo, IntegrationLogEntry, Page, Webhook, WebhookDelivery } from "@/lib/integrations";
import { sectionsFor } from "@/lib/settings-sections";
import { ImportExportPanel } from "../ImportExportPanel";
import { ApiClientsPanel } from "./ApiClientsPanel";
import { IntegrationLogPanel } from "./IntegrationLogPanel";
import { WebhookDetailPanel } from "./WebhookDetailPanel";
import { WebhooksPanel } from "./WebhooksPanel";

const push = vi.fn();
vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/integrations",
  useRouter: () => ({ push }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

// Key-shaped values are made at run time, so none is ever committed.
const fakeApiKey = ["zvk", "abcdefghijkl", crypto.randomUUID().replaceAll("-", "")].join("_");

const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

const info: IntegrationInfo = {
  environment: "test",
  tokenUrl: "https://id.example.ae/realms/zelvo-acme/protocol/openid-connect/token",
  apiKeyHeader: "X-Api-Key",
  scopes: ["audit.read", "users.read"],
  events: ["user.created", "user.updated"],
  logRetentionDays: 30,
  defaultRateLimit: 600,
  maxRateLimit: 100000,
  roles: [
    { key: "agent", name: { en: "Agent", ar: "موظف الدعم", missing: [] }, scopes: ["users.read"] },
    { key: "requester", name: { en: "Requester", ar: "مقدّم الطلب", missing: [] }, scopes: [] },
  ],
};

const client: ApiClient = {
  id: "c1",
  kind: "ApiKey",
  name: "CRM sync",
  description: null,
  role: "agent",
  scopes: ["users.read"],
  rateLimitPerMinute: 1200,
  status: "Active",
  expired: false,
  expiresAt: null,
  keyHint: "zvk_abcdefghijkl_…",
  clientId: null,
  tokenUrl: null,
  createdAt: "2026-11-20T08:00:00Z",
  updatedAt: "2026-11-20T08:00:00Z",
  rotatedAt: null,
  revokedAt: null,
  lastUsedAt: null,
};

const page = <T,>(items: T[]): Page<T> => ({ items, page: 1, pageSize: 25, totalCount: items.length });

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      {children}
    </NextIntlClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

afterEach(() => {
  vi.unstubAllGlobals();
  push.mockReset();
});

describe("Settings sections for module 14", () => {
  it("shows Integrations to holders of integrations.manage and Import and export to user managers", () => {
    expect(sectionsFor(["integrations.manage"]).map((s) => s.key)).toEqual(["integrations"]);
    expect(sectionsFor(["users.manage", "users.read"]).map((s) => s.key)).toEqual(["importExport"]);
    expect(sectionsFor(["users.read"]).map((s) => s.key)).toEqual([]);
  });
});

describe("ApiClientsPanel", () => {
  it("lists integrations with their role, limit and status in Arabic", () => {
    wrap(<ApiClientsPanel initial={page([client])} info={info} csrfToken="csrf-1" display={{ ...display, locale: "ar", digits: "arab" }} />, "ar");

    const row = screen.getByTestId("api-client-row");
    expect(row).toHaveTextContent("CRM sync");
    expect(row).toHaveTextContent("موظف الدعم");
    expect(row).toHaveTextContent("١٬٢٠٠");
    expect(row).toHaveTextContent("نشط");
    expect(within(row).getByText("zvk_abcdefghijkl_…")).toHaveAttribute("dir", "ltr");
  });

  it("creates an API key with the chosen permissions and shows the key once", async () => {
    const fetchMock = vi.fn((url: string, init?: RequestInit) =>
      Promise.resolve(
        init?.method === "POST"
          ? json({ client, apiKey: fakeApiKey, clientSecret: null }, 201)
          : json(page([client])),
      ),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<ApiClientsPanel initial={page([])} info={info} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    expect(screen.getByText("No integrations yet.")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "CRM sync" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /See users/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create integration" }));

    await waitFor(() => expect(screen.getByTestId("secret-value")).toHaveTextContent(fakeApiKey));
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/integrations/clients");
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-1");
    expect(JSON.parse(init.body as string)).toMatchObject({ kind: "ApiKey", name: "CRM sync", role: "agent", scopes: ["users.read"], rateLimitPerMinute: 600 });
    expect(screen.getByText(/the only time it is shown/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "I have stored it" }));
    expect(screen.queryByTestId("secret-reveal")).not.toBeInTheDocument();
  });

  it("says when a role has nothing to give", () => {
    wrap(<ApiClientsPanel initial={page([])} info={info} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    fireEvent.change(screen.getByLabelText("Acts as role"), { target: { value: "requester" } });
    expect(screen.getByText("This role has no permissions that can be given to integrations.")).toBeInTheDocument();
  });

  it("revokes only after confirmation", async () => {
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(url.endsWith("/revoke") ? json({ ...client, status: "Revoked" }) : json(page([{ ...client, status: "Revoked" }]))),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<ApiClientsPanel initial={page([client])} info={info} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    fireEvent.click(screen.getByRole("button", { name: "Revoke" }));
    expect(fetchMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Yes, revoke it" }));

    await waitFor(() => expect(screen.getByText("CRM sync is revoked.")).toBeInTheDocument());
    expect(screen.getByTestId("api-client-row")).toHaveTextContent("Revoked");
  });
});

const webhook: Webhook = {
  id: "w1",
  name: "CRM",
  url: "https://crm.example.ae/hooks/zelvo",
  events: ["user.created"],
  status: "Active",
  disabledReason: null,
  consecutiveFailures: 0,
  secretRotating: false,
  previousSecretExpiresAt: null,
  createdAt: "2026-11-20T08:00:00Z",
  updatedAt: "2026-11-20T08:00:00Z",
};

describe("WebhooksPanel", () => {
  it("explains a refused receiver address", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ code: "validation-failed", urlProblem: "address-not-allowed" }, 400)));
    wrap(<WebhooksPanel initial={page([webhook])} info={info} csrfToken="csrf-1" />);

    expect(screen.getByTestId("webhook-row")).toHaveTextContent("https://crm.example.ae/hooks/zelvo");
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Internal" } });
    fireEvent.change(screen.getByLabelText("Receiver URL"), { target: { value: "https://10.0.0.5/hook" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /A user was created/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create webhook" }));

    await waitFor(() => expect(screen.getByText(/this server or an internal or reserved network/)).toBeInTheDocument());
  });

  it("needs at least one event", () => {
    vi.stubGlobal("fetch", vi.fn());
    wrap(<WebhooksPanel initial={page([])} info={info} csrfToken="csrf-1" />);

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "X" } });
    fireEvent.change(screen.getByLabelText("Receiver URL"), { target: { value: "https://x.example.ae" } });
    fireEvent.click(screen.getByRole("button", { name: "Create webhook" }));

    expect(screen.getByText("Choose at least one event.")).toBeInTheDocument();
  });
});

const delivery: WebhookDelivery = {
  id: 7,
  eventId: "e1",
  eventType: "user.created",
  status: "Failed",
  manual: false,
  attempts: 8,
  nextAttemptAt: null,
  lastAttemptAt: "2026-11-20T09:00:00Z",
  lastStatusCode: 503,
  lastDurationMs: 120,
  lastError: "http-503: the receiver answered 503",
  createdAt: "2026-11-20T08:00:00Z",
  completedAt: "2026-11-21T08:00:00Z",
  payload: '{"type":"user.created"}',
};

describe("WebhookDetailPanel", () => {
  it("sends a test event and shows the receiver's answer", async () => {
    const fetchMock = vi.fn((url: string) =>
      Promise.resolve(
        url.endsWith("/test")
          ? json({ succeeded: true, statusCode: 200, durationMs: 42, error: null, responseBody: '{"ok":true}', deliveryId: 9 })
          : json(page([delivery])),
      ),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<WebhookDetailPanel initial={webhook} initialDeliveries={page([delivery])} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    expect(screen.getByTestId("delivery-row")).toHaveTextContent("The receiver answered with an error status.");
    fireEvent.click(screen.getByRole("button", { name: "Send test event" }));

    await waitFor(() =>
      expect(screen.getByTestId("webhook-test-result")).toHaveTextContent("The receiver accepted the test event: status 200 in 42 ms."),
    );
  });

  it("queues a failed delivery to be sent again", async () => {
    const fetchMock = vi.fn((url: string) => Promise.resolve(url.endsWith("/redeliver") ? json({ ...delivery, id: 8, status: "Pending" }, 202) : json(page([delivery]))));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<WebhookDetailPanel initial={webhook} initialDeliveries={page([delivery])} csrfToken="csrf-1" display={{ ...display, locale: "en" }} />);

    fireEvent.click(screen.getByRole("button", { name: "Send again" }));

    await waitFor(() => expect(screen.getByText("Queued to be sent again within seconds.")).toBeInTheDocument());
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe("/bff/api/v1/integrations/webhooks/w1/deliveries/7/redeliver");
  });

  it("deletes after confirmation and goes back to the list", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 204 })));
    wrap(<WebhookDetailPanel initial={webhook} initialDeliveries={page([])} csrfToken="csrf-1" display={{ ...display, locale: "ar" }} />, "ar");

    fireEvent.click(screen.getByRole("button", { name: "حذف خطاف الويب" }));
    fireEvent.click(screen.getByRole("button", { name: "نعم، احذفه" }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/settings/integrations/webhooks"));
  });
});

describe("IntegrationLogPanel", () => {
  const entry: IntegrationLogEntry = {
    id: 3,
    occurredAt: "2026-11-20T08:00:00Z",
    direction: "Incoming",
    apiClientId: "c1",
    subscriptionId: null,
    name: "CRM sync",
    eventType: null,
    method: "GET",
    target: "/api/v1/users?email=[masked]",
    statusCode: 200,
    durationMs: 12,
    error: null,
    correlationId: "abc",
  };

  it("shows calls and loads their masked bodies on demand", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ entry, requestBody: null, responseBody: '{"email":"[masked]"}' })));
    wrap(<IntegrationLogPanel initial={page([entry])} display={{ ...display, locale: "en" }} />);

    const row = screen.getByTestId("log-row");
    expect(row).toHaveTextContent("GET /api/v1/users?email=[masked]");
    expect(row).toHaveTextContent("API calls in");
    fireEvent.click(within(row).getByRole("button", { name: "Details" }));

    await waitFor(() => expect(row).toHaveTextContent('"email": "[masked]"'));
    expect(row).toHaveTextContent("No body recorded.");
  });

  it("filters by direction", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(page([])));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<IntegrationLogPanel initial={page([entry])} display={{ ...display, locale: "en" }} />);

    fireEvent.change(screen.getByLabelText("Direction"), { target: { value: "Outgoing" } });

    await waitFor(() => expect(screen.getByText("Nothing logged yet.")).toBeInTheDocument());
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toContain("direction=Outgoing");
  });
});

describe("ImportExportPanel", () => {
  it("checks a file and lists the problems by row in Arabic", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      json({
        dryRun: true,
        applied: false,
        rows: 2,
        created: 1,
        updated: 0,
        unchanged: 0,
        errors: [{ row: 3, column: "email", code: "email-invalid", message: "Not a valid email address." }],
      }),
    );
    vi.stubGlobal("fetch", fetchMock);
    wrap(<ImportExportPanel csrfToken="csrf-1" canImport locale="ar" digits="latn" />, "ar");

    const input = screen.getByLabelText(/ملف CSV أو Excel/) as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(["email\nx"], "users.csv", { type: "text/csv" })] } });
    fireEvent.click(screen.getByRole("button", { name: "فحص الملف" }));

    await waitFor(() => expect(screen.getByTestId("import-errors")).toHaveTextContent("البريد الإلكتروني غير صالح."));
    expect(screen.getByTestId("import-result")).toHaveTextContent("قُرئ صفان");
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/bff/api/v1/bulk/users/import?dryRun=true");
    expect(init.body).toBeInstanceOf(FormData);
    expect(new Headers(init.headers).get("X-CSRF-Token")).toBe("csrf-1");
  });

  it("offers exports and templates, and no import without users.manage", () => {
    wrap(<ImportExportPanel csrfToken="csrf-1" canImport={false} locale="en" digits="latn" />);

    expect(screen.getByRole("link", { name: "Export as CSV" })).toHaveAttribute("href", "/bff/api/v1/bulk/users/export?format=csv");
    expect(screen.getByRole("link", { name: "Export as Excel" })).toHaveAttribute("href", "/bff/api/v1/bulk/users/export?format=xlsx");
    expect(screen.getByText("Your role may export users but not import them.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Import" })).not.toBeInTheDocument();
  });
});
