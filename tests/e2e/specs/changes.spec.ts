import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Task H3 (module 17): change management against the demo installation (`make demo`, then
 * `make e2e-demo`). The government entity's team lead opens the change calendar, then the sample
 * change that moves the transactions system: high risk, planned and assessed. Submitting it starts
 * the high-risk approval path. Runs after the accessibility audit (project demo-changes), which
 * reads the demo's changes as seeded.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const tenant = process.env.E2E_DEMO_TENANT ?? "demo-municipality";

async function signInAs(page: Page, persona: "agent" | "lead", baseURL: string) {
  const response = await page.request.post("/auth/demo", { form: { tenant, locale: "ar", persona }, headers: { Origin: baseURL } });
  expect(response.ok(), `demo sign-in as ${persona}`).toBeTruthy();
}

test.describe.configure({ mode: "serial" });

test.describe("Changes", () => {
  test.skip(tenant !== "demo-municipality", "The journey uses the government entity's sample changes.");

  test("a team lead reads the calendar and submits a high-risk change to its approval path", async ({ page, baseURL }) => {
    await signInAs(page, "lead", baseURL ?? "http://localhost:3000");
    await page.goto("/ar");
    await page.getByTestId("nav-changes").click();
    await expect(page).toHaveURL(/\/ar\/changes$/);
    await page.getByRole("link", { name: "تقويم التغييرات" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "تقويم التغييرات" })).toBeVisible();
    expect((await new AxeBuilder({ page }).withTags(wcag).analyze()).violations).toEqual([]);

    await page.goto("/ar/changes?riskLevel=High&stage=New");
    await page.getByTestId("change-table").getByRole("link").filter({ hasText: "CHG-" }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: "نقل نظام إدارة المعاملات إلى مركز البيانات الجديد" })).toBeVisible();
    await expect(page.getByTestId("change-risk")).toContainText("مرتفعة");
    await expect(page.getByTestId("change-plan")).toContainText("خطة التراجع");
    expect((await new AxeBuilder({ page }).withTags(wcag).analyze()).violations).toEqual([]);

    await page.getByTestId("change-work").getByRole("button", { name: "تقديم للاعتماد" }).click();
    await expect(page.getByTestId("change-facts")).toContainText("بانتظار الاعتماد");
  });
});
