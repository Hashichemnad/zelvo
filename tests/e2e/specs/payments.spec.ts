import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

// Task E8: Settings → Payments and finance's Payments page, in both languages and accessible.
// Paying itself goes through the development payment-mock; it is tried in the demo walkthrough.
test.describe("online payments", () => {
  test.setTimeout(120_000);

  test("administrators open Settings → Payments and the finance page in English and Arabic", async ({ page }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/settings/payments`);
      await expect(page.getByTestId("payment-settings")).toBeVisible();
      await expectAccessible(page);
      await page.goto(`/${locale}/payments`);
      await expect(page.getByTestId("payments-finance")).toBeVisible();
      await expectAccessible(page);
    }
    await page.goto("/en/settings/payments");
    await page.getByRole("button", { name: "Connect a gateway" }).click();
    await expect(page.getByTestId("gateway-editor")).toBeVisible();
    await expect(page.getByTestId("gateway-operation")).toHaveCount(2);
  });
});
