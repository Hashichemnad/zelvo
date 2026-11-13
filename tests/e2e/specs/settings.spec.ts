import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Settings and localization (task A3): the Settings shell, tenant defaults for language, calendar
 * and digits (LANG-1, LANG-8), personal preferences (LANG-2) and the display language order.
 * Runs in its own Playwright project after all other tests, because it changes tenant-wide
 * defaults; every test puts back what it changed.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

/** Saves the tenant defaults through the web app's API proxy, as the signed-in admin. */
async function setTenantDefaults(page: Page, values: { defaultLanguage: string; defaultCalendar: string; defaultDigits: string }) {
  const status = await page.evaluate(async (body) => {
    const csrf = (document.querySelector("input[name=csrf]") as HTMLInputElement).value;
    const response = await fetch("/bff/api/v1/settings/localization", {
      method: "PUT",
      headers: { "content-type": "application/json", "x-csrf-token": csrf },
      body: JSON.stringify(body),
    });
    return response.status;
  }, values);
  expect(status).toBe(200);
}

const platformDefaults = { defaultLanguage: "en", defaultCalendar: "Gregorian", defaultDigits: "Western" };

test.describe("settings and localization", () => {
  test.describe.configure({ mode: "serial" });

  test("an admin sets the tenant defaults that visitors see (LANG-1, LANG-8)", async ({ page, browser }) => {
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/en\/account$/);

    try {
      await page.getByRole("link", { name: "Settings" }).click();
      await expect(page).toHaveURL(/\/en\/settings$/);
      const menu = page.getByRole("navigation", { name: "Settings sections" });
      await expect(menu.getByRole("link")).toHaveText(["Language and region", "Missing translations"]);
      await expectAccessible(page);

      await menu.getByRole("link", { name: "Language and region" }).click();
      await page.getByLabel("Default language").selectOption("ar");
      await page.getByLabel("Default calendar").selectOption("Hijri");
      await page.getByLabel("Default digits").selectOption("ArabicIndic");
      await page.getByRole("button", { name: "Save" }).click();
      await expect(page.getByRole("status")).toHaveText("The defaults are saved.");
      await expectAccessible(page);

      // A visitor whose browser asks for neither English nor Arabic gets the tenant default.
      const french = await browser.newContext({ locale: "fr-FR" });
      const visitor = await french.newPage();
      await visitor.goto("/");
      await expect(visitor).toHaveURL(/\/ar$/);
      await expect(visitor.getByTestId("today-primary")).toContainText("الهجري");
      await expect(visitor.getByTestId("today-primary")).toContainText(/[٠-٩]/);
      await french.close();

      // A visitor whose browser asks for English still gets English.
      const english = await browser.newContext({ locale: "en-GB" });
      const englishVisitor = await english.newPage();
      await englishVisitor.goto("/");
      await expect(englishVisitor).toHaveURL(/\/en$/);
      await english.close();

      // The admin's own settings page in Arabic, right to left.
      await page.goto("/ar/settings/language");
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
      await expect(page.getByLabel("اللغة الافتراضية")).toHaveValue("ar");
      await expectAccessible(page);
    } finally {
      await setTenantDefaults(page, platformDefaults);
    }
  });

  test("a requester's own language and digits win, also at the next sign-in (LANG-2, LANG-8)", async ({ page }) => {
    await signInWithPassword(page, devUser("requester"));
    await expect(page).toHaveURL(/\/en\/account$/);
    await expect(page.getByRole("link", { name: "Settings" })).toHaveCount(0);

    try {
      await page.getByLabel("Language", { exact: true }).selectOption("ar");
      await page.getByLabel("Digits", { exact: true }).selectOption("ArabicIndic");
      await expect(page.getByTestId("preview-number")).toHaveText(/[٠-٩]/);
      await page.getByRole("button", { name: "Save" }).click();

      // Saving a new language shows the same page in it.
      await expect(page).toHaveURL(/\/ar\/account$/);
      await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
      await expect(page.getByLabel("اللغة", { exact: true })).toHaveValue("ar");
      await expectAccessible(page);

      await page.goto("/ar");
      await expect(page.getByTestId("today-primary")).toContainText(/[٠-٩]/);

      // The language switch applies to this visit only...
      await page.getByRole("link", { name: "تغيير اللغة إلى English" }).click();
      await expect(page).toHaveURL(/\/en$/);
      await page.goto("/");
      await expect(page).toHaveURL(/\/en$/);

      // ...and the next sign-in opens in the profile language again.
      await page.getByRole("button", { name: "Sign out" }).click();
      await signInWithPassword(page, devUser("requester"), "en");
      await expect(page).toHaveURL(/\/ar\/account$/);
    } finally {
      await page.goto("/ar/account");
      await page.getByLabel("اللغة", { exact: true }).selectOption("default");
      await page.getByLabel("الأرقام", { exact: true }).selectOption("default");
      await page.getByRole("button", { name: "حفظ" }).click();
      await expect(page).toHaveURL(/\/en\/account$/);
    }
  });

  test("people without settings permissions are told so", async ({ page }) => {
    await signInWithPassword(page, devUser("requester"));
    await expect(page).toHaveURL(/\/en\/account$/);

    await page.goto("/en/settings");

    await expect(page.getByTestId("settings-no-access")).toBeVisible();
    await expectAccessible(page);
  });

  test("visitors are asked to sign in before Settings", async ({ page }) => {
    await page.goto("/en/settings/language");

    await expect(page).toHaveURL(/\/realms\/zelvo-[a-z0-9-]+\/protocol\/openid-connect\/auth/);
  });
});
