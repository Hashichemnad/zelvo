import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Form builder (task C1, module 2), against `make up`: an administrator builds a form for a new
 * category with a field and a rule (show "Monitor size" only when "Add a monitor" is ticked),
 * previews it on mobile in Arabic, publishes it and binds it to the category; a requester reports
 * an incident in that category, the rule works as they type, the server checks the answers, and
 * the answers appear on the incident. The new screens are checked for accessibility in English
 * and Arabic. Runs in its own project: it signs in as the administrator and adds a category.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("form builder", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);

  test("an administrator builds and publishes a category form and a requester fills it", async ({ page, browser }) => {
    const stamp = Date.now();
    const category = `E2E monitors ${stamp}`;
    const title = `I need a second screen ${stamp}`;

    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    // The new Settings pages, in both languages.
    for (const locale of ["en", "ar"]) {
      for (const section of ["forms", "field-sets", "departments"]) {
        await page.goto(`/${locale}/settings/${section}`);
        await expect(page.locator("main").getByRole("heading", { level: 2 }).first()).toBeVisible();
        await expectAccessible(page);
      }
    }

    // A category for the form.
    await page.goto("/en/settings/categories");
    await page.getByRole("button", { name: "Add a top-level category" }).click();
    const addCategory = page.getByRole("form", { name: "Add a top-level category" });
    await addCategory.getByLabel("In English", { exact: true }).fill(category);
    await addCategory.getByLabel("In Arabic", { exact: true }).fill(`الشاشات ${stamp}`);
    await addCategory.getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("tree-categories").getByText(category, { exact: true })).toBeVisible();

    // A new form, built with the palette's buttons.
    await page.goto("/en/settings/forms");
    const create = page.getByRole("form", { name: "Create a form" });
    await create.getByLabel("Key").fill(`monitors-${stamp}`);
    await create.getByLabel("In English").fill(`Monitor request ${stamp}`);
    await create.getByLabel("In Arabic").fill(`طلب شاشة ${stamp}`);
    await create.getByRole("button", { name: "Create a form" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/forms\/[0-9a-f-]{36}$/);
    await expectAccessible(page);

    const properties = page.locator("section", { has: page.getByRole("heading", { name: "Properties" }) });
    await page.getByRole("button", { name: "Add Checkbox" }).click();
    await properties.getByLabel("Key").fill("addMonitor");
    await properties.getByLabel("In English").first().fill("Add a monitor");
    await properties.getByLabel("In Arabic").first().fill("إضافة شاشة");
    await page.getByRole("button", { name: "Add Dropdown" }).click();
    await properties.getByLabel("Key").fill("monitorSize");
    await properties.getByLabel("In English").first().fill("Monitor size");
    await properties.getByLabel("In Arabic").first().fill("حجم الشاشة");
    await properties.getByLabel("English label of option 1").fill("27 inch");
    await properties.getByLabel("Arabic label of option 1").fill("27 بوصة");
    await properties.getByRole("button", { name: "Add a rule" }).click();
    await properties.getByRole("button", { name: "Add a rule" }).click();
    await properties.getByRole("group", { name: "Rule 2" }).getByLabel("What happens").selectOption("Require");
    await expectAccessible(page);
    await page.getByRole("button", { name: "Save draft" }).click();
    await expect(page.getByText("Draft saved.")).toBeVisible();

    // Preview on mobile, in Arabic: the rule works there too.
    await page.getByRole("button", { name: "Preview" }).click();
    await page.getByRole("button", { name: "Mobile" }).click();
    await page.getByRole("button", { name: "العربية" }).click();
    const preview = page.getByTestId("preview-mobile");
    await expect(preview.getByLabel("حجم الشاشة")).toHaveCount(0);
    await preview.getByLabel("إضافة شاشة").check();
    await expect(preview.getByLabel(/حجم الشاشة/)).toBeVisible();
    await expectAccessible(page);

    await page.getByRole("button", { name: "Publish" }).click();
    await expect(page.getByText("Published. New tickets use this version.")).toBeVisible();

    // Use it for the category.
    await page.goto("/en/settings/forms");
    const bind = page.getByRole("form", { name: "Use this form" });
    await bind.getByLabel("Category").selectOption({ label: category });
    await bind.getByLabel("Form").selectOption({ label: `Monitor request ${stamp}` });
    await bind.getByRole("button", { name: "Use this form" }).click();
    await expect(page.getByRole("cell", { name: category })).toBeVisible();

    // The requester fills the form; the rule shows and requires the size.
    const requesterContext = await browser.newContext();
    const requester = await requesterContext.newPage();
    await signInWithPassword(requester, devUser("requester"));
    await requester.goto("/en/my-incidents/new");
    await requester.getByLabel("Title", { exact: true }).fill(title);
    await requester.getByLabel("Category", { exact: true }).selectOption({ label: category });
    await expect(requester.getByRole("heading", { name: `Monitor request ${stamp}` })).toBeVisible();
    await expect(requester.getByLabel(/Monitor size/)).toHaveCount(0);
    await requester.getByLabel("Add a monitor").check();
    await requester.getByRole("button", { name: "Report incident" }).click();
    await expect(requester.getByText("This field is required.")).toBeVisible();
    await expectAccessible(requester);
    await requester.getByLabel(/Monitor size/).selectOption({ label: "27 inch" });
    await requester.getByRole("button", { name: "Report incident" }).click();
    await expect(requester.getByRole("heading", { level: 1, name: title })).toBeVisible();

    // The answers are on the incident, in both languages.
    const answers = requester.getByRole("region", { name: /Answers to/ });
    await expect(answers.getByText("27 inch")).toBeVisible();
    await expectAccessible(requester);
    await requester.goto(requester.url().replace("/en/", "/ar/"));
    await expect(requester.getByText("27 بوصة")).toBeVisible();
    await expectAccessible(requester);
    await requesterContext.close();
  });
});
