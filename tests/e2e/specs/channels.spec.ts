import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { completeSecondFactor, devUser, signInWithPassword } from "../support/identity";

/**
 * Live chat and the ticket conversation (task E2, module 6): an administrator makes a group with the
 * agent and a portal chat widget; the requester chats on the portal; the agent accepts the chat on
 * the Chats page, answers and makes a ticket of it; the transcript is the incident's conversation in the workspace,
 * where the agent replies. Pages are checked for accessibility in English and Arabic. Runs in its
 * own project: it adds a widget shown on the portal.
 */

const wcag = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"];

async function expectAccessible(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(wcag).analyze();
  expect(results.violations).toEqual([]);
}

test.describe("live chat", () => {
  test.setTimeout(300_000);

  test("a requester chats on the portal, the agent makes a ticket of it and replies in its conversation", async ({ page, browser }) => {
    const stamp = Date.now();
    const groupName = `Chat desk ${stamp}`;

    // The administrator: a group with the agent, then a portal widget for it.
    await signInWithPassword(page, devUser("admin"));
    await completeSecondFactor(page, devUser("admin"));
    await page.goto("/en/settings/groups");
    const addGroup = page.getByRole("form", { name: "Add group" });
    await addGroup.getByLabel("In English", { exact: true }).fill(groupName);
    await addGroup.getByRole("button", { name: "Add group" }).click();
    await expect(page).toHaveURL(/\/en\/settings\/groups\/[0-9a-f-]{36}$/);
    await page.getByRole("searchbox", { name: "Find people to add" }).fill(devUser("agent"));
    await page.getByRole("button", { name: "Find", exact: true }).click();
    const person = page.getByLabel("Person", { exact: true });
    await person.selectOption((await person.locator("option").nth(1).getAttribute("value"))!);
    await page.getByRole("button", { name: "Add to group" }).click();
    await expect(page.getByTestId("group-members").getByRole("row")).toHaveCount(2);

    for (const locale of ["en", "ar"]) {
      await page.goto(`/${locale}/settings/chat`);
      await expect(page.getByTestId("chat-widgets-panel")).toBeVisible();
      await expectAccessible(page);
      await page.goto(`/${locale}/settings/mailboxes`);
      await expect(page.getByTestId("mailboxes-panel")).toBeVisible();
      await expectAccessible(page);
      // Task E3: WhatsApp, SMS gateways and the Teams app.
      await page.goto(`/${locale}/settings/messaging`);
      await expect(page.getByTestId("messaging-panel")).toBeVisible();
      await expectAccessible(page);
    }

    await page.goto("/en/settings/chat");
    if ((await page.getByRole("button", { name: "Add a chat widget" }).count()) > 0) {
      await page.getByRole("button", { name: "Add a chat widget" }).click();
    }
    const form = page.getByRole("form", { name: "Add a chat widget" });
    await form.getByLabel("Name (English)").fill(`Chat with us ${stamp}`);
    await form.getByLabel("Name (Arabic)").fill(`تحدّث معنا ${stamp}`);
    await form.getByLabel("Group that answers").selectOption({ label: groupName });
    await form.getByRole("button", { name: "Save widget" }).click();
    await expect(page.getByTestId("chat-widget").filter({ hasText: `Chat with us ${stamp}` })).toBeVisible();

    // The agent is available on the Chats page.
    const agentContext = await browser.newContext();
    const agent = await agentContext.newPage();
    await signInWithPassword(agent, devUser("agent"));
    await completeSecondFactor(agent, devUser("agent"));
    await agent.goto("/en/chats");
    await expect(agent.getByTestId("agent-chats")).toBeVisible();
    if ((await agent.getByRole("button", { name: "Set me as available" }).count()) > 0) {
      await agent.getByRole("button", { name: "Set me as available" }).click();
    }
    await expectAccessible(agent);

    // The requester chats from the portal as themselves.
    const requesterContext = await browser.newContext();
    const requester = await requesterContext.newPage();
    await signInWithPassword(requester, devUser("requester"));
    await requester.goto("/en/my-incidents");
    await requester.getByRole("button", { name: "Chat with us" }).click();
    const chat = requester.getByTestId("chat-window");
    await chat.getByLabel("How can we help?").fill(`My badge does not open the door ${stamp}`);
    await chat.getByRole("button", { name: "Start chat" }).click();
    await expect(chat.getByText("Waiting for someone from our team…")).toBeVisible();
    await expectAccessible(requester);

    // The agent accepts, answers and makes a ticket of the chat.
    const waiting = agent.getByTestId("chat-summary").filter({ hasText: `My badge does not open the door ${stamp}` });
    await expect(waiting).toBeVisible({ timeout: 30_000 });
    await waiting.getByRole("button", { name: /^Accept/ }).click();
    const panel = agent.getByTestId("chat-panel");
    await panel.getByLabel("Your answer").fill("Which door is it?");
    await panel.getByRole("button", { name: "Send" }).click();
    await expect(chat.getByText("Which door is it?")).toBeVisible({ timeout: 15_000 });
    await chat.getByPlaceholder("Write a message").fill("The main entrance.");
    await chat.getByRole("button", { name: "Send" }).click();
    await expect(panel.getByText("The main entrance.")).toBeVisible({ timeout: 15_000 });
    await panel.getByRole("button", { name: "Make a ticket" }).click();
    await panel.getByRole("link", { name: /^Ticket INC-\d{6,}$/ }).click();

    // The transcript is the incident's conversation in the workspace (B4); the agent replies there.
    const detail = agent.getByTestId("workspace-detail");
    await expect(detail.getByTestId("comment")).toHaveCount(3);
    await expect(detail.getByTestId("comment").first()).toContainText("From a chat");
    await agent.getByTestId("composer-body").fill("A new badge is ready at reception.");
    await agent.getByRole("button", { name: "Send reply" }).click();
    await expect(detail.getByTestId("comment")).toHaveCount(4);
    await expectAccessible(agent);

    await chat.getByRole("button", { name: "End chat" }).click();
    await expect(chat.getByText("The chat has ended.")).toBeVisible();
    await requester.goto("/ar/my-incidents");
    await expect(requester.locator("html")).toHaveAttribute("dir", "rtl");
    await expectAccessible(requester);

    await agentContext.close();
    await requesterContext.close();
  });
});
