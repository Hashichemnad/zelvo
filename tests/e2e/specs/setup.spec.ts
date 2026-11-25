import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * The setup wizard (module 10, task F2), against `make up`: the administrator sees the reminder
 * on the Settings home, walks the steps, creates a facilities department from its template with
 * its own names, and finishes the wizard. Screens are checked for accessibility in English and
 * Arabic. Runs on its own: it creates a department and marks the tenant's setup steps.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("setup wizard", () => {
  test.setTimeout(180_000);

  test("an administrator walks the steps and creates a department from a template", async ({ page }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));

    await page.goto("/en/setup");
    const reopen = page.getByTestId("setup-reopen");
    if (await reopen.isVisible()) await reopen.click();
    await page.goto("/en/settings");
    await expect(page.getByTestId("setup-banner")).toBeVisible();
    await page.getByRole("link", { name: "Open the setup wizard" }).click();
    await expect(page).toHaveURL(/\/en\/setup$/);
    await expectAccessible(page);

    // Steps can be done in any order; each says what to do and links to its settings.
    await page.getByRole("navigation", { name: "Setup steps" }).getByRole("button", { name: /^Business calendar/ }).click();
    await expect(page.getByRole("heading", { level: 2, name: "Business calendar" })).toBeVisible();
    await expect(page.getByTestId("setup-step-link")).toHaveAttribute("href", "/en/settings/business-calendars");
    // A step left done or skipped by an earlier run is set back to do first.
    const toDo = page.getByRole("button", { name: "Mark as to do" });
    if (await toDo.isVisible()) await toDo.click();
    await page.getByTestId("setup-skip").click();
    await expect(page.getByRole("navigation", { name: "Setup steps" }).getByRole("button", { name: /Business calendar\s*:\s*Skipped/ })).toBeVisible();

    const stamp = Date.now();
    await page.getByRole("navigation", { name: "Setup steps" }).getByRole("button", { name: /^Departments and service desks/ }).click();
    await page.getByRole("radio", { name: "Facilities" }).check();
    await expect(page.getByTestId("template-preview")).toContainText("Building maintenance");
    await expectAccessible(page);
    await page.getByRole("group", { name: "Department name" }).getByLabel("English").fill(`Facilities ${stamp}`);
    await page.getByTestId("apply-template").click();
    await expect(page.getByTestId("template-result")).toContainText("2 groups");

    await page.goto("/en/settings/departments");
    await expect(page.getByRole("cell", { name: `Facilities ${stamp}`, exact: true })).toBeVisible();

    await page.goto("/ar/setup");
    await expect(page.getByRole("heading", { level: 1, name: "معالج الإعداد" })).toBeVisible();
    await expectAccessible(page);
    await page.goto("/en/setup");
    await page.getByTestId("setup-finish").click();
    await expect(page.getByTestId("setup-finished")).toBeVisible();
    await page.goto("/en/settings");
    await expect(page.getByTestId("setup-banner")).toHaveCount(0);
  });
});
