import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

// Task F5: Settings → Privacy, privacy requests, Account → Privacy and the public notice page, in
// both languages and accessible. Nothing is published here: a published notice would ask every
// other test's visitors to accept it.
test.describe("privacy (UAE PDPL)", () => {
  test.setTimeout(120_000);

  test("administrators open Settings → Privacy and the privacy requests in English and Arabic", async ({ page }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/settings/privacy`);
      await expect(page.getByTestId("privacy-notice-editor")).toBeVisible();
      await expect(page.getByTestId("privacy-purposes")).toBeVisible();
      await expect(page.getByTestId("privacy-retention")).toBeVisible();
      await expectAccessible(page);
      await page.goto(`/${locale}/privacy-requests`);
      await expect(page.getByTestId("log-privacy-request")).toBeVisible();
      await expectAccessible(page);
    }
    await page.goto("/en/settings/privacy");
    await page.getByRole("button", { name: "Preview" }).nth(1).click();
    await expect(page.getByTestId("retention-preview")).toBeVisible();
  });

  test("people open Account → Privacy and visitors read the notice page", async ({ page }) => {
    await page.goto("/en/privacy");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expectAccessible(page);
    await signInWithPassword(page, devUser("requester"));
    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/account/privacy`);
      await expect(page.getByTestId("my-privacy")).toBeVisible();
      await expect(page.getByTestId("download-my-data")).toHaveAttribute("href", "/bff/api/v1/me/privacy/export");
      await expectAccessible(page);
    }
  });
});
