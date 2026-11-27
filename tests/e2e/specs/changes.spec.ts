import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Task H3 (module 17): change management against the demo installation (`make demo`, then
 * `make e2e-demo`). The government entity's team lead opens the change calendar, then the sample
 * change that moves the transactions system: high risk, planned and assessed. Submitting it starts
 * the high-risk approval path; the lead, who chairs the change advisory board (task H4), takes it
 * up at the week's meeting, votes and records the board's decision. Runs after the accessibility
 * audit (project demo-changes), which reads the demo's changes as seeded.
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
    const number = (await page.locator("header p.font-mono").first().textContent())?.trim() ?? "";

    // Task H4: the team lead chairs the board. The week's meeting takes the change on its agenda;
    // the members' votes are advice and the chair's decision approves the board's level.
    await page.goto("/ar/changes/board");
    await expect(page.getByTestId("waiting-changes")).toContainText(number);
    await page.getByTestId("meeting-table").getByRole("link").first().click();
    await page.getByRole("button", { name: "تحديث جدول الأعمال" }).click();
    const item = page.getByTestId("agenda-item").filter({ hasText: number });
    await expect(item).toBeVisible();
    expect((await new AxeBuilder({ page }).withTags(wcag).analyze()).violations).toEqual([]);
    await item.getByTestId("vote-form").getByRole("radio", { name: "موافقة" }).check();
    await item.getByRole("button", { name: "حفظ صوتي" }).click();
    await expect(page.getByTestId("agenda-item").filter({ hasText: number })).toContainText("1 موافقة");
    await page.getByTestId("agenda-item").filter({ hasText: number }).getByRole("button", { name: "تسجيل القرار" }).click();
    await expect(page.getByTestId("agenda-item").filter({ hasText: number })).toContainText("اعتمده المجلس");
  });
});
