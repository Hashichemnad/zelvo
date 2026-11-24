import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * SLA management (task B3, module 8, GOV-7): an administrator adds an around-the-clock calendar
 * with a holiday, and an SLA policy for a new category; the policy tester shows the targets; a
 * requester reports an incident in the category and the agent sees its response and resolution
 * clocks; starting work meets the response target. The new Settings pages are checked for
 * accessibility in English and Arabic. Runs in its own Playwright project because it changes
 * tenant-wide SLA set-up; the policy (and the calendar, unless it became the default) are removed.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

/** Server-rendered buttons do nothing until the page is interactive: click until the item is gone. */
async function clickUntilGone(page: Page, name: string) {
  const button = page.getByRole("button", { name });
  await expect(async () => {
    if ((await button.count()) > 0) await button.first().click();
    await expect(button).toHaveCount(0, { timeout: 2_000 });
  }).toPass({ timeout: 30_000 });
}

test.describe("SLA management", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);

  test("an incident in a category with an SLA policy gets clocks, and starting work meets the response", async ({ page, browser }) => {
    const stamp = Date.now();
    const category = `E2E payroll ${stamp}`;
    const calendarName = `E2E around the clock ${stamp}`;
    const policyName = `E2E payroll SLA ${stamp}`;
    const title = `Payslips are not generated ${stamp}`;

    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    for (const locale of ["en", "ar"]) {
      for (const section of ["sla-policies", "business-calendars", "customer-groups"]) {
        await page.goto(`/${locale}/settings/${section}`);
        await expect(page.locator("main").getByRole("heading", { level: 2 }).first()).toBeVisible();
        await expectAccessible(page);
      }
    }
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

    // A category for the policy.
    await page.goto("/en/settings/categories");
    await page.getByRole("button", { name: "Add a top-level category" }).click();
    const addCategory = page.getByRole("form", { name: "Add a top-level category" });
    await addCategory.getByLabel("In English", { exact: true }).fill(category);
    await addCategory.getByLabel("In Arabic", { exact: true }).fill(`الرواتب ${stamp}`);
    await addCategory.getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("tree-categories").getByText(category, { exact: true })).toBeVisible();

    // An around-the-clock calendar with a holiday.
    await page.goto("/en/settings/business-calendars");
    await page.getByRole("button", { name: "Add a calendar" }).click();
    const calendarForm = page.getByTestId("calendar-form");
    await calendarForm.getByLabel("In English", { exact: true }).fill(calendarName);
    await calendarForm.getByLabel("In Arabic", { exact: true }).fill(`على مدار الساعة ${stamp}`);
    await calendarForm.getByRole("button", { name: "Around the clock" }).click();
    await expectAccessible(page);
    await calendarForm.getByRole("button", { name: "Save calendar" }).click();
    const calendar = page.getByTestId("calendars").getByRole("listitem").filter({ hasText: calendarName });
    await expect(calendar).toBeVisible();
    const holiday = calendar.getByRole("form", { name: "Add a holiday" });
    await holiday.getByLabel("In English", { exact: true }).fill("National Day");
    await holiday.getByLabel("In Arabic", { exact: true }).fill("عيد الاتحاد");
    await holiday.getByLabel("First day", { exact: true }).fill("2030-12-02");
    await holiday.getByLabel("Last day (optional)", { exact: true }).fill("2030-12-03");
    await holiday.getByRole("button", { name: "Add a holiday" }).click();
    await expect(calendar.getByText("National Day", { exact: true })).toBeVisible();
    const becameDefault = (await calendar.getByText("Default", { exact: true }).count()) > 0;

    // A policy for the category on that calendar: P3 responds in 2 h and resolves in 8 h.
    await page.goto("/en/settings/sla-policies");
    await page.getByRole("button", { name: "Add a policy" }).click();
    const policy = page.getByTestId("policy-form");
    await policy.getByLabel("In English", { exact: true }).fill(policyName);
    await policy.getByLabel("Category", { exact: true }).selectOption({ label: category });
    await policy.getByLabel("Business hours", { exact: true }).selectOption({ label: calendarName });
    await policy.getByLabel(/^Response hours for P3/).fill("2");
    await policy.getByLabel(/^Resolution hours for P3/).fill("8");
    await expectAccessible(page);
    await policy.getByRole("button", { name: "Save policy" }).click();
    await expect(page.getByTestId("sla-policies")).toContainText(policyName);
    // Policies are tried in order: move it first (buttons are disabled while an order change saves).
    const moveUp = page.getByRole("button", { name: `Move ${policyName} up` });
    const first = page.getByTestId("sla-policies").getByRole("listitem").first();
    await expect(async () => {
      if (!(await first.textContent())?.includes(policyName) && (await moveUp.isEnabled())) await moveUp.click();
      await expect(first).toContainText(policyName, { timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
    await page.getByLabel("Category", { exact: true }).last().selectOption({ label: category });
    await page.getByRole("button", { name: "Try", exact: true }).click();
    await expect(page.getByTestId("sla-test-result")).toContainText(`Policy ${policyName}, business hours: ${calendarName}.`);
    await expect(page.getByTestId("sla-test-result")).toContainText("Response within 2 hr");

    // The requester reports an incident in the category.
    const requesterContext = await browser.newContext();
    const requester = await requesterContext.newPage();
    await signInWithPassword(requester, devUser("requester"));
    await requester.goto("/en/my-incidents/new");
    await requester.getByLabel("Title", { exact: true }).fill(title);
    await requester.getByLabel("Category", { exact: true }).selectOption({ label: category });
    await requester.getByRole("button", { name: "Report incident" }).click();
    await expect(requester.getByRole("heading", { level: 1, name: title })).toBeVisible();
    const incidentId = requester.url().split("/").pop()!;
    // Requesters do not see SLA clocks.
    await expect(requester.getByTestId("sla-clocks")).toHaveCount(0);
    await requesterContext.close();

    // The agent sees both clocks, in English and Arabic.
    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await agent.goto(`/en/incidents/${incidentId}`);
    const clocks = agent.getByTestId("sla-clocks");
    await expect(clocks.getByText("Response: On track")).toBeVisible();
    await expect(clocks.getByText("Resolution: On track")).toBeVisible();
    await expect(clocks.getByText(`Business hours: ${calendarName}`).first()).toBeVisible();
    await expectAccessible(agent);
    await agent.goto(`/ar/incidents/${incidentId}`);
    await expect(agent.getByTestId("sla-clocks").getByText("الاستجابة: ضمن المسار")).toBeVisible();
    await expectAccessible(agent);
    await agent.goto("/en/incidents?sla=atRisk&open=true&sort=due");
    await expectAccessible(agent);
    await agentContext.close();

    // Starting work meets the response target (as the admin, who may assign and work on it).
    await page.goto(`/en/incidents/${incidentId}`);
    const picker = page.getByTestId("assignment-picker").getByLabel("Assignee", { exact: true });
    await expect(async () => {
      await picker.selectOption({ index: 1 });
      await page.getByTestId("assignment-picker").getByRole("button").first().click();
      await expect(page.getByText("Assigned", { exact: true }).first()).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 30_000 });
    await page.getByRole("button", { name: "Start work" }).click();
    await expect(page.getByText("In progress", { exact: true }).first()).toBeVisible();
    await expect(async () => {
      await page.reload();
      await expect(page.getByTestId("sla-clocks").getByText("Response: Met")).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 30_000 });

    // Put things back.
    await page.goto("/en/settings/sla-policies");
    await clickUntilGone(page, `Delete ${policyName}`);
    if (!becameDefault) {
      await page.goto("/en/settings/business-calendars");
      await clickUntilGone(page, `Delete ${calendarName}`);
    }
  });
});
