import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type BrowserContext, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Task F7: prepares a running installation for the load tests in tests/load (see its README).
 * Runs only with E2E_LOAD=1 (`npx playwright test --project=load-provision`):
 *
 * 1. The administrator creates LOAD_API_KEYS API keys that act as the agent role, with a rate
 *    limit of 100,000 a minute each (the most an administrator may set).
 * 2. LOAD_INCIDENTS sample incidents are raised with those keys for the development requester,
 *    so lists, searches and incident pages have data to read.
 * 3. The development agent signs in once with a second factor, and LOAD_SESSIONS more web
 *    sessions are opened from Keycloak's single sign-on (no new codes), so load-test agents use
 *    the web app's backend-for-frontend exactly as browsers do.
 *
 * Everything is written to tests/load/.credentials/load.json (git-ignored): keys and session
 * cookies are real credentials of that installation. Revoke the keys in Settings → Integrations
 * after the run.
 */

const apiUrl = process.env.API_URL ?? "http://localhost:8080";
const tenant = process.env.LOAD_TENANT ?? "default";
const keyCount = Number(process.env.LOAD_API_KEYS ?? "10");
const incidentCount = Number(process.env.LOAD_INCIDENTS ?? "500");
const sessionCount = Number(process.env.LOAD_SESSIONS ?? "50");
const output = path.resolve(__dirname, "../../load/.credentials/load.json");
const scopes = ["incidents.read", "incidents.create", "incidents.edit", "incidents.assign", "knowledge.read", "directory.read"];

/** Calls the API through the web app's proxy from the page, as the signed-in person. */
async function bff<T>(page: Page, method: string, apiPath: string, body?: unknown): Promise<T> {
  const csrf = await page.locator('input[name="csrf"]').first().getAttribute("value");
  return page.evaluate(
    async ({ method, apiPath, body, csrf }) => {
      const response = await fetch(`/bff/api/v1/${apiPath}`, {
        method,
        headers: { "Content-Type": "application/json", Accept: "application/json", "X-CSRF-Token": csrf ?? "" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        throw new Error(`${method} ${apiPath}: ${response.status} ${await response.text()}`);
      }
      return (await response.json()) as T;
    },
    { method, apiPath, body, csrf },
  );
}

/** The cookie header a load-test agent sends, and the session's CSRF token. */
async function sessionOf(context: BrowserContext, page: Page): Promise<{ cookie: string; csrf: string }> {
  const cookies = await context.cookies(page.url());
  const session = cookies.find((c) => /zelvo-session$/.test(c.name));
  expect(session, "the web app's session cookie").toBeTruthy();
  const csrf = await page.locator('input[name="csrf"]').first().getAttribute("value");
  expect(csrf).toBeTruthy();
  return { cookie: `${session!.name}=${session!.value}`, csrf: csrf! };
}

test.describe("load-test provisioning", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(30 * 60_000);

  const result: {
    createdAt: string;
    apiUrl: string;
    webUrl: string;
    tenant: string;
    requesterId: string;
    apiKeys: { id: string; key: string }[];
    incidentIds: string[];
    sessions: { cookie: string; csrf: string }[];
  } = { createdAt: new Date().toISOString(), apiUrl, webUrl: "", tenant, requesterId: "", apiKeys: [], incidentIds: [], sessions: [] };

  test("the administrator creates API keys for the agent role", async ({ page, baseURL }) => {
    result.webUrl = baseURL ?? "http://localhost:3000";
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    const stamp = new Date().toISOString().slice(0, 16);
    for (let i = 0; i < keyCount; i++) {
      const created = await bff<{ client: { id: string }; apiKey: string }>(page, "POST", "integrations/clients", {
        kind: "ApiKey",
        name: `Load test ${stamp} #${i + 1}`,
        description: "Task F7 load test; revoke after the run.",
        role: "agent",
        scopes,
        rateLimitPerMinute: 100_000,
      });
      result.apiKeys.push({ id: created.client.id, key: created.apiKey });
    }

    const people = await bff<{ items: { id: string; email: string }[] }>(page, "GET", `users?q=${encodeURIComponent(devUser("requester"))}`);
    const requester = people.items.find((u) => u.email === devUser("requester"));
    expect(requester, "the development requester").toBeTruthy();
    result.requesterId = requester!.id;
  });

  test("sample incidents are raised with the keys", async ({ request }) => {
    const words = ["printer", "VPN", "email", "laptop", "Wi-Fi", "password", "Teams", "badge", "monitor", "طابعة", "الشبكة", "البريد"];
    for (let i = 0; i < incidentCount; i++) {
      const key = result.apiKeys[i % result.apiKeys.length].key;
      const word = words[i % words.length];
      const response = await request.post(`${apiUrl}/api/v1/incidents`, {
        headers: { "X-Api-Key": key, "X-Zelvo-Tenant": tenant },
        data: {
          title: `Load test: ${word} not working (${i + 1})`,
          description: `Sample incident ${i + 1} for the load test. ${word} stopped working this morning.`,
          requesterId: result.requesterId,
          impact: (i % 3) + 1,
          urgency: ((i + 1) % 3) + 1,
        },
      });
      expect(response.status(), await response.text()).toBe(201);
      result.incidentIds.push(((await response.json()) as { id: string }).id);
    }
  });

  test("the agent opens web sessions for the load-test agents", async ({ page, context }) => {
    await signInWithPassword(page, devUser("agent"));
    await completeSecondFactor(page, devUser("agent"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);
    result.sessions.push(await sessionOf(context, page));

    for (let i = 1; i < sessionCount; i++) {
      // Forget only the web app's session; Keycloak's single sign-on (with the second factor)
      // signs the next one in without a new code.
      const keep = (await context.cookies()).filter((c) => !/zelvo-session$/.test(c.name));
      await context.clearCookies();
      await context.addCookies(keep);
      await page.goto("/auth/login?mfa=1&locale=en&returnTo=%2Fen%2Faccount");
      await expect(page).toHaveURL(/\/en\/account$/);
      result.sessions.push(await sessionOf(context, page));
    }

    mkdirSync(path.dirname(output), { recursive: true });
    writeFileSync(output, JSON.stringify(result, null, 2), { mode: 0o600 });
  });
});
