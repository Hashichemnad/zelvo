import { createHmac } from "node:crypto";
import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Integrations (task E1, module 14), against `make up`: an administrator creates an API key and
 * a webhook in Settings → Integrations, the key calls the API, the call shows in the integration
 * log, the webhook tester reaches the development webhook receiver with a valid signature, and a
 * revoked key is refused. Runs in its own project because it signs in as the administrator.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const apiUrl = process.env.API_URL ?? "http://localhost:8080";
const receiverUrl = process.env.WEBHOOK_RECEIVER_URL ?? "http://localhost:8096";

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

interface Received {
  id: string;
  timestamp: string;
  signature: string;
  event: string;
  environment: string;
  body: string;
}

/** Checks a Standard Webhooks signature as a receiver would. */
function signedWith(webhook: Received, secret: string): boolean {
  const key = Buffer.from(secret.replace(/^whsec_/, ""), "base64");
  const expected = `v1,${createHmac("sha256", key).update(`${webhook.id}.${webhook.timestamp}.${webhook.body}`).digest("base64")}`;
  return webhook.signature.split(" ").includes(expected);
}

test.describe("integrations", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test("an administrator connects a system with an API key and a webhook", async ({ page, request }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    await page.goto("/en/settings");
    const menu = page.getByRole("navigation", { name: "Settings sections" });
    await menu.getByRole("link", { name: "Integrations" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/integrations$/);
    await expect(page.getByTestId("integrations-environment")).toContainText("Development");
    await expectAccessible(page);

    // An API key that may only read users, shown once.
    const name = `E2E CRM ${Date.now()}`;
    await page.getByLabel("Name", { exact: true }).last().fill(name);
    await page.getByLabel("Acts as role").last().selectOption("agent");
    await page.getByRole("checkbox", { name: /See users/ }).last().check();
    await page.getByRole("button", { name: "Create integration" }).click();
    const key = (await page.getByTestId("secret-value").innerText()).trim();
    expect(key).toMatch(/^zvk_[a-z0-9]{12}_/);
    await expectAccessible(page);
    await page.getByRole("button", { name: "I have stored it" }).click();
    await expect(page.getByTestId("secret-reveal")).toHaveCount(0);

    const call = await request.get(`${apiUrl}/api/v1/users`, { headers: { "X-Api-Key": key } });
    expect(call.status()).toBe(200);
    expect(call.headers()["zelvo-environment"]).toBe("development");
    const forbidden = await request.get(`${apiUrl}/api/v1/integrations/clients`, { headers: { "X-Api-Key": key } });
    expect(forbidden.status()).toBe(403);

    // The calls are in the integration log.
    await page.getByRole("navigation", { name: "Integrations pages" }).getByRole("link", { name: "Integration log" }).click();
    // Newest first: the refused call to /integrations/clients comes before this one.
    const row = page.getByTestId("log-row").filter({ hasText: name }).filter({ hasText: "GET /api/v1/users" });
    await expect(row).toHaveCount(1);
    await expect(page.getByTestId("log-row").filter({ hasText: name }).filter({ hasText: "GET /api/v1/integrations/clients" })).toContainText("403");
    await row.getByRole("button", { name: "Details" }).click();
    await expect(row).toContainText("[masked]");
    await expectAccessible(page);

    // A webhook to the development receiver; the tester sends a signed test event.
    await page.getByRole("navigation", { name: "Integrations pages" }).getByRole("link", { name: "Webhooks" }).click();
    const receiverName = `E2E receiver ${Date.now()}`;
    await page.getByLabel("Name", { exact: true }).fill(receiverName);
    await page.getByLabel("Receiver URL").fill("http://webhook-receiver:8096/hook");
    await page.getByRole("checkbox", { name: /A user was created/ }).check();
    await page.getByRole("button", { name: "Create webhook" }).click();
    const secret = (await page.getByTestId("secret-value").innerText()).trim();
    expect(secret).toMatch(/^whsec_/);
    await page.getByRole("button", { name: "I have stored it" }).click();
    await page.getByTestId("webhook-row").filter({ hasText: receiverName }).getByRole("link", { name: /Details/ }).click();
    await page.getByRole("button", { name: "Send test event" }).click();
    await expect(page.getByTestId("webhook-test-result")).toContainText("The receiver accepted the test event");
    await expect(page.getByTestId("delivery-row").first()).toContainText("Delivered");
    await expectAccessible(page);

    const received = (await (await request.get(`${receiverUrl}/received`)).json()) as Received[];
    const test = received.find((r) => r.event === "webhook.test");
    expect(test).toBeDefined();
    expect(signedWith(test!, secret)).toBe(true);
    expect(test!.environment).toBe("development");

    // Revoked: the key is refused at once.
    await page.getByRole("navigation", { name: "Integrations pages" }).getByRole("link", { name: "API keys and OAuth clients" }).click();
    const client = page.getByTestId("api-client-row").filter({ hasText: name });
    await client.getByRole("button", { name: "Revoke" }).click();
    await client.getByRole("button", { name: "Yes, revoke it" }).click();
    await expect(page.getByText(`${name} is revoked.`)).toBeVisible();
    const refused = await request.get(`${apiUrl}/api/v1/users`, { headers: { "X-Api-Key": key } });
    expect(refused.status()).toBe(401);

    // The same screens in Arabic, right to left.
    await page.goto("/ar/settings/integrations/webhooks");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { name: "التكاملات" })).toBeVisible();
    await expectAccessible(page);
  });

  test("an administrator exports users and checks an import file", async ({ page }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await page.goto("/en/settings/import-export");
    await expect(page.getByRole("heading", { name: "Import and export" })).toBeVisible();
    await expectAccessible(page);

    const csv = await page.request.get("/bff/api/v1/bulk/users/export?format=csv");
    expect(csv.status()).toBe(200);
    expect(csv.headers()["content-disposition"]).toContain(".csv");
    expect(await csv.text()).toContain("admin@zelvo.test");

    await page.getByLabel(/CSV or Excel file/).setInputFiles({
      name: "users.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("email,status\nnot-an-email,Active\n"),
    });
    await page.getByRole("button", { name: "Check file" }).click();
    await expect(page.getByTestId("import-errors")).toContainText("Not a valid email address.");
    await expectAccessible(page);
  });
});
