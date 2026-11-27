import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

/**
 * Task H1 (module 16): problem management against the demo installation (`make demo`, then
 * `make e2e-demo`), whose government entity has a problem raised from the e-services portal's
 * outage with a known error. An agent opens it from the header, reads the analysis and the
 * known error, then sees the workaround on one of its incidents in the workspace and pastes it
 * into the reply (not sent). Runs after the demo tests (project demo-problems).
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];
const tenant = process.env.E2E_DEMO_TENANT ?? "demo-municipality";

async function signInAs(page: Page, persona: "agent" | "lead", baseURL: string) {
  const response = await page.request.post("/auth/demo", { form: { tenant, locale: "ar", persona }, headers: { Origin: baseURL } });
  expect(response.ok(), `demo sign-in as ${persona}`).toBeTruthy();
}

test.describe.configure({ mode: "serial" });

test.describe("Problems", () => {
  test.skip(tenant !== "demo-municipality", "The journey uses the government entity's sample problem.");

  test("an agent reads a known error and pastes its workaround into an incident's reply", async ({ page, baseURL }) => {
    await signInAs(page, "agent", baseURL ?? "http://localhost:3000");
    await page.goto("/ar");
    await page.getByTestId("nav-problems").click();
    await expect(page).toHaveURL(/\/ar\/problems$/);
    await page.getByTestId("problem-table").getByRole("link").filter({ hasText: "PRB-" }).last().click();
    await expect(page.getByRole("heading", { level: 1, name: "انقطاعات متكررة في بوابة الخدمات الإلكترونية" })).toBeVisible();
    await expect(page.getByTestId("problem-facts")).toContainText("خطأ معروف");
    await expect(page.getByTestId("fishbone-list")).toContainText("التقنية");
    await expect(page.getByTestId("known-error")).toContainText("التطبيق الذكي");
    expect((await new AxeBuilder({ page }).withTags(wcag).analyze()).violations).toEqual([]);

    // One of its incidents in the workspace: the workaround goes into the reply.
    const href = await page.getByTestId("problem-incidents").getByRole("link").first().getAttribute("href");
    const incidentId = href?.split("/").pop() ?? "";
    await page.goto(`/ar/workspace/${incidentId}`);
    const panel = page.getByTestId("incident-known-errors");
    await expect(panel).toContainText("مشكلة مرتبطة");
    await panel.getByRole("button", { name: "إدراج في الرد (العربية)" }).click();
    await expect(page.getByTestId("composer-body")).toHaveValue(/التطبيق الذكي/);
  });
});
