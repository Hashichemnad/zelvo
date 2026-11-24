import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { AnchorHTMLAttributes, ReactNode } from "react";
import ar from "../../../../messages/ar.json";
import en from "../../../../messages/en.json";
import type { ApiClient, Page } from "@/lib/integrations";
import { canDecideAiActions, type McpAction, type McpServer, type McpServerConnection } from "@/lib/mcp";
import { AiApprovalsList } from "../../ai/AiApprovalsList";
import { McpServerPanel } from "./McpServerPanel";
import { McpServersPanel } from "./McpServersPanel";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/settings/ai/mcp",
  useRouter: () => ({ push: vi.fn() }),
  Link: ({ href, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement>) => <a href={`/en${href}`} {...rest} />,
}));

const display = { calendar: "gregory", digits: "latn", timeZone: "Asia/Dubai" } as const;

const server: McpServer = {
  enabled: false,
  endpoint: "/api/v1/mcp",
  tools: [
    { name: "search_knowledge", description: "Searches knowledge.", permission: null, changesData: false },
    { name: "create_incident", description: "Raises an incident.", permission: "incidents.create", changesData: true },
  ],
  agents: [
    {
      clientId: "c1",
      name: "Copilot Studio",
      kind: "ApiKey",
      status: "Active",
      usable: true,
      role: "agent",
      tools: ["search_knowledge"],
      toolsWithoutApproval: [],
      allowedTools: ["search_knowledge", "create_incident"],
      updatedAt: "2026-11-24T08:00:00Z",
    },
  ],
  pendingApprovals: 0,
  approvalWindowHours: 24,
};

const client = (id: string, name: string): ApiClient => ({
  id, kind: "ApiKey", name, description: null, role: "agent", scopes: [], rateLimitPerMinute: 600, status: "Active", expired: false, expiresAt: null,
  keyHint: null, clientId: null, tokenUrl: null, createdAt: "2026-11-24T08:00:00Z", updatedAt: "2026-11-24T08:00:00Z", rotatedAt: null, revokedAt: null, lastUsedAt: null,
});

const action: McpAction = {
  id: "a1",
  agent: { id: "c1", name: "Copilot Studio" },
  tool: "create_incident",
  summary: "Raise an incident for mariam@example.test: \"Printer offline\"",
  arguments: { title: "Printer offline" },
  status: "Pending",
  approvalRequired: true,
  requestedAt: "2026-11-24T08:00:00Z",
  expiresAt: "2026-11-25T08:00:00Z",
  decidedBy: null,
  decidedAt: null,
  note: null,
  ticket: null,
  completedAt: null,
  errorCode: null,
  error: null,
};

const hr: McpServerConnection = {
  id: "s1",
  name: "HR system",
  url: "https://hr.example.test/mcp",
  authHeader: "Authorization",
  secretSet: true,
  secretSetAt: "2026-11-24T08:00:00Z",
  status: "Active",
  tools: [{ name: "lookup_employee", description: "Finds an employee.", readOnlyHint: true, enabled: false, changesData: false, missing: false }],
  toolsRefreshedAt: "2026-11-24T08:00:00Z",
  lastTestAt: "2026-11-24T08:00:00Z",
  lastTestSucceeded: false,
  lastTestError: "server-auth-failed: The MCP server refused the credential.",
  updatedAt: "2026-11-24T08:00:00Z",
};

function wrap(children: ReactNode, locale: "en" | "ar" = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "en" ? en : ar}>
      <div dir={locale === "ar" ? "rtl" : "ltr"}>{children}</div>
    </NextIntlClientProvider>,
  );
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function keys(value: unknown, prefix = ""): string[] {
  return value && typeof value === "object"
    ? Object.entries(value as Record<string, unknown>).flatMap(([k, v]) => keys(v, `${prefix}${k}.`))
    : [prefix];
}

afterEach(() => vi.unstubAllGlobals());

describe("MCP (task E6)", () => {
  it("has every text in English and Arabic", () => {
    expect(keys(ar.aiApprovals).sort()).toEqual(keys(en.aiApprovals).sort());
    expect(keys(ar.settings.ai.mcp).sort()).toEqual(keys(en.settings.ai.mcp).sort());
    expect(keys(ar.settings.ai.mcpServers).sort()).toEqual(keys(en.settings.ai.mcpServers).sort());
  });

  it("shows AI approvals to people who may make the changes", () => {
    expect(canDecideAiActions(["incidents.edit"])).toBe(true);
    expect(canDecideAiActions(["knowledge.read"])).toBe(false);
  });

  it("turns the server on and saves an agent's tools with approval, sending the CSRF token", async () => {
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (url === "/bff/api/v1/ai/mcp/settings") return json({ ...server, enabled: true });
      if (url.startsWith("/bff/api/v1/ai/mcp/agents/") && init?.method === "PUT") return json(server.agents[0]);
      return json({ ...server, enabled: true });
    });
    vi.stubGlobal("fetch", fetchMock);
    wrap(<McpServerPanel initial={server} clients={[client("c1", "Copilot Studio"), client("c2", "Own agent")]} actions={[]} csrfToken="csrf-1" display={{ locale: "en", ...display }} />);

    fireEvent.click(screen.getByRole("button", { name: "Turn the MCP server on" }));
    await waitFor(() => expect(screen.getByTestId("mcp-status")).toHaveTextContent("On"));

    const agent = screen.getByTestId("mcp-agent");
    const approval = within(agent).getByRole("checkbox", { name: "Raise an incident needs a person's approval" });
    expect(approval).toBeDisabled();
    fireEvent.click(within(agent).getByRole("checkbox", { name: "Turn on Raise an incident" }));
    expect(approval).toBeEnabled();
    expect(approval).toBeChecked();
    fireEvent.click(within(agent).getByRole("button", { name: /Save tools/ }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Saved the tools of Copilot Studio."));

    const put = fetchMock.mock.calls.find(([url, init]) => String(url).endsWith("/agents/c1") && init?.method === "PUT")!;
    expect(JSON.parse(String(put[1]!.body))).toEqual({ tools: ["search_knowledge", "create_incident"], toolsWithoutApproval: [] });
    expect((put[1]!.headers as Record<string, string>)["X-CSRF-Token"]).toBe("csrf-1");
    // Only integrations that are not agents yet can be added.
    expect(screen.getByRole("option", { name: "Own agent" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Copilot Studio" })).not.toBeInTheDocument();
  });

  it("is in Arabic with right-to-left layout", () => {
    wrap(<McpServerPanel initial={server} clients={[]} actions={[{ ...action, status: "Succeeded", decidedBy: { id: "u1", name: "Fatima" } }]} csrfToken="x" display={{ locale: "ar", ...display, digits: "arab" }} />, "ar");
    expect(screen.getByRole("button", { name: "تفعيل خادم MCP" })).toBeInTheDocument();
    expect(screen.getByText("تم")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "تفعيل تسجيل بلاغ" })).toBeInTheDocument();
  });

  it("adds a customer MCP server, reads its tools and explains failed tests", async () => {
    const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
      if (url === "/bff/api/v1/ai/mcp-servers" && init?.method === "POST") return json(hr, 201);
      if (url.endsWith("/refresh")) return json(hr);
      return json([hr]);
    });
    vi.stubGlobal("fetch", fetchMock);
    wrap(<McpServersPanel initial={[]} csrfToken="csrf-2" display={{ locale: "en", ...display }} />);

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "HR system" } });
    fireEvent.change(screen.getByLabelText("Address"), { target: { value: "https://hr.example.test/mcp" } });
    fireEvent.change(screen.getByLabelText("Credential"), { target: { value: crypto.randomUUID() } });
    fireEvent.click(screen.getByRole("button", { name: "Add and read tools" }));

    await waitFor(() => expect(screen.getByTestId("mcp-server")).toBeInTheDocument());
    expect(screen.getByText("The server refused the credential.")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Turn on lookup_employee" })).not.toBeChecked();
    const post = fetchMock.mock.calls.find(([url, init]) => url === "/bff/api/v1/ai/mcp-servers" && init?.method === "POST")!;
    expect(JSON.parse(String(post[1]!.body))).toMatchObject({ name: "HR system", authHeader: "Authorization" });
  });

  it("shows refused addresses in the person's language", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => json({ code: "validation-failed", urlProblem: "url-not-https" }, 400)));
    wrap(<McpServersPanel initial={[]} csrfToken="x" display={{ locale: "ar", ...display }} />, "ar");
    fireEvent.change(screen.getByLabelText("الاسم"), { target: { value: "HR" } });
    fireEvent.change(screen.getByLabelText("العنوان"), { target: { value: "http://hr.example.test/mcp" } });
    fireEvent.click(screen.getByRole("button", { name: "إضافة وقراءة الأدوات" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("استخدموا عنوانًا يبدأ بـ https://."));
  });

  it("approves a waiting change with a note", async () => {
    const page: Page<McpAction> = { items: [action], page: 1, pageSize: 50, totalCount: 1 };
    const fetchMock = vi.fn(async (url: string) =>
      url.endsWith("/approve") ? json({ ...action, status: "Succeeded", ticket: { id: "t1", name: "INC-000042" } }) : json({ ...page, items: [], totalCount: 0 }));
    vi.stubGlobal("fetch", fetchMock);
    wrap(<AiApprovalsList initial={page} csrfToken="csrf-3" display={{ locale: "en", ...display }} />);

    expect(screen.getByText("Raise an incident")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Note for the agent (optional)"), { target: { value: "Fine" } });
    fireEvent.click(screen.getByRole("button", { name: /^Approve/ }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Approved and done. INC-000042"));
    expect(screen.getByText("Nothing is waiting for your approval.")).toBeInTheDocument();
    const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith("/approve"))! as unknown as [string, RequestInit];
    expect(JSON.parse(String(call[1].body))).toEqual({ note: "Fine" });
  });

  it("says when someone else decided first", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => (url.endsWith("/reject") ? json({ code: "action-not-pending" }, 409) : json({ items: [], page: 1, pageSize: 50, totalCount: 0 }))));
    wrap(<AiApprovalsList initial={{ items: [action], page: 1, pageSize: 50, totalCount: 1 }} csrfToken="x" display={{ locale: "ar", ...display }} />, "ar");
    fireEvent.click(screen.getByRole("button", { name: /^رفض/ }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("تم البت في هذا الطلب سابقًا أو انتهت صلاحيته."));
  });
});
