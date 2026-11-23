import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Directory and assignment (task B2, modules 1 and 10): an administrator builds a category, a
 * group with the development agent and a round-robin rule for that category; a requester reports
 * an incident in the category and it lands with the agent. The new Settings pages are checked for
 * accessibility in English and Arabic. Runs in its own Playwright project because it adds
 * tenant-wide rules; the rule is removed again at the end.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

const directoryPages = ["users", "roles", "groups", "categories", "locations", "skills", "assignment"];

test.describe("directory and assignment", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(240_000);

  test("an incident in a category with a round-robin rule lands with the group's agent", async ({ page, browser }) => {
    const stamp = Date.now();
    const category = `E2E printers ${stamp}`;
    const groupName = `E2E print desk ${stamp}`;
    const ruleName = `E2E printers to print desk ${stamp}`;
    const title = `The label printer does not print ${stamp}`;

    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await expect(page).toHaveURL(/\/(en|ar)\/account$/);

    // Every new Settings page, in both languages.
    for (const locale of ["en", "ar"]) {
      for (const section of directoryPages) {
        await page.goto(`/${locale}/settings/${section}`);
        await expect(page.locator("main").getByRole("heading", { level: 2 }).first()).toBeVisible();
        await expectAccessible(page);
      }
    }
    await page.goto("/ar/settings/assignment");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");

    // A category.
    await page.goto("/en/settings/categories");
    await page.getByRole("button", { name: "Add a top-level category" }).click();
    const addCategory = page.getByRole("form", { name: "Add a top-level category" });
    await addCategory.getByLabel("In English", { exact: true }).fill(category);
    await addCategory.getByLabel("In Arabic", { exact: true }).fill(`طابعات ${stamp}`);
    await addCategory.getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("tree-categories").getByText(category, { exact: true })).toBeVisible();
    await expectAccessible(page);

    // A group with the development agent as its only member.
    await page.goto("/en/settings/groups");
    const addGroup = page.getByRole("form", { name: "Add group" });
    await addGroup.getByLabel("In English", { exact: true }).fill(groupName);
    await addGroup.getByRole("button", { name: "Add group" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/groups\/[0-9a-f-]{36}$/);
    await page.getByRole("searchbox", { name: "Find people to add" }).fill(devUser("agent"));
    await page.getByRole("button", { name: "Find", exact: true }).click();
    const person = page.getByLabel("Person", { exact: true });
    const agentId = await person.locator("option").nth(1).getAttribute("value");
    await person.selectOption(agentId!);
    await page.getByRole("button", { name: "Add to group" }).click();
    await expect(page.getByTestId("group-members").getByRole("row")).toHaveCount(2);
    await expectAccessible(page);

    // A round-robin rule for the category, tried before any ticket exists.
    await page.goto("/en/settings/assignment");
    await page.getByRole("button", { name: "Add a rule" }).click();
    const form = page.getByTestId("rule-form");
    await form.getByLabel("In English", { exact: true }).fill(ruleName);
    await form.getByLabel("Category", { exact: true }).selectOption({ label: category });
    await form.getByLabel("Group", { exact: true }).selectOption({ label: groupName });
    await form.getByLabel("How", { exact: true }).selectOption("RoundRobin");
    await form.getByRole("button", { name: "Save rule" }).click();
    await expect(page.getByTestId("assignment-rules")).toContainText(ruleName);
    await page.getByLabel("Category", { exact: true }).last().selectOption({ label: category });
    await page.getByRole("button", { name: "Try", exact: true }).click();
    await expect(page.getByTestId("rule-test-result")).toContainText(`${ruleName} sends it to ${groupName}`);
    await expectAccessible(page);

    // The requester reports an incident in the category.
    const requesterContext = await browser.newContext();
    const requester = await requesterContext.newPage();
    await signInWithPassword(requester, devUser("requester"));
    await requester.goto("/en/my-incidents/new");
    await requester.getByLabel("Title", { exact: true }).fill(title);
    await requester.getByLabel("Category", { exact: true }).selectOption({ label: category });
    await expectAccessible(requester);
    await requester.getByRole("button", { name: "Report incident" }).click();
    await expect(requester.getByRole("heading", { level: 1, name: title })).toBeVisible();
    await expect(requester.getByText(category, { exact: true }).first()).toBeVisible();
    await requesterContext.close();

    // The agent finds it assigned to them, in the group, routed by the rule.
    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await agent.goto("/en/incidents?assignee=me&open=true");
    await agent.getByRole("link", { name: title }).click();
    await expect(agent.getByText(groupName).first()).toBeVisible();
    await expect(agent.getByText(ruleName, { exact: true }).first()).toBeVisible();
    await expect(agent.getByText("Assigned", { exact: true }).first()).toBeVisible();
    await expect(agent.getByTestId("assignment-picker").getByLabel("Assignee", { exact: true })).toHaveValue(agentId!);
    await expectAccessible(agent);
    await agentContext.close();

    // Put things back: remove the rule, so later runs start from the same rules.
    await page.goto("/en/settings/assignment");
    await page.getByRole("button", { name: `Delete ${ruleName}` }).click();
    await expect(page.getByTestId("assignment-rules")).not.toContainText(ruleName);
  });
});
