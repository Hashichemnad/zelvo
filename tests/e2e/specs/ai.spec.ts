import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Settings → AI (task E4, module 9), against `make up`: an administrator connects the development
 * AI mock as an OpenAI-compatible provider, tests it, turns a feature on with a model, sets a
 * price and a monthly limit, and finds the test call in the AI log and in Usage and cost; the
 * pages pass the accessibility checks in English and Arabic. Runs in its own project because it
 * signs in as the administrator.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const mockUrl = process.env.AI_MOCK_URL ?? "http://localhost:8097";

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("AI settings", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(120_000);

  test("an administrator connects a provider, tests it and turns a feature on", async ({ page, request }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    await page.goto("/en/settings");
    await page.getByRole("navigation", { name: "Settings sections" }).getByRole("link", { name: "AI", exact: true }).click();
    await expect(page).toHaveURL(/\/en\/settings\/ai$/);
    await expectAccessible(page);

    // Connect the development mock; it needs no key.
    const name = `E2E mock ${Date.now()}`;
    await page.getByLabel("Provider", { exact: true }).selectOption("OpenAICompatible");
    await page.getByLabel("Name", { exact: true }).last().fill(name);
    await page.getByLabel("Endpoint", { exact: true }).last().fill("http://ai-mock:8097/v1");
    await page.getByRole("button", { name: "Connect" }).click();
    const card = page.getByTestId("ai-provider").filter({ hasText: name });
    await expect(card).toHaveCount(1);

    // The test button reaches the model.
    await card.getByLabel("Model to test").fill("zelvo-mock-fast");
    await card.getByRole("button", { name: "Test" }).click();
    await expect(card.getByTestId("ai-test-result")).toContainText("The model answered");
    const received = (await (await request.get(`${mockUrl}/received`)).json()) as { body: string }[];
    expect(received[0]!.body).toContain("ZELVO-DATA-");
    await expectAccessible(page);

    // A feature with a provider and model.
    const tabs = page.getByRole("navigation", { name: "AI pages" });
    await tabs.getByRole("link", { name: "Features" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/ai\/features$/);
    const summary = page.getByTestId("ai-features-panel").getByTestId("ai-feature-summary");
    await summary.getByRole("checkbox", { name: "On" }).check();
    await summary.getByLabel("Provider").selectOption({ label: name });
    await summary.getByLabel("Model").fill("zelvo-mock-strong");
    await summary.getByRole("button", { name: /Save/ }).click();
    await expect(summary.getByRole("status")).toHaveText("Saved.");
    await expectAccessible(page);

    // A price and a monthly limit.
    await tabs.getByRole("link", { name: "Prices" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/ai\/prices$/);
    const prices = page.getByTestId("ai-prices-panel");
    await prices.getByLabel("Provider").selectOption({ label: name });
    await prices.getByLabel("Model").fill("zelvo-mock-fast");
    await prices.getByLabel("Input, USD per million tokens").fill("1");
    await prices.getByLabel("Output, USD per million tokens").fill("5");
    await prices.getByRole("button", { name: "Save price" }).click();
    await expect(prices.getByTestId("ai-price-row").filter({ hasText: "zelvo-mock-fast" }).first()).toBeVisible();
    await expectAccessible(page);

    await tabs.getByRole("link", { name: "Usage and cost" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/ai\/usage$/);
    const usage = page.getByTestId("ai-usage-panel");
    await usage.getByLabel("Limit in US dollars").fill("250");
    await usage.getByRole("button", { name: "Save limit" }).click();
    await expect(page.getByTestId("ai-budget")).toContainText("$250.00");
    await expect(page.getByTestId("ai-usage-totals")).toBeVisible();
    await expectAccessible(page);

    // The test call is in the AI log.
    await tabs.getByRole("link", { name: "AI log" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/ai\/log$/);
    const row = page.getByTestId("ai-log").getByTestId("ai-log-row").filter({ hasText: "Connection test" }).first();
    await expect(row).toContainText("Succeeded");
    await row.getByRole("button", { name: "Details" }).click();
    await expect(row).toContainText("Sent (personal data masked)");
    await expectAccessible(page);

    // Arabic, right to left.
    await page.goto("/ar/settings/ai/usage");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { name: "الذكاء الاصطناعي", level: 2 })).toBeVisible();
    await expectAccessible(page);
  });
});
