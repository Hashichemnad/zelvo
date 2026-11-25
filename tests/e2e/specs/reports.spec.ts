import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Reporting (module 10, task F2), against `make up`: the executive opens every dashboard in
 * English and Arabic, filters one, downloads its CSV and schedules it; the team lead sees the
 * groups they lead; the agent sees only "My performance"; the requester has no reports. Screens
 * are checked for accessibility in both languages.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("reports", () => {
  test.setTimeout(180_000);

  test("the executive reads, filters, exports and schedules the dashboards", async ({ page }) => {
    await signInWithPassword(page, devUser("executive"));
    for (const locale of ["en", "ar"] as const) {
      await page.goto(`/${locale}/reports`);
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expect(page.getByTestId("report-kpis")).toBeVisible();
      await expectAccessible(page);
      for (const slug of ["volumes", "backlog", "sla", "agents", "happiness"]) {
        await page.goto(`/${locale}/reports/${slug}`);
        await expect(page.getByTestId("report-filters")).toBeVisible();
        await expectAccessible(page);
      }
    }

    await page.goto("/en/reports/volumes");
    await expect(page.getByTestId("report-scope")).toHaveText("You see the whole organisation.");
    await page.getByRole("checkbox", { name: "Incidents" }).check();
    await page.getByRole("button", { name: "Show", exact: true }).click();
    await expect(page).toHaveURL(/types=Incident/);
    await expect(page.getByRole("checkbox", { name: "Incidents" })).toBeChecked();
    const csv = await page.request.get((await page.getByTestId("report-csv").getAttribute("href"))!);
    expect(csv.ok()).toBe(true);
    expect(await csv.text()).toContain("period,created,resolved,closed");

    const name = `Weekly volumes ${Date.now()}`;
    await page.getByTestId("report-schedule").click();
    await expect(page).toHaveURL(/\/en\/reports\/schedules\/new\?report=Volumes/);
    await expectAccessible(page);
    await page.getByLabel("Name").fill(name);
    await page.getByRole("button", { name: "Schedule the report" }).click();
    await expect(page).toHaveURL(/\/en\/reports\/schedules$/);
    await expect(page.getByTestId("schedule-table").getByText(name)).toBeVisible();
    await expectAccessible(page);
    await page.getByRole("link", { name }).click();
    await expect(page.getByTestId("delivery-log").or(page.getByText("Nothing sent yet."))).toBeVisible();
    await page.goto("/ar/reports/schedules");
    await expectAccessible(page);
  });

  test("the team lead sees the groups they lead and the agent their own figures", async ({ browser }) => {
    const leadContext = await browser.newContext();
    const lead = await leadContext.newPage();
    await signInWithPassword(lead, devUser("lead"));
    await completeSecondFactor(lead, devUser("lead"));
    await lead.goto("/en/reports/agents");
    await expect(lead.getByTestId("report-scope")).toContainText(/You see (the groups you lead|your own figures)/);
    await expectAccessible(lead);
    await leadContext.close();

    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await expect(agent.getByTestId("nav-reports")).toHaveText("My performance");
    await agent.goto("/en/reports");
    await expect(agent.getByRole("heading", { level: 1, name: "My performance" })).toBeVisible();
    await expect(agent.getByTestId("report-scope")).toHaveText("You see your own figures.");
    await expectAccessible(agent);
    await agent.goto("/ar/reports");
    await expect(agent.getByRole("heading", { level: 1, name: "أدائي" })).toBeVisible();
    await expectAccessible(agent);
    // Other dashboards lead back to "My performance".
    await agent.goto("/en/reports/sla");
    await expect(agent).toHaveURL(/\/en\/reports$/);
    await agentContext.close();
  });

  test("requesters have no reports", async ({ page }) => {
    await signInWithPassword(page, devUser("requester"));
    await expect(page.getByTestId("nav-reports")).toHaveCount(0);
    await page.goto("/en/reports");
    await expect(page.getByTestId("reports-no-access")).toBeVisible();
  });
});
