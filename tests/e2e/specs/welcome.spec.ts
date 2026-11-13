import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

test.describe("welcome page", () => {
  test("opens in English by default, left to right", async ({ page }) => {
    await page.goto("/");

    await expect(page).toHaveURL(/\/en$/);
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Welcome to Zelvo");
    await expect(page.getByTestId("powered-by")).toHaveText("Powered by Zelvo");
  });

  test("opens in Arabic for an Arabic browser, right to left (LANG-4)", async ({ browser }) => {
    const context = await browser.newContext({ locale: "ar-AE" });
    const page = await context.newPage();

    await page.goto("/");

    await expect(page).toHaveURL(/\/ar$/);
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("مرحبًا بكم في زيلفو");
    await expect(page.getByTestId("powered-by")).toHaveText("بتقنية زيلفو");
    await context.close();
  });

  test("switches language and stays on the same screen (LANG-3)", async ({ page }) => {
    await page.goto("/en");

    await page.getByRole("link", { name: "Change language to العربية" }).click();
    await expect(page).toHaveURL(/\/ar$/);
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

    await page.getByRole("link", { name: "تغيير اللغة إلى English" }).click();
    await expect(page).toHaveURL(/\/en$/);
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
  });

  test("remembers the chosen language", async ({ page }) => {
    await page.goto("/ar");
    await page.goto("/");

    await expect(page).toHaveURL(/\/ar$/);
  });

  test("shows every service running", async ({ page }) => {
    await page.goto("/en");

    for (const service of ["api", "postgres", "redis", "rabbitmq", "opensearch", "keycloak"]) {
      await expect(page.locator(`[data-service="${service}"]`)).toHaveAttribute("data-status", "Healthy");
    }
    await expect(page.getByText("All services are running.")).toBeVisible();
    await expect(page.getByTestId("license-mode")).toHaveText("Development license, not for production");
  });

  test("shows Gregorian and Hijri dates", async ({ page }) => {
    await page.goto("/en");

    await expect(page.getByText("Gregorian")).toBeVisible();
    await expect(page.getByText("Hijri")).toBeVisible();
    await expect(page.getByText(/\d{4} AH/)).toBeVisible();
  });

  for (const locale of ["en", "ar"]) {
    test(`has no WCAG 2.2 AA violations in ${locale}`, async ({ page }) => {
      await page.goto(`/${locale}`);
      await expect(page.locator('[data-service="api"]')).toBeVisible();

      const results = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
        .analyze();

      expect(results.violations).toEqual([]);
    });
  }

  test("unknown pages show a translated not-found page", async ({ page }) => {
    const response = await page.goto("/ar/does-not-exist");

    expect(response?.status()).toBe(404);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("الصفحة غير موجودة");
  });
});
