import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * The built-in MCP server (task E6, module 9), against `make up`: an administrator creates an API
 * key, makes it an AI agent with two tools in Settings → AI → MCP server and turns the server on;
 * the agent raises an incident through MCP, which waits for approval; the support agent approves
 * it in AI approvals and the incident exists. English and Arabic pages pass the accessibility
 * checks. Runs in its own project because it changes tenant-wide settings.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const apiUrl = process.env.API_URL ?? "http://localhost:8080";

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

/** One MCP request over Streamable HTTP (stateless); the answer may come as JSON or as one SSE event. */
async function mcp(request: APIRequestContext, key: string, method: string, params: unknown) {
  const response = await request.post(`${apiUrl}/api/v1/mcp`, {
    headers: { "X-Api-Key": key, Accept: "application/json, text/event-stream", "Content-Type": "application/json" },
    data: { jsonrpc: "2.0", id: Date.now(), method, params },
  });
  expect(response.status()).toBe(200);
  const text = await response.text();
  const data = text.trimStart().startsWith("{") ? text : text.split("\n").find((line) => line.startsWith("data: "))!.slice(6);
  return JSON.parse(data) as { result: { content: { text: string }[]; isError?: boolean; tools?: { name: string }[] } };
}

test.describe("MCP server", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(180_000);

  test("an AI agent raises an incident that a person approves", async ({ page, browser, request }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));

    // An API key for the agent.
    await page.goto("/en/settings/integrations");
    const name = `E2E agent ${Date.now()}`;
    await page.getByLabel("Name", { exact: true }).last().fill(name);
    await page.getByLabel("Acts as role").last().selectOption("agent");
    await page.getByRole("checkbox", { name: /See every incident/ }).last().check();
    await page.getByRole("checkbox", { name: /Log incidents for others/ }).last().check();
    await page.getByRole("button", { name: "Create integration" }).click();
    const key = (await page.getByTestId("secret-value").innerText()).trim();
    await page.getByRole("button", { name: "I have stored it" }).click();

    // Make it an AI agent with two tools; approval stays on.
    await page.goto("/en/settings/ai/mcp");
    await expectAccessible(page);
    if ((await page.getByTestId("mcp-status").innerText()).trim() === "Off") {
      await page.getByRole("button", { name: "Turn the MCP server on" }).click();
      await expect(page.getByTestId("mcp-status")).toHaveText("On");
    }
    await page.getByLabel("Make an integration an AI agent").selectOption({ label: name });
    await page.getByRole("button", { name: "Add as AI agent" }).click();
    const agent = page.getByTestId("mcp-agent").filter({ hasText: name });
    await agent.getByRole("checkbox", { name: "Turn on Find incidents" }).check();
    await agent.getByRole("checkbox", { name: "Turn on Raise an incident" }).check();
    await expect(agent.getByRole("checkbox", { name: "Raise an incident needs a person's approval" })).toBeChecked();
    await agent.getByRole("button", { name: /Save tools/ }).click();
    await expect(page.getByRole("status")).toContainText(name);

    // The agent sees its tools and asks for an incident.
    const tools = await mcp(request, key, "tools/list", {});
    expect(tools.result.tools!.map((t) => t.name).sort()).toEqual(["create_incident", "get_action_status", "search_incidents"]);
    const title = `Printer offline ${Date.now()}`;
    const asked = await mcp(request, key, "tools/call", { name: "create_incident", arguments: { title, requesterEmail: devUser("requester") } });
    const pending = JSON.parse(asked.result.content[0]!.text) as { status: string; actionId: string };
    expect(pending.status).toBe("pending-approval");

    // The support agent approves it, in Arabic too.
    const context = await browser.newContext();
    const agentPage = await context.newPage();
    await signInWithPassword(agentPage, devUser("agent"));
    await completeSecondFactor(agentPage, devUser("agent"));
    await agentPage.goto("/ar/ai-approvals");
    await expect(agentPage.locator("html")).toHaveAttribute("dir", "rtl");
    await expectAccessible(agentPage);
    await agentPage.goto("/en/ai-approvals");
    const item = agentPage.getByTestId("ai-approval").filter({ hasText: title });
    await expectAccessible(agentPage);
    await item.getByRole("button", { name: /^Approve/ }).click();
    await expect(agentPage.getByRole("status")).toContainText("Approved and done. INC-");
    await context.close();

    const status = await mcp(request, key, "tools/call", { name: "get_action_status", arguments: { actionId: pending.actionId } });
    expect(JSON.parse(status.result.content[0]!.text)).toMatchObject({ status: "Succeeded" });

    // The activity list shows what the agent did.
    await page.goto("/en/settings/ai/mcp");
    await expect(page.getByTestId("mcp-action-row").filter({ hasText: title })).toContainText("Done");
    await page.goto("/ar/settings/ai/mcp-servers");
    await expectAccessible(page);
  });
});
