import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Demo mode (task D), against the demo installation (`make demo`, then `make e2e-demo`): the demo
 * panel, one-click switching between demo users and sample organisations, and "Reset demo".
 * Runs as its own Playwright project (E2E_DEMO=1), one test after another, because it resets the demo.
 */

const apiUrl = process.env.API_URL ?? "http://localhost:8080";

async function openPanel(page: Page) {
  const summary = page.locator("summary", { has: page.locator("#demo-panel-heading") });
  await summary.click();
  return page.getByRole("region", { name: /Demo|العرض التوضيحي/ });
}

async function lastResetAt(tenant: string): Promise<string | null> {
  const response = await fetch(`${apiUrl}/api/v1/demo`, { headers: { "X-Zelvo-Tenant": tenant } });
  return ((await response.json()) as { lastResetAt: string | null }).lastResetAt;
}

test.describe.configure({ mode: "serial" });

test.describe("demo mode", () => {
  test("visitors see the demo panel with one demo user per persona, accessible in both languages", async ({ page }) => {
    await page.goto("/en");

    const panel = await openPanel(page);
    await expect(panel.getByRole("button", { name: /Agent\s*Hamad Al Ketbi/ })).toBeVisible();
    await expect(panel.getByRole("button")).toHaveCount(7); // six personas and the other organisation
    expect((await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze()).violations).toEqual([]);

    await page.goto("/ar");
    const arabic = await openPanel(page);
    await expect(arabic.getByRole("button", { name: /موظف الدعم/ })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    expect((await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze()).violations).toEqual([]);
  });

  test("one click signs in as a demo agent, second factor included, and another click switches to the admin", async ({ page }) => {
    await page.goto("/en");
    await (await openPanel(page)).getByRole("button", { name: /Agent\s*Hamad Al Ketbi/ }).click();

    // No sign-in form: back on Zelvo, signed in, in the organisation's default language (Arabic).
    await expect(page).toHaveURL(/localhost:3000\/ar$/);
    await expect(page.getByRole("link", { name: /Hamad Al Ketbi/ })).toBeVisible();
    await page.goto("/en/account");
    await expect(page.getByText("agent@municipality.example")).toBeVisible();

    await (await openPanel(page)).getByRole("button", { name: /Tenant administrator\s*Fatima Al Hammadi/ }).click();
    await expect(page.getByRole("link", { name: /Fatima Al Hammadi/ })).toBeVisible();
    // Staff roles need a second factor; the demo sign-in counts as one, so Settings opens.
    await page.goto("/en/settings/language");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page).toHaveURL(/\/en\/settings\/language$/);
  });

  test("the admin resets the demo after confirming", async ({ page }) => {
    // Resetting empties and reseeds both sample organisations (six months of sample data since F2).
    test.setTimeout(240_000);
    await page.goto("/en");
    await (await openPanel(page)).getByRole("button", { name: /Tenant administrator\s*Fatima Al Hammadi/ }).click();
    await expect(page.getByRole("link", { name: /Fatima Al Hammadi/ })).toBeVisible();
    const before = await lastResetAt("demo-company");

    await page.goto("/en");
    const panel = await openPanel(page);
    await panel.getByRole("button", { name: "Reset demo" }).click();
    await expect(panel.getByText(/The audit log is kept/)).toBeVisible();
    await panel.getByRole("button", { name: "Reset now" }).click();

    await expect.poll(() => lastResetAt("demo-company"), { timeout: 180_000 }).not.toBe(before);
    // Still signed in after the reset.
    await expect(page.getByRole("link", { name: /Fatima Al Hammadi/ })).toBeVisible();
  });

  test("requesters do not see the reset, and the panel opens the other sample organisation", async ({ page }) => {
    await page.goto("/en");
    await (await openPanel(page)).getByRole("button", { name: /Requester\s*Mariam Al Suwaidi/ }).click();
    await expect(page.getByRole("link", { name: /Mariam Al Suwaidi/ })).toBeVisible();
    await page.goto("/en");
    let panel = await openPanel(page);
    await expect(panel.getByRole("button", { name: "Reset demo" })).toHaveCount(0);

    await panel.getByRole("button", { name: /Gulf Horizon Trading/ }).click();
    await expect(page).toHaveURL(/localhost:3000\/en$/);
    // A visitor of the company now: its own demo users.
    await expect(page.getByRole("link", { name: /Mariam Al Suwaidi/ })).toHaveCount(0);
    panel = await openPanel(page);
    await panel.getByRole("button", { name: /Agent\s*Daniel Mathew/ }).click();
    await expect(page.getByRole("link", { name: /Daniel Mathew/ })).toBeVisible();
    await expect(page).toHaveURL(/\/en$/);
  });
});
