import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * The agent workspace (task B4, module 5): a requester reports an incident; an agent finds it in
 * the workspace, replies and writes an internal note; the requester's answer reaches the agent's
 * screen through live updates without reloading; a bulk action and the keyboard shortcuts work;
 * the screen is accessible in English and Arabic.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("agent workspace", () => {
  // Sign-ins with a second factor share one authenticator secret per user; run on one project.
  test.beforeEach(() => test.skip(test.info().project.name !== "desktop", "Runs once, on the desktop project."));
  test.setTimeout(300_000);

  test("an agent works an incident in one screen with live updates", async ({ page, browser }) => {
    const title = `VPN drops every hour ${Date.now()}`;

    // The requester reports the incident.
    await signInWithPassword(page, devUser("requester"));
    await page.goto("/en/my-incidents/new");
    await page.getByLabel("Title").fill(title);
    await page.getByLabel("What happened?").fill("The VPN disconnects at the start of every hour.");
    await page.getByRole("button", { name: "Report incident" }).click();
    await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
    const requesterUrl = page.url();

    // The agent opens the workspace with every incident, newest first, and opens the new one.
    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await agent.goto("/en/workspace?sort=created");
    await expect(agent.getByRole("heading", { level: 1, name: "Workspace" })).toBeVisible();
    await expect(agent.getByTestId("live-status")).toHaveText(/Live updates on/, { timeout: 30_000 });
    await expectAccessible(agent);
    await agent.getByTestId("workspace-list").getByRole("link", { name: new RegExp(title) }).click();
    const detail = agent.getByTestId("workspace-detail");
    await expect(detail.getByRole("heading", { name: title })).toBeVisible();
    await expect(agent.getByRole("heading", { name: "Customer" })).toBeVisible();
    await expectAccessible(agent);

    // A public reply and, with the "n" shortcut, an internal note.
    await agent.getByTestId("composer-body").fill("Which VPN client version do you use?");
    await agent.getByRole("button", { name: "Send reply" }).click();
    await expect(detail.getByTestId("comment").filter({ hasText: "Which VPN client version" })).toContainText("Public reply");
    await agent.locator("body").click({ position: { x: 1, y: 1 } });
    await agent.keyboard.press("n");
    await expect(agent.getByTestId("composer-body")).toBeFocused();
    await agent.keyboard.type("Probably the client update from last week.");
    await agent.getByRole("button", { name: "Add note" }).click();
    await expect(detail.getByTestId("comment").filter({ hasText: "client update" })).toContainText("Internal note");

    // The requester sees the reply but not the note, and answers in Arabic.
    await page.goto(requesterUrl);
    await expect(page.getByTestId("comment").filter({ hasText: "Which VPN client version" })).toBeVisible();
    await expect(page.getByText("Probably the client update")).toHaveCount(0);
    await page.getByTestId("composer-body").fill("الإصدار 6.2");
    await page.getByRole("button", { name: "Send reply" }).click();
    await expect(page.getByTestId("comment").filter({ hasText: "الإصدار 6.2" })).toBeVisible();

    // The agent's screen shows the answer without a reload.
    await expect(detail.getByTestId("comment").filter({ hasText: "الإصدار 6.2" })).toBeVisible({ timeout: 30_000 });

    // A bulk action on the selected incident: assign it to the agent.
    await agent.getByLabel(/^Select INC-/).first().check();
    const bulk = agent.getByTestId("bulk-bar");
    await expect(bulk).toContainText("1 incident selected");
    await bulk.getByLabel("Action").selectOption("Assign");
    // The second list of the bulk bar is the agent (the first is the action).
    await bulk.getByRole("combobox").nth(1).selectOption({ index: 1 });
    await bulk.getByRole("button", { name: "Apply" }).click();
    await expect(bulk).toContainText("1 changed, 0 left as they were.");

    // Keyboard shortcuts are listed in a dialog.
    await agent.locator("body").click({ position: { x: 1, y: 1 } });
    await agent.keyboard.press("?");
    await expect(agent.getByRole("dialog", { name: "Keyboard shortcuts" })).toBeVisible();
    await agent.keyboard.press("Escape");

    // The same screen in Arabic, right to left.
    const id = new URL(agent.url()).pathname.split("/").pop()!;
    await agent.goto(`/ar/workspace/${id}?sort=created`);
    await expect(agent.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(agent.getByRole("heading", { level: 1, name: "مساحة العمل" })).toBeVisible();
    await expectAccessible(agent);
    await agentContext.close();
  });

  test("canned responses are kept in English and Arabic in Settings", async ({ page }) => {
    await signInWithPassword(page, devUser("agent"));
    await completeSecondFactor(page, devUser("agent"));
    await page.goto("/en/settings/canned-responses");
    await page.getByRole("button", { name: "New response" }).click();
    const form = page.getByRole("form", { name: "New canned response" });
    const name = `Check the cable ${Date.now()}`;
    await form.getByLabel("Title").first().fill(name);
    await form.getByLabel("Wording").first().fill("Hello {{requester.name}}, please check the network cable.");
    await form.getByLabel("Title").nth(1).fill("فحص الكابل");
    await form.getByLabel("Wording").nth(1).fill("السيد/السيدة {{requester.name}}، نرجو التحقق من كابل الشبكة.");
    await expectAccessible(page);
    await form.getByRole("button", { name: "Save" }).click();
    await expect(page.getByText("The response is saved.")).toBeVisible();
    await expect(page.getByTestId("canned-list")).toContainText(name);
    await page.goto("/ar/settings/canned-responses");
    await expect(page.getByTestId("canned-list")).toContainText("فحص الكابل");
    await expectAccessible(page);
  });
});
